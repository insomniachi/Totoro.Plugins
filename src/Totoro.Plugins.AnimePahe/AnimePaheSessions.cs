using System.Text.Json;
using Totoro.Core.Helpers;

namespace Totoro.Plugins.AnimePahe;

/// <summary>
/// AnimePahe's address for each show: its pages and API go by a "session" id that changes every few days, while the
/// show's id (the one shows are saved with, see <see cref="AnimePaheProvider"/>) doesn't. So the latest session of
/// each id is kept, with the title to search for when it's stale. Kept in the module's folder, across runs.
/// </summary>
public sealed class AnimePaheSessions
{
	private readonly string _path = FileHelper.GetModulePath(AnimePaheModule.Descriptor, "sessions.json");
	private readonly Lock _lock = new();
	private Dictionary<string, Entry>? _entries;

	/// <param name="Session">The show's session id, as of when it was found.</param>
	public sealed record Entry(string Session, string Title);

	public Entry? Get(string id)
	{
		lock (_lock)
		{
			return Load().GetValueOrDefault(id);
		}
	}

	/// <summary>Keeps each show's session (e.g. a search's results), saving once if any changed.</summary>
	public void Set(IEnumerable<(string Id, string Session, string Title)> shows)
	{
		lock (_lock)
		{
			var entries = Load();
			var changed = false;
			foreach (var (id, session, title) in shows)
			{
				var entry = new Entry(session, title);
				if (entries.GetValueOrDefault(id) != entry)
				{
					entries[id] = entry;
					changed = true;
				}
			}

			if (!changed)
			{
				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
			File.WriteAllText(_path, JsonSerializer.Serialize(entries));
		}
	}

	private Dictionary<string, Entry> Load()
	{
		if (_entries is null)
		{
			try
			{
				_entries = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) : null;
			}
			catch (JsonException)
			{
				// Only a cache: start over.
			}

			_entries ??= [];
		}

		return _entries;
	}
}
