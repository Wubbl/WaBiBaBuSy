namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>
/// Turns a folder's media profiles into a varied party playlist (spec 2026-10-14 folder-playlist §1): full-screen files
/// and videos as Showcases, look-alike transparent sprites in groups of 3–5, the rest as solo items; interleaved, no
/// recipe twice in a row, GIF dwell on whole loops. Pure and deterministic in its inputs.
/// </summary>
public static class PlaylistBuilder
{
    /// <summary>A Showcase video plays at most this long.</summary>
    public const int MaxVideoDwellMs = 120_000;

    private const int GroupSize = 4;
    private enum Bucket { Group, Solo, Showcase }
    private static readonly Bucket[] Rhythm = { Bucket.Group, Bucket.Solo, Bucket.Group, Bucket.Showcase };

    /// <summary>Group sizes for <paramref name="count"/> swarmable files (≥ 3): fours, the remainder folded in (5 → 5, 6 → 3+3, 9 → 4+5).</summary>
    public static IReadOnlyList<int> GroupSizes(int count)
    {
        if (count < 3) return Array.Empty<int>();
        var sizes = Enumerable.Repeat(GroupSize, count / GroupSize).ToList();
        switch (count % GroupSize)
        {
            case 1: sizes[^1] = 5; break;
            case 2: sizes[^1] = 3; sizes.Add(3); break;
            case 3: sizes.Add(3); break;
        }
        return sizes;
    }

    /// <summary>
    /// Build the playlist. <paramref name="baseScene"/> supplies monitor targets and distribution mode (as for Suggest);
    /// <paramref name="defaultDurationMs"/> becomes the playlist default and the dwell items round to.
    /// </summary>
    public static Playlist Build(string name, IReadOnlyList<MediaProfile> files, int screenWidth, int screenHeight,
        float pixelsPerCm, CrossScreenConfig baseScene, int defaultDurationMs)
    {
        var playlist = new Playlist { Name = name, DefaultItemDurationMs = defaultDurationMs };
        var ordered = files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase).ToList();

        var showcase = ordered.Where(IsShowcase).ToList();
        var swarm = ordered.Where(f => !IsShowcase(f) && f.Pixels.HasAlpha && f.Aspect != AspectClass.Wide).ToList();
        var solo = ordered.Where(f => !IsShowcase(f) && !swarm.Contains(f)).ToList();
        if (swarm.Count < 3) { solo.AddRange(swarm); solo = solo.OrderBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase).ToList(); swarm.Clear(); }

        var looks = swarm.OrderBy(f => f.Pixels.IsMonochrome ? 0 : 1).ThenBy(f => f.PixelHeight)
            .ThenBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase).ToList();
        var groups = new Queue<IReadOnlyList<MediaProfile>>();
        int at = 0;
        foreach (int size in GroupSizes(looks.Count)) { groups.Enqueue(looks.GetRange(at, size)); at += size; }

        var queues = new Dictionary<Bucket, Queue<IReadOnlyList<MediaProfile>>>
        {
            [Bucket.Group] = groups,
            [Bucket.Solo] = new(solo.Select(f => (IReadOnlyList<MediaProfile>)new[] { f })),
            [Bucket.Showcase] = new(showcase.Select(f => (IReadOnlyList<MediaProfile>)new[] { f })),
        };

        string? previous = null;
        for (int beat = 0; queues.Values.Any(q => q.Count > 0); beat++)
        {
            var queue = queues[Rhythm[beat % Rhythm.Length]];
            if (queue.Count == 0) continue;
            var item = BuildItem(queue.Dequeue(), screenWidth, screenHeight, pixelsPerCm, baseScene, defaultDurationMs, previous);
            previous = item.Name.Split(':')[0];
            playlist.Items.Add(item);
        }
        return playlist;
    }

    private static bool IsShowcase(MediaProfile f) => f.Size == SizeClass.FullScreen || f.IsPanorama || f.Kind == MediaKind.Video;

    private static PlaylistItem BuildItem(IReadOnlyList<MediaProfile> files, int screenWidth, int screenHeight,
        float pixelsPerCm, CrossScreenConfig baseScene, int defaultDurationMs, string? previousRecipe)
    {
        var selection = new SelectionProfile(files, screenWidth, screenHeight, pixelsPerCm);
        var ranked = SceneSuggester.Ranked(selection);
        var recipe = ranked.FirstOrDefault(r => r.Name != previousRecipe) ?? ranked[0];
        int seed = PatternLayout.Hash3(StableHash(files), 0, 11);
        var card = SceneSuggester.BuildCard(recipe, selection, baseScene, seed);

        string first = Path.GetFileName(files[0].Path);
        return new PlaylistItem
        {
            Name = $"{recipe.Name}: {first}" + (files.Count > 1 ? $" + {files.Count - 1}" : string.Empty),
            Config = card.Scene,
            DurationMs = Dwell(selection, defaultDurationMs),
        };
    }

    /// <summary>Video: its length up to the cap; GIFs: the nearest whole number of shortest loops; else the default (null).</summary>
    private static int? Dwell(SelectionProfile s, int defaultDurationMs)
    {
        long dwell = s.Files.FirstOrDefault(f => f.Kind == MediaKind.Video)?.DurationMs is long videoMs
            ? Math.Min(videoMs, MaxVideoDwellMs)
            : s.ShortestLoopMs is long loop && loop > 0
                ? Math.Max(1, (long)Math.Round(defaultDurationMs / (double)loop)) * loop
                : defaultDurationMs;
        return dwell == defaultDurationMs ? null : (int)Math.Min(dwell, int.MaxValue);
    }

    /// <summary>FNV-1a over the lower-case file names: stable across runs and machines (unlike string.GetHashCode).</summary>
    private static int StableHash(IReadOnlyList<MediaProfile> files)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var f in files)
                foreach (char c in Path.GetFileName(f.Path).ToLowerInvariant()) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }
}
