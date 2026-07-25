using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// Loads/saves <see cref="Playlist"/> files as JSON under %APPDATA%\WaBiBaBuSy\playlists\.
/// Mirrors the logging-config persistence location convention.
/// </summary>
public class PlaylistStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Directory holding playlist JSON files (created on demand).</summary>
    public string Directory { get; }

    public PlaylistStore(string? directoryOverride = null)
    {
        Directory = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WaBiBaBuSy", "playlists");
    }

    private void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "playlist" : name;
    }

    /// <summary>Full path for a playlist name (does not create the file).</summary>
    public string PathFor(string name) => Path.Combine(Directory, Sanitize(name) + ".json");

    /// <summary>Save a playlist to <see cref="PathFor"/>(playlist.Name).</summary>
    public async Task SaveAsync(Playlist playlist)
    {
        EnsureDirectory();
        var json = JsonSerializer.Serialize(playlist, Options);
        await File.WriteAllTextAsync(PathFor(playlist.Name), json);
    }

    /// <summary>Load a playlist from an absolute file path.</summary>
    public async Task<Playlist> LoadAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<Playlist>(json)
               ?? throw new InvalidDataException($"Playlist file was empty or invalid: {filePath}");
    }

    /// <summary>Enumerate saved playlist file paths (newest first). Empty if the directory is absent.</summary>
    public IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<string>();
        return System.IO.Directory.GetFiles(Directory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>Delete a playlist by name. No-op if it does not exist.</summary>
    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }
}
