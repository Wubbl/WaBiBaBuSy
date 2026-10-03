using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// Scene presets under %APPDATA%\WaBiBaBuSy\presets\, one <see cref="SceneFile"/> per preset: a showcase scene is
/// a valid preset and an exported preset a valid scene. Targets are never stored (they belong to the room).
/// Saved presets reference gallery files in place; imported ones carry their assets in <c>presets\assets\</c>,
/// referenced relative to the preset, so the folder can be copied to another server.
/// </summary>
public sealed class PresetStore
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Directory holding the preset files (created on demand).</summary>
    public string Directory { get; }

    /// <summary>A preset was saved, imported or deleted. Raised on the calling thread.</summary>
    public event EventHandler? Changed;

    public PresetStore(string? directoryOverride = null)
    {
        Directory = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WaBiBaBuSy", "presets");
    }

    /// <summary>A file stem for <paramref name="name"/>: invalid characters → '_', no trailing dots, no device names.</summary>
    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.').Trim();
        if (name.Length == 0) return "preset";
        return ReservedNames.Contains(name) ? name + "_" : name;
    }

    /// <summary>Full path for a preset name (does not create the file).</summary>
    public string PathFor(string name) => Path.Combine(Directory, Sanitize(name) + ".json");

    /// <summary>True when a preset with this name exists.</summary>
    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary><paramref name="name"/> if free, else "name (2)", "name (3)", …</summary>
    public string UniqueName(string name)
    {
        var stem = Sanitize(name);
        if (!Exists(stem)) return stem;
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem} ({i})";
            if (!Exists(candidate)) return candidate;
        }
    }

    /// <summary>Preset names, alphabetical. Empty when the folder does not exist.</summary>
    public IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<string>();
        return System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Load a preset; asset paths come back absolute. Throws <see cref="ScenarioException"/> for a bad file.</summary>
    public Task<CrossScreenConfig> LoadAsync(string name) => Task.Run(() => SceneFile.Load(PathFor(name)));

    /// <summary>Save the editor's scene: gallery assets stay referenced in place, targets are dropped.</summary>
    public Task<string> SaveAsync(string name, CrossScreenConfig config)
    {
        var copy = SceneFile.Clone(config);
        copy.SelectedMonitorIds = new List<string>();
        PresetBundle.RelativizeUnder(copy, Directory);
        return WriteAsync(name, copy);
    }

    /// <summary>
    /// Import a scene / exported preset file: its assets are copied into <c>presets\assets\</c>. The name defaults
    /// to a free variant of the file name; an explicit name overwrites. Throws <see cref="ScenarioException"/>
    /// (bad JSON, missing asset, UNC path) or <see cref="IOException"/>.
    /// </summary>
    public async Task<string> ImportAsync(string sourceFile, string? name = null)
    {
        var config = await Task.Run(() => SceneFile.Load(sourceFile));
        config.SelectedMonitorIds = new List<string>();
        await PresetBundle.BundleAssetsAsync(config, Directory);
        return await WriteAsync(name ?? UniqueName(Path.GetFileNameWithoutExtension(sourceFile)), config);
    }

    /// <summary>Export a scene to <paramref name="targetFile"/> with its assets in an <c>assets\</c> folder next to it.</summary>
    public static async Task ExportAsync(CrossScreenConfig config, string targetFile)
    {
        var copy = SceneFile.Clone(config);
        copy.SelectedMonitorIds = new List<string>();
        var full = Path.GetFullPath(targetFile);
        await PresetBundle.BundleAssetsAsync(copy, Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, SceneFile.ToJson(copy));
    }

    /// <summary>Delete a preset (its assets stay: other presets may share them). No-op if missing.</summary>
    public void Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return;
        File.Delete(path);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> WriteAsync(string name, CrossScreenConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var stem = Sanitize(name);
        await File.WriteAllTextAsync(PathFor(stem), SceneFile.ToJson(config));
        Changed?.Invoke(this, EventArgs.Empty);
        return stem;
    }
}
