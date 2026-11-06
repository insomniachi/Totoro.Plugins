using System.Buffers.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Totoro.Plugins.Gotaku;

/// <summary>
/// Gotaku's encryption (after the Android extension at github.com/yuzono/anime-extensions, src/en/gotaku, Crypto.kt):
/// <list type="number">
/// <item>API answers to <c>?k={version}</c>: [version byte][12-byte IV][AES-GCM ciphertext and tag] under
/// HKDF-SHA256(API seed, no salt, "api-seal|v1") (see <see cref="GotakuKeys"/>); the plaintext is zlib'd JSON.</item>
/// <item>A manifest request: base64url(IV, AES-GCM of {"nonce","stamp"}) under the manifest seed, in the URL's path.</item>
/// <item>Its answer: base64url(IV, ciphertext) under HKDF(manifest seed, salt = the request's nonce, "manifest-resp|v1").</item>
/// <item>Playlists: [IV][ciphertext] under HKDF(the manifest's keySeed, salt = its URL's token, "pl|v1").</item>
/// <item>Segments: their first <c>obf.segmentBytes</c> bytes XORed with AES-CTR under keySeed, the counter from a hash of their URL.</item>
/// </list>
/// </summary>
internal static class GotakuCrypto
{
	private static readonly byte[] s_manifestSeed = Convert.FromHexString("458e5403c40a73332706df406193dd6b040a2ea97a6bb59b52b294a61006e6ef");

	private const int MaxInflatedBytes = 4 * 1024 * 1024;

	/// <summary>An API answer's JSON; null when it isn't one (another build's, say).</summary>
	public static string? DecryptApi(byte[] body, GotakuKeys keys)
	{
		if (body.Length <= 29 || body[0] != byte.Parse(keys.Version) || GcmDecrypt(keys.ApiKey, body.AsSpan(1)) is not { } compressed)
		{
			return null;
		}

		using var inflater = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
		using var json = new MemoryStream();
		var buffer = new byte[8192];
		for (int read; (read = inflater.Read(buffer)) > 0;)
		{
			if (json.Length + read > MaxInflatedBytes)
			{
				return null;
			}

			json.Write(buffer, 0, read);
		}

		return Encoding.UTF8.GetString(json.ToArray());
	}

	/// <summary>One manifest exchange: its nonce salts the answer's key, so the request and answer share an instance.</summary>
	public sealed class ManifestHandshake(string stamp)
	{
		private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(16);

		/// <summary>The path to GET under the manifest base.</summary>
		public string RequestPath()
		{
			// Compact, in this order: what the site's JSON.stringify makes.
			var payload = JsonSerializer.SerializeToUtf8Bytes(new { nonce = Base64Url.EncodeToString(_nonce), stamp });
			return Base64Url.EncodeToString(GcmEncrypt(s_manifestSeed, payload));
		}

