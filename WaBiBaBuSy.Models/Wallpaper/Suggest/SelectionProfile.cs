namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>How the picked files relate to each other (spec 2026-10-14 §1.2).</summary>
public enum SelectionMix { Single, Uniform, BigPlusSmall, Mixed }

/// <summary>The whole selection the recipes score and build from: files (hero first) plus the screen they target.</summary>
public sealed class SelectionProfile
{
    private const int MaxPalette = 5;

    /// <summary>
    /// Profile the selection. <paramref name="files"/> must hold at least one file, hero first; repeated paths count once.
    /// <paramref name="pixelsPerCm"/> is the primary monitor's reference DPI (0 = unknown) for physical-unit rooms.
    /// </summary>
    public SelectionProfile(IReadOnlyList<MediaProfile> files, int screenWidth, int screenHeight, float pixelsPerCm = 0f)
    {
        if (files.Count == 0) throw new ArgumentException("A selection needs at least one file", nameof(files));
        files = files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        Files = files;
        PixelsPerCm = pixelsPerCm > 0f ? pixelsPerCm : 0f;
        ScreenWidth = screenWidth > 0 ? screenWidth : 1920;
        ScreenHeight = screenHeight > 0 ? screenHeight : 1080;
        Mix = ClassifyMix(files);
        Big = Mix == SelectionMix.BigPlusSmall ? files.First(f => f.Size == SizeClass.FullScreen)
            : files.Count == 1 && files[0].Size == SizeClass.FullScreen ? files[0]
            : null;
        Sprites = Mix == SelectionMix.BigPlusSmall ? files.Where(f => f.Size != SizeClass.FullScreen).ToList() : files;
        AllMonochrome = Sprites.All(f => f.Pixels.IsMonochrome);
        var paletteSource = Mix == SelectionMix.BigPlusSmall ? files.OrderBy(f => f == Big ? 0 : 1) : files.AsEnumerable();
        SharedPalette = paletteSource.SelectMany(f => f.Pixels.DominantColors)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPalette).ToList();
        var loops = files.Where(f => f.LoopMs > 0).Select(f => f.LoopMs!.Value).ToList();
        ShortestLoopMs = loops.Count > 0 ? loops.Min() : null;
    }

    /// <summary>The picked files, hero first.</summary>
    public IReadOnlyList<MediaProfile> Files { get; }
    /// <summary>Number of picked files.</summary>
    public int Count => Files.Count;
    /// <summary>Primary monitor width in pixels (1920 when unknown).</summary>
    public int ScreenWidth { get; }
    /// <summary>Primary monitor height in pixels (1080 when unknown).</summary>
    public int ScreenHeight { get; }
    /// <summary>Reference pixels per cm of the primary monitor; 0 when unknown (cm values are then not filled).</summary>
    public float PixelsPerCm { get; }
    /// <summary>How the files relate to each other.</summary>
    public SelectionMix Mix { get; }
    /// <summary>The full-screen file (BigPlusSmall, or a single full-screen file); otherwise null.</summary>
    public MediaProfile? Big { get; }
    /// <summary>The files sprite recipes move: all of them, except the big one in BigPlusSmall.</summary>
    public IReadOnlyList<MediaProfile> Sprites { get; }
    /// <summary>Every sprite is monochrome (and so can be tinted).</summary>
    public bool AllMonochrome { get; }
    /// <summary>Up to 5 dominant colours (the big file's first in BigPlusSmall, else the hero's), no duplicates.</summary>
    public IReadOnlyList<string> SharedPalette { get; }
    /// <summary>The shortest GIF loop in milliseconds, or null without GIFs.</summary>
    public long? ShortestLoopMs { get; }

    /// <summary>A sprite is a video (only Showcase can use videos).</summary>
    public bool HasVideoSprite => Sprites.Any(f => f.Kind == MediaKind.Video);
    /// <summary>Every sprite has transparency (can float, can swarm).</summary>
    public bool AllSpritesHaveAlpha => Sprites.All(f => f.Pixels.HasAlpha);
    /// <summary>Every sprite is Small.</summary>
    public bool AllSpritesSmall => Sprites.All(f => f.Size == SizeClass.Small);
    /// <summary>Mean luminance over the sprites.</summary>
    public double MeanLuminance => Sprites.Average(f => f.Pixels.MeanLuminance);

    /// <summary>"cat.gif" or "cat.gif + 2 more" for the strip title.</summary>
    public string Describe() =>
        Path.GetFileName(Files[0].Path) + (Count > 1 ? $" + {Count - 1} more" : string.Empty);

    private static SelectionMix ClassifyMix(IReadOnlyList<MediaProfile> files)
    {
        if (files.Count == 1) return SelectionMix.Single;
        if (files.Count(f => f.Size == SizeClass.FullScreen) == 1) return SelectionMix.BigPlusSmall;
        if (files.Any(f => f.PixelHeight <= 0)) return SelectionMix.Mixed;
        int min = files.Min(f => f.PixelHeight), max = files.Max(f => f.PixelHeight);
        return max <= 2 * min ? SelectionMix.Uniform : SelectionMix.Mixed;
    }
}
