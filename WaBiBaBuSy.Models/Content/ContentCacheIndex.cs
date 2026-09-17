using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WaBiBaBuSy.Models.Content;

/// <summary>
/// Persisted map contentId → local file path for the client's download cache
/// (<c>cache-index.json</c> inside the cache directory). Without it every client restart
/// re-downloaded every file although it was still on disk.
/// </summary>
public static class ContentCacheIndex
{
    public const string FileName = "cache-index.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Load the index; entries whose file vanished are dropped. Missing/corrupt file → empty.</summary>
    public static Dictionary<string, string> Load(string cacheDirectory)
    {
        var path = Path.Combine(cacheDirectory, FileName);
        try
        {
            if (!File.Exists(path)) return new();
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions) ?? new();
            return map.Where(kv => !string.IsNullOrEmpty(kv.Value) && File.Exists(kv.Value))
                      .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ContentCacheIndex] Could not read {path}: {ex.Message}");
            return new();
        }
    }

    public static void Save(string cacheDirectory, IReadOnlyDictionary<string, string> entries)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var path = Path.Combine(cacheDirectory, FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ContentCacheIndex] Could not write index: {ex.Message}");
        }
    }
}
