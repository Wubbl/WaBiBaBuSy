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
    /// against the scene file's folder. Throws <see cref="ScenarioException"/> for bad JSON, a missing asset,
    /// or a UNC / device scene or asset path (rejected before any file system access).
    /// </summary>
    public static CrossScreenConfig Load(string path)
    {
        if (ScenarioLoader.IsRemoteOrDevicePath(path)) throw new ScenarioException($"{path}: scene {ScenarioLoader.LocalOnly}");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ScenarioException($"{path}: invalid scene path — {ex.Message}");
        }
        if (ScenarioLoader.IsRemoteOrDevicePath(full)) throw new ScenarioException($"{full}: scene {ScenarioLoader.LocalOnly}");
        if (!File.Exists(full)) throw new ScenarioException($"{full}: scene file not found");
        CrossScreenConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<CrossScreenConfig>(File.ReadAllText(full), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ScenarioException($"{full}: invalid scene JSON — {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ScenarioException($"{full}: cannot read the scene — {ex.Message}");
        }
        if (config == null) throw new ScenarioException($"{full}: empty scene");
        // JSON "null" overrides the (non-nullable) defaults: report it instead of crashing later.
        var nulls = new List<string>();
        if (IsNull(config.Animation)) nulls.Add("Animation");
        else if (IsNull(config.Animation.AdditionalAnimationPaths)) nulls.Add("Animation.AdditionalAnimationPaths");
        if (IsNull(config.Background)) nulls.Add("Background");
        if (IsNull(config.Movement)) nulls.Add("Movement");
        if (nulls.Count > 0) throw new ScenarioException($"{full}: {string.Join(", ", nulls.Select(n => n + " is null"))}");

        var dir = Path.GetDirectoryName(full)!;
        try
        {
            config.Animation.AnimationPath = Absolute(dir, config.Animation.AnimationPath);
            config.Animation.AdditionalAnimationPaths = config.Animation.AdditionalAnimationPaths.Select(p => Absolute(dir, p)).ToList();
            if (!string.IsNullOrEmpty(config.Background.ImagePath))
                config.Background.ImagePath = Absolute(dir, config.Background.ImagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ScenarioException($"{full}: invalid asset path — {ex.Message}");
        }

        var assets = SceneAssets.CollectPaths(config);
        // All remote checks before the first File.Exists: probing a UNC path would authenticate to the share.
        foreach (var (asset, _) in assets)
            if (ScenarioLoader.IsRemoteOrDevicePath(asset))
                throw new ScenarioException($"{full}: asset {ScenarioLoader.LocalOnly}: {asset}");
        foreach (var (asset, _) in assets)
            if (!string.IsNullOrWhiteSpace(asset) && !File.Exists(asset))
                throw new ScenarioException($"{full}: asset not found: {asset}");
        return config;
    }

    private static bool IsNull(object? value) => value is null;

    private static string Absolute(string dir, string path) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(dir, path));
}