		public string? DecryptResponse(string body)
		{
			var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, s_manifestSeed, 32, _nonce, "manifest-resp|v1"u8.ToArray());
			return TryBase64Url(body) is { } blob && GcmDecrypt(key, blob) is { } plain ? Encoding.UTF8.GetString(plain) : null;
		}
	}

	/// <summary>A playlist's text; null if it doesn't decrypt.</summary>
	public static string? DecryptPlaylist(byte[] body, byte[] keySeed, Uri url)
	{
		var token = System.Web.HttpUtility.ParseQueryString(url.Query)["token"]
			?? url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(3) ?? "";
		var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, keySeed, 32, Encoding.UTF8.GetBytes(token), "pl|v1"u8.ToArray());
		return GcmDecrypt(key, body) is { } plain ? Encoding.UTF8.GetString(plain) : null;
	}

	/// <summary>
	/// Decrypts a segment's first <paramref name="length"/> bytes in place. The site hashes <c>url.split("/").drop(2)</c>,
	/// which is either the whole URL past its host or its path past two folders: whichever makes media is the one.
	/// </summary>
	public static void DecryptSegment(byte[] data, byte[] keySeed, string url, int length)
	{
		if (length <= 0 || LooksLikeMedia(data))
		{
			return;
		}

		var n = Math.Min(length, data.Length);
		byte[]? first = null;
		foreach (var fullUrl in new[] { true, false })
		{
			var attempt = data.ToArray();
			XorKeystream(attempt.AsSpan(0, n), keySeed, Counter(url, fullUrl));
			if (LooksLikeMedia(attempt))
			{
				attempt.CopyTo(data, 0);
				return;
			}

			first ??= attempt;
		}

		first!.CopyTo(data, 0);
	}

	/// <summary>The CTR counter block: the first 8 bytes of SHA-256 of the URL's id (without its extension), then a zero block number.</summary>
	internal static byte[] Counter(string url, bool fullUrl)
	{
		var parts = fullUrl ? url.Split('/', StringSplitOptions.RemoveEmptyEntries) : new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var id = string.Join('/', parts.Skip(2));
		var dot = id.LastIndexOf('.');
		id = dot >= 0 ? id[..dot] : id;
		return [.. SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, 8), .. new byte[8]];
	}

	/// <summary>AES-CTR (a big-endian counter in the block's last 8 bytes, as Java's) XORed over <paramref name="data"/>.</summary>
	internal static void XorKeystream(Span<byte> data, byte[] key, byte[] counter)
	{
		using var aes = Aes.Create();
		aes.Key = key;
		var block = counter.ToArray();
		Span<byte> stream = stackalloc byte[16];
		var low = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(8));
		for (var offset = 0; offset < data.Length; offset += 16)
		{
			aes.EncryptEcb(block, stream, PaddingMode.None);
			for (var i = 0; i < 16 && offset + i < data.Length; i++)
			{
				data[offset + i] ^= stream[i];
			}

			System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(block.AsSpan(8), ++low);
		}
	}

	/// <summary>MPEG-TS (a sync byte at every 188-byte packet in the first few) or fragmented MP4 (a box at the start).</summary>
	internal static bool LooksLikeMedia(ReadOnlySpan<byte> data)
	{
		if (data.Length > 0 && data[0] == 0x47)
		{
			for (var i = 188; i < Math.Min(data.Length, 188 * 4); i += 188)
			{
				if (data[i] != 0x47)
				{
					return false;
				}
			}

			return true;
		}

		return data.Length >= 8 && (data[4..8].SequenceEqual("ftyp"u8) || data[4..8].SequenceEqual("styp"u8) || data[4..8].SequenceEqual("moof"u8));
	}

	public static byte[]? TryBase64Url(string text)
	{
		try
		{
			return Base64Url.DecodeFromChars(text.Trim().TrimEnd('=').Replace('+', '-').Replace('/', '_'));
		}
		catch (FormatException)
		{
			return null;
		}
	}

	/// <summary>[12-byte IV][ciphertext][16-byte tag], as WebCrypto makes them; null when it doesn't authenticate.</summary>
	private static byte[]? GcmDecrypt(byte[] key, ReadOnlySpan<byte> blob)
	{
		if (blob.Length < 28)
		{
			return null;
		}

		var plain = new byte[blob.Length - 28];
		try
		{
			using var gcm = new AesGcm(key, 16);
			gcm.Decrypt(blob[..12], blob[12..^16], blob[^16..], plain);
			return plain;
		}
		catch (CryptographicException)
		{
			return null;
		}
	}

	private static byte[] GcmEncrypt(byte[] key, byte[] plain)
	{
		var blob = new byte[12 + plain.Length + 16];
		RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
		using var gcm = new AesGcm(key, 16);
		gcm.Encrypt(blob.AsSpan(0, 12), plain, blob.AsSpan(12, plain.Length), blob.AsSpan(12 + plain.Length));
		return blob;
	}
}
