using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>How a playlist item replaces the one before it (roadmap 2.5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransitionKind
{
    /// <summary>Switch on the shared start (the standby switch).</summary>
    Cut,
    /// <summary>Dip through black: the old item darkens over the half before the start, the new one brightens over the half after.</summary>
    Fade,
    /// <summary>Ring wipe: a boundary runs once along the seat-map path; behind it the new item shows, ahead of it the old one.</summary>
    Wipe,
}

/// <summary>A transition and its length. Travels to remote seats as <c>SyncParameters.transition_effect</c> ("Fade:1500").</summary>
public sealed class SceneTransition
{
    public const int MinDurationMs = 200;
    public const int MaxDurationMs = 20_000;

    public TransitionKind Kind { get; set; } = TransitionKind.Cut;
    public int DurationMs { get; set; } = 1500;

    [JsonIgnore] public bool IsCut => Kind == TransitionKind.Cut;
    [JsonIgnore] public int ClampedMs => Math.Clamp(DurationMs, MinDurationMs, MaxDurationMs);

    public static SceneTransition Cut => new();

    /// <summary>Wire form: "" for Cut, else "Kind:ms".</summary>
    public string Encode() => IsCut ? string.Empty : $"{Kind}:{ClampedMs}";

    /// <summary>Wire form back; anything unknown or malformed is a Cut (an older server sends "").</summary>
    public static SceneTransition Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Cut;
        var parts = text.Split(':');
        if (!Enum.TryParse<TransitionKind>(parts[0], ignoreCase: true, out var kind)) return Cut;
        int ms = parts.Length > 1 && int.TryParse(parts[1], out var v) ? v : 1500;
        return new SceneTransition { Kind = kind, DurationMs = ms };
    }

    public override string ToString() => IsCut ? "Cut" : $"{Kind} {ClampedMs / 1000.0:0.#} s";
}

/// <summary>
/// Pure timing of item transitions, shared by the players (and testable): everything is a function of the shared
/// start T0 and the clock, so every seat runs the same transition on the same frame.
/// </summary>
public static class TransitionMath
{
    /// <summary>Black veil over the incoming item (Fade): 1 before T0, down to 0 at T0 + half.</summary>
    public static float FadeInVeil(long msSinceStart, int durationMs)
    {
        float half = Math.Max(1, durationMs / 2);
        if (msSinceStart < 0) return 1f;
        return msSinceStart >= half ? 0f : 1f - msSinceStart / half;
    }

    /// <summary>Black veil over the outgoing item (Fade): 0 until T0 − half, up to 1 at T0.</summary>
    public static float FadeOutVeil(long msSinceStart, int durationMs)
    {
        float half = Math.Max(1, durationMs / 2);
        if (msSinceStart >= 0) return 1f;
        return msSinceStart <= -half ? 0f : (msSinceStart + half) / half;
    }

    /// <summary>Wipe boundary on the path (canvas units): 0 at T0, <paramref name="canvasWidth"/> at T0 + duration.</summary>
    public static float WipeBoundary(long msSinceStart, int durationMs, float canvasWidth) =>
        Math.Clamp(msSinceStart / (float)Math.Max(1, durationMs), 0f, 1f) * canvasWidth;

    /// <summary>
    /// The part of a node's slice the boundary has passed, in local canvas units [Left, Right) of [0, Width].
    /// The path runs left→right in canvas space; on a mirrored node it enters from the right edge.
    /// </summary>
    public static (float Left, float Right) WipedSpan(float boundary, NodeLayout node)
    {
        float s = Math.Clamp(boundary - node.OffsetX, 0f, node.Width);
        return node.Mirrored ? (node.Width - s, node.Width) : (0f, s);
    }

    /// <summary>How long after T0 the outgoing players must stay (the wipe still shows them ahead of the boundary).</summary>
    public static int OutgoingHoldMs(SceneTransition t) => t.Kind == TransitionKind.Wipe ? t.ClampedMs : 0;
}
