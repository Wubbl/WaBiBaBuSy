using System.Collections.Generic;
using Avalonia.Media;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>One colored tag on a playlist row: glyph + text in its category's color.</summary>
public sealed record SceneChip(string Glyph, string Text, IBrush Foreground, IBrush Background, string? Tip = null);

/// <summary>
/// Icons (plain Unicode symbols, colored by <c>Foreground</c>) and category colors shared by the Playlist rows and the
/// Scene editor, so a mode looks the same everywhere: source = blue, motion = amber, distribution = violet,
/// look (background, pattern, grading, effects) = teal, transition = pink.
/// </summary>
public static class SceneGlyphs
{
    public static readonly IBrush Source = Brush("#6CB4FF");
    public static readonly IBrush Motion = Brush("#FFB547");
    public static readonly IBrush Spread = Brush("#B48CFF");
    public static readonly IBrush Look = Brush("#4FD1A5");
    public static readonly IBrush Transition = Brush("#FF7AB8");

    // Scene editor XAML binds these through x:Static.
    public const string Sequential = "⇉";
    public const string Simultaneous = "⧉";
    public const string BgSolid = "■";
    public const string BgStretched = "▣";
    public const string BgTiled = "▦";
    public const string BgThreeZone = "☰";
    public const string BgIconZone = "⌖";
    public const string BgPanorama = "▭";

    public static string Movement(MovementType type) => type switch
    {
        MovementType.Static => "◎",
        MovementType.Linear => "→",
        MovementType.Bounce => "⇄",
        MovementType.SineWave => "≈",
        MovementType.Circular => "↻",
        MovementType.RandomWalk => "⁂",
        _ => "•",
    };

    public static string Distribution(AnimationDistributionMode mode) =>
        mode == AnimationDistributionMode.Simultaneous ? Simultaneous : Sequential;

    public static string Background(BackgroundMode mode) => mode switch
    {
        BackgroundMode.SolidColor => BgSolid,
        BackgroundMode.StretchedImage => BgStretched,
        BackgroundMode.TiledImage => BgTiled,
        BackgroundMode.ThreeZone => BgThreeZone,
        BackgroundMode.IconZone => BgIconZone,
        BackgroundMode.Panorama => BgPanorama,
        _ => "•",
    };

    public static string TransitionGlyph(TransitionKind kind) => kind switch
    {
        TransitionKind.Fade => "◐",
        TransitionKind.Wipe => "⟳",
        _ => "✂",
    };

    /// <summary>Video ▶, GIF ◈, still image ▣.</summary>
    public static string Media(string? path) =>
        SceneChecks.IsVideo(path) ? "▶"
        : string.Equals(System.IO.Path.GetExtension(path), ".gif", System.StringComparison.OrdinalIgnoreCase) ? "◈"
        : "▣";

    public static SceneChip Chip(IBrush color, string glyph, string text, string? tip = null) =>
        new(glyph, text, color, Tint(color), tip);

    private static readonly Dictionary<IBrush, IBrush> Tints = new();

    private static IBrush Tint(IBrush color)
    {
        if (Tints.TryGetValue(color, out var tint)) return tint;
        var c = ((ISolidColorBrush)color).Color;
        return Tints[color] = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B)).ToImmutable();
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();
}
