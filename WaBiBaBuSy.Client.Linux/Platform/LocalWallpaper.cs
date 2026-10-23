using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace WaBiBaBuSy.Client.Linux.Platform;

/// <summary>
/// This machine's own desktop wallpaper — what a stand-in scene (agenda on an idle seat, the BiBaBo / show-event
/// stand-in) shows, like the Windows player's SPI_GETDESKWALLPAPER. GNOME via gsettings, KDE Plasma via its
/// applet config; null when neither answers (the scene's own fallback image is used then).
/// </summary>
public static partial class LocalWallpaper
{
    /// <summary>The wallpaper file, or (no picture set) the desktop's plain colour as #RRGGBB.</summary>
    public sealed record Result(string? ImagePath, string? ColorHex);

    private static (Result? Value, long AtMs) s_cache;
    private const long CacheMs = 30_000;

    public static Result? Find(ILogger? logger = null)
    {
        long now = Environment.TickCount64;
        var cached = s_cache;
        if (cached.AtMs != 0 && now - cached.AtMs < CacheMs) return cached.Value;
        Result? result = null;
        // KDE machines often have gsettings too (with GNOME's defaults), so ask the running desktop first
        bool kde = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("KDE", StringComparison.OrdinalIgnoreCase);
        try { result = kde ? FromKde() ?? FromGnome() : FromGnome() ?? FromKde(); }
        catch (Exception ex) { logger?.LogDebug(ex, "Could not read the desktop wallpaper"); }
        s_cache = (result, now);
        logger?.LogInformation("[BG] Local desktop wallpaper: {Path}", result?.ImagePath ?? result?.ColorHex ?? "(unknown)");
        return result;
    }

    private static Result? FromGnome()
    {
        var scheme = GSettings("org.gnome.desktop.interface", "color-scheme");
        if (scheme == null) return null;   // no gsettings / no GNOME schema
        string? uri = scheme.Contains("dark") ? GSettings("org.gnome.desktop.background", "picture-uri-dark") : null;
        if (string.IsNullOrEmpty(uri)) uri = GSettings("org.gnome.desktop.background", "picture-uri");
        var path = FileFromUri(uri) ?? FromSlideshow(uri);
        if (path != null) return new Result(path, null);
        // The plain colour only when GNOME really shows no picture. Otherwise it is just the schema default (#023c88,
        // dark blue) behind a picture we could not read, and the scene's fallback image is the better guess.
        if (GSettings("org.gnome.desktop.background", "picture-options") != "none" && !string.IsNullOrWhiteSpace(uri?.Trim('\'')))
            return null;
        var color = GSettings("org.gnome.desktop.background", "primary-color");
        return color != null && HexColor().IsMatch(color) ? new Result(null, color[..7]) : null;
    }

    private static Result? FromKde()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "plasma-org.kde.plasma.desktop-appletsrc");
        if (!File.Exists(config)) return null;
        // [Containments][N][Wallpaper][org.kde.image][General] Image=file:///…  (first desktop containment wins)
        bool inImageGroup = false;
        foreach (var line in File.ReadLines(config))
        {
            if (line.StartsWith('[')) inImageGroup = line.Contains("[Wallpaper][org.kde.image][General]");
            else if (inImageGroup && line.StartsWith("Image=") && FileFromUri(line["Image=".Length..]) is { } path) return new Result(path, null);
        }
        return null;
    }

    /// <summary>"'file:///home/a/b.jpg'" / "file:///…" / a plain path → an existing file, else null.</summary>
    private static string? FileFromUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().Trim('\'');
        string path = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : value;
        // A folder (KDE slideshow) or a GNOME .xml slideshow is not an image
        bool image = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".jxl";
        return image && File.Exists(path) ? path : null;
    }

    /// <summary>A GNOME .xml slideshow (Fedora's default wallpaper is one) → its first picture, else null.</summary>
    private static string? FromSlideshow(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().Trim('\'');
        string path = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : value;
        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        foreach (Match m in SlideshowFile().Matches(File.ReadAllText(path)))
            if (FileFromUri(m.Groups[1].Value) is { } image) return image;
        return null;
    }

    private static string? GSettings(string schema, string key)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gsettings", $"get {schema} {key}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000) || p.ExitCode != 0) return null;
            return output.Trim().Trim('\'');
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;   // gsettings not installed
        }
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}")]
    private static partial Regex HexColor();

    /// <summary>A path inside &lt;file&gt; or &lt;size&gt; of a slideshow XML.</summary>
    [GeneratedRegex(@">\s*(/[^<>]+?)\s*</(?:file|size)>")]
    private static partial Regex SlideshowFile();
}
