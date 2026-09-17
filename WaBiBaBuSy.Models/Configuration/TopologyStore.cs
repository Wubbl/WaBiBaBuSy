using System.Text.Json;

namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// One node's place in the wall: its traversal order and bezel distance to the previous node.
/// Bound primarily by the client's persistent <see cref="ClientId"/>; <see cref="Hostname"/> is
/// the fallback so a re-imaged machine (new id, same name) takes over its old seat.
/// </summary>
public class TopologyEntry
{
    public string ClientId { get; set; } = string.Empty;
    public string? Hostname { get; set; }
    public int OrderPosition { get; set; }
    public int PhysicalDistanceCm { get; set; }
}

/// <summary>
/// Server-side persistence of the topology (order + distances), so a server restart or a
/// client reconnect does not scramble a 20-machine wall. JSON file next to the other configs
/// (<c>%APPDATA%\WaBiBaBuSy\topology.json</c>). Not thread-safe by itself — the gRPC service
/// serializes access with its own lock.
/// </summary>
public class TopologyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WaBiBaBuSy", "topology.json");

    private readonly string _path;
    private readonly List<TopologyEntry> _entries;

    private TopologyStore(string path, List<TopologyEntry> entries)
    {
        _path = path;
        _entries = entries;
    }

    public IReadOnlyList<TopologyEntry> Entries => _entries;

    /// <summary>Load the store from disk; a missing or unreadable file yields an empty store.</summary>
    public static TopologyStore Load(string? path = null)
    {
        path ??= DefaultPath;
        var entries = new List<TopologyEntry>();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<List<TopologyEntry>>(json, JsonOptions);
                if (loaded != null)
                    entries = loaded.Where(e => !string.IsNullOrEmpty(e.ClientId)).ToList();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TopologyStore] Could not read {path}: {ex.Message}. Starting empty.");
        }
        return new TopologyStore(path, entries);
    }

    /// <summary>Write the store to disk. Failures are reported, never thrown — topology is a convenience.</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TopologyStore] Could not write {_path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Find the entry for a registering client. Exact <paramref name="clientId"/> match first;
    /// otherwise the first entry with the same hostname (case-insensitive) is re-bound to the new
    /// id and returned. Null when the machine is unknown.
    /// </summary>
    public TopologyEntry? Resolve(string clientId, string? hostname)
    {
        var byId = _entries.FirstOrDefault(e => e.ClientId == clientId);
        if (byId != null) return byId;

        if (string.IsNullOrWhiteSpace(hostname)) return null;
        var byHost = _entries.FirstOrDefault(e =>
            string.Equals(e.Hostname, hostname, StringComparison.OrdinalIgnoreCase));
        if (byHost == null) return null;

        byHost.ClientId = clientId;
        return byHost;
    }

    /// <summary>Insert or replace the entry with the same ClientId.</summary>
    public void Upsert(TopologyEntry entry)
    {
        var idx = _entries.FindIndex(e => e.ClientId == entry.ClientId);
        if (idx >= 0) _entries[idx] = entry;
        else _entries.Add(entry);
    }

    public void SetOrder(string clientId, string? hostname, int orderPosition)
    {
        var e = GetOrCreate(clientId, hostname);
        e.OrderPosition = orderPosition;
    }

    public void SetDistance(string clientId, string? hostname, int distanceCm)
    {
        var e = GetOrCreate(clientId, hostname);
        e.PhysicalDistanceCm = distanceCm;
    }

    /// <summary>One above the highest stored order, so a brand-new machine lands at the end of the chain.</summary>
    public int NextFreeOrder() => _entries.Count == 0 ? 1 : _entries.Max(e => e.OrderPosition) + 1;

    private TopologyEntry GetOrCreate(string clientId, string? hostname)
    {
        var e = _entries.FirstOrDefault(x => x.ClientId == clientId);
        if (e == null)
        {
            e = new TopologyEntry { ClientId = clientId, Hostname = hostname, OrderPosition = NextFreeOrder() };
            _entries.Add(e);
        }
        else if (!string.IsNullOrWhiteSpace(hostname))
        {
            e.Hostname = hostname;
        }
        return e;
    }
}
