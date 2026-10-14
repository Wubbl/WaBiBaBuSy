using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Reads pre-layers scene JSON (one <c>Animation</c> + <c>Movement</c> + <c>DistributionMode</c>) into
/// <see cref="CrossScreenConfig.Layers"/>[0]. Runs after every deserialization of a scene (presets,
/// playlists, scene files, clones), so old files load forever and are written back in the new shape.
/// </summary>
public static class LegacySceneMigration
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Build layer 0 from legacy keys when the scene has no layers; always drop the legacy keys.</summary>
    public static void Apply(CrossScreenConfig config)
    {
        var legacy = config.Legacy;
        config.Legacy = null;
        config.Layers ??= new();   // "Layers": null = no layers
        if (legacy == null || legacy.Count == 0 || config.Layers.Count > 0) return;

        var animation = Read<AnimationLayerConfig>(legacy, "Animation");
        var movement = Read<MovementConfig>(legacy, "Movement");
        if (animation == null && movement == null) return;

        var layer = new SceneLayer
        {
            Animation = animation ?? new AnimationLayerConfig(),
            Movement = movement ?? new MovementConfig(),
            Distribution = Read<AnimationDistributionMode?>(legacy, "DistributionMode") ?? AnimationDistributionMode.Sequential,
        };
        var name = Path.GetFileNameWithoutExtension(layer.Animation.AnimationPath);
        if (!string.IsNullOrWhiteSpace(name)) layer.Name = name;
        config.Layers.Add(layer);
    }

    private static T? Read<T>(Dictionary<string, JsonElement> legacy, string key)
    {
        foreach (var (k, v) in legacy)
        {
            if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (v.ValueKind == JsonValueKind.Null) return default;
            return v.Deserialize<T>(Options);
        }
        return default;
    }
}
