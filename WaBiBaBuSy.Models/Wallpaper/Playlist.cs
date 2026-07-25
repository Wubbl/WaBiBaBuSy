using System.Collections.Generic;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// An ordered set of animation configurations rotated across all synced machines as a "show."
/// Persisted as JSON; each item embeds a full self-contained <see cref="CrossScreenConfig"/>.
/// </summary>
public class Playlist
{
    /// <summary>Display name of the playlist.</summary>
    public string Name { get; set; } = "Untitled Playlist";

    /// <summary>When true, repeat the whole list forever; otherwise stop after the last item.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>When true, randomise item order once per full cycle (server-side only).</summary>
    public bool Shuffle { get; set; } = false;

    /// <summary>Dwell time (ms) used for items whose own <see cref="PlaylistItem.DurationMs"/> is null.</summary>
    public int DefaultItemDurationMs { get; set; } = 30_000;

    /// <summary>Items in author order.</summary>
    public List<PlaylistItem> Items { get; set; } = new();
}

/// <summary>A single playlist entry: one animation config plus its dwell/rotation settings.</summary>
public class PlaylistItem
{
    /// <summary>Display label, e.g. "Fish traversal".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full embedded animation configuration for this item.</summary>
    public CrossScreenConfig Config { get; set; } = new();

    /// <summary>Per-item dwell time in ms. Null = use the playlist's <see cref="Playlist.DefaultItemDurationMs"/>.</summary>
    public int? DurationMs { get; set; }

    /// <summary>
    /// When true, round the dwell up to the next whole movement lap before switching
    /// (Linear movement without a Pattern only; otherwise falls back to hard-cut). See PlaylistScheduler.
    /// </summary>
    public bool SnapToLap { get; set; } = false;

    /// <summary>Resolve the effective dwell for this item given the playlist default.</summary>
    public int GetEffectiveDurationMs(int playlistDefault) => DurationMs ?? playlistDefault;
}
