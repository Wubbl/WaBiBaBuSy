using System.Text.Json;

namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// Free-text tags per room node ("BiBaBu", "Guests"), used to aim show events at a group of seats. Keyed by node id
/// (clients keep a persistent id; local monitor ids are stable). Server-side JSON (<c>%APPDATA%\WaBiBaBuSy\tags.json</c>).
/// UI thread only.
/// </summary>
public sealed class NodeTagStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "tags.json");

    private readonly string _path;
    private readonly Dictionary<string, List<string>> _tags;

    private NodeTagStore(string path, Dictionary<string, List<string>> tags)
    {
        _path = path;
        _tags = tags;
    }

    /// <summary>Load from disk; a missing or unreadable file yields an empty store.</summary>
    public static NodeTagStore Load(string? path = null)
    {
        path ??= DefaultPath;
        var tags = new Dictionary<string, List<string>>();
        try
        {
            if (File.Exists(path))
                tags = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path), JsonOptions) ?? tags;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NodeTagStore] Could not read {path}: {ex.Message}. Starting empty.");
        }
        return new NodeTagStore(path, tags);
    }

    /// <summary>Tags of one node (empty when none).</summary>
    public IReadOnlyList<string> Of(string nodeId) => _tags.TryGetValue(nodeId, out var t) ? t : Array.Empty<string>();

    /// <summary>Every tag in use, sorted case-insensitively.</summary>
    public IReadOnlyList<string> All() =>
        _tags.Values.SelectMany(t => t).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Node ids carrying <paramref name="tag"/> (case-insensitive).</summary>
    public IReadOnlyList<string> NodesWith(string tag) =>
        _tags.Where(kv => kv.Value.Contains(tag, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();

    /// <summary>Replace the tags of <paramref name="nodeIds"/> with <paramref name="tags"/> (trimmed, deduped; empty clears) and save.</summary>
    public void Set(IEnumerable<string> nodeIds, IEnumerable<string> tags)
    {
        var clean = Parse(string.Join(",", tags));
        foreach (var id in nodeIds)
        {
            if (clean.Count == 0) _tags.Remove(id);
            else _tags[id] = clean.ToList();
        }
        Save();
    }

    /// <summary>"BiBaBu, guests ,BiBaBu" → ["BiBaBu", "guests"].</summary>
    public static List<string> Parse(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_tags, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NodeTagStore] Could not write {_path}: {ex.Message}");
        }
    }
}
