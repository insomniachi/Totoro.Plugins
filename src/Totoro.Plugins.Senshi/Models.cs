using System.Text.Json.Serialization;
using Totoro.Core.Modules.Media;

namespace Totoro.Plugins.Senshi;

public class SenshiItem
{
	[JsonPropertyName("id")] public int InternalId { get; set; }
	[JsonPropertyName("public_id")] public string Id { get; set; } = string.Empty;
	[JsonPropertyName("anime_picture")] public string? Image { get; set; }
	[JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
	[JsonPropertyName("anilist_id")] public long? AnilistId { get; set; }
}

public class SenshiSearchResponse
{
	[JsonPropertyName("data")] public List<SenshiItem> Items { get; set; } = [];
}

public class SenshiEpisode
{
	[JsonPropertyName("ep_id")] public float Episode { get; set; }
	[JsonPropertyName("ep_title")] public string Title { get; set; } = string.Empty;
}

public class SenshiStream
{
	[JsonPropertyName("url")] public string StreamUrl { get; set; } = string.Empty;
	[JsonPropertyName("status")] public string Type { get; set; } = string.Empty;
	[JsonPropertyName("intro_start_ms")] public long? IntroStart { get; set; }
	[JsonPropertyName("intro_end_ms")] public long? IntroEnd { get; set; }
	[JsonPropertyName("outro_start_ms")] public long? OutroStart { get; set; }
	[JsonPropertyName("outro_end_ms")] public long? OutroEnd { get; set; }

	/// <summary>The stream's opening and ending, those Senshi knows (often neither).</summary>
	public List<MediaSegment> Segments() =>
	[
		.. Segment(MediaSegmentType.Opening, IntroStart, IntroEnd),
		.. Segment(MediaSegmentType.Ending, OutroStart, OutroEnd)
	];

	private static IEnumerable<MediaSegment> Segment(MediaSegmentType type, long? start, long? end) =>
		start is { } s && end is { } e && e > s ? [new(type, TimeSpan.FromMilliseconds(s), TimeSpan.FromMilliseconds(e))] : [];
}
