using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// First run: imports the starter presets listed in <c>starter-presets.json</c> (the shipped showcase scenes)
/// into an empty preset folder. Assets are copied, so the presets do not depend on the install folder.
/// </summary>
public static class PresetSeeder
{
    /// <summary>Manifest file in the starter folder: <c>{ "presets": [ { "name": …, "scene": … } ] }</c>.</summary>
    public const string ManifestFileName = "starter-presets.json";

    private sealed class Manifest
    {
        public List<Entry> Presets { get; set; } = new();
    }

    private sealed class Entry
    {
        public string Name { get; set; } = string.Empty;
        public string Scene { get; set; } = string.Empty;
    }

    /// <summary>
    /// When the store holds no preset, import every manifest entry of <paramref name="starterDir"/>. A bad entry is
    /// logged and skipped; a missing or unreadable manifest seeds nothing. Returns the number of presets seeded.
    /// </summary>
    public static async Task<int> SeedIfEmptyAsync(PresetStore store, string starterDir, ILogger? logger = null)
    {
        if (store.List().Count > 0) return 0;

        var manifestPath = Path.Combine(starterDir, ManifestFileName);
        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath), SceneFile.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning("Starter presets not seeded: {Manifest}: {Message}", manifestPath, ex.Message);
            return 0;
        }

        int seeded = 0;
        foreach (var entry in manifest?.Presets ?? new List<Entry>())
        {
            try
            {
                await store.ImportAsync(Path.Combine(starterDir, entry.Scene), entry.Name);
                seeded++;
            }
            catch (Exception ex) when (ex is ScenarioException or IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning("Starter preset '{Name}' skipped: {Message}", entry.Name, ex.Message);
            }
        }
        return seeded;
    }
}
