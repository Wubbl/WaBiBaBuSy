using System.Text.Json;
using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>Loads a <see cref="CrossScreenConfig"/> JSON used by a scenario.</summary>
public static class SceneFile
{
    /// <summary>Same shape as the configs sent to clients (PascalCase), plus enum names and comments.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Load a scene; the animation, additional image and background image paths become absolute
    /// against the scene file's folder. Throws <see cref="ScenarioException"/> for bad JSON or a missing asset.
    /// </summary>
    public static CrossScreenConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        CrossScreenConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<CrossScreenConfig>(File.ReadAllText(full), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ScenarioException($"{full}: invalid scene JSON — {ex.Message}");
        }
        if (config == null) throw new ScenarioException($"{full}: empty scene");

        var dir = Path.GetDirectoryName(full)!;
        config.Animation.AnimationPath = Absolute(dir, config.Animation.AnimationPath);
        config.Animation.AdditionalAnimationPaths = config.Animation.AdditionalAnimationPaths.Select(p => Absolute(dir, p)).ToList();
        if (!string.IsNullOrEmpty(config.Background.ImagePath))
            config.Background.ImagePath = Absolute(dir, config.Background.ImagePath);

        foreach (var (asset, _) in SceneAssets.CollectPaths(config))
            if (!string.IsNullOrWhiteSpace(asset) && !File.Exists(asset))
                throw new ScenarioException($"{full}: asset not found: {asset}");
        return config;
    }

    private static string Absolute(string dir, string path) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(dir, path));
}
