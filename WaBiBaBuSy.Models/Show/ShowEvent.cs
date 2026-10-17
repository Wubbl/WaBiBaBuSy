using System.Text.Json.Serialization;

namespace WaBiBaBuSy.Models.Show;

/// <summary>Kind of one-shot show event (roadmap 2.4, design 2026-10-03-show-events-design.md).</summary>
/// <remarks>System.Text.Json writes the name, so an old client that does not know a newer type fails to parse it and ignores the event.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
/// <remarks>Identify: every seat shows its own seat number (<c>NodeLayout.Order</c> + 1) and machine name, for setting up the room.</remarks>
public enum ShowEventType { Spotlight, Celebration, Traversal, Blackout, Announce, Identify }

/// <summary>
/// One one-shot moment drawn by every seat above the scene from <see cref="T0Utc"/> for <see cref="DurationMs"/>.
/// Everything a node draws is a pure function of this object, its node layout and the event-local time.
/// </summary>
public sealed class ShowEvent
{
    /// <summary>Unique per firing; players and hosts dedupe replays by it.</summary>
    public string EventId { get; set; } = string.Empty;
    public ShowEventType Type { get; set; }
    /// <summary>Shared start, UTC ms. On clients: the command's timestamp after clock conversion.</summary>
    public long T0Utc { get; set; }
    public int DurationMs { get; set; }
    /// <summary>Drawn once on the server and broadcast (Celebration particles).</summary>
    public int Seed { get; set; }
    public string ColorHex { get; set; } = "#FFD700";
    /// <summary>Spotlight: traversal index (<c>NodeLayout.Order</c>) of the target seat; -1 = none.</summary>
    public int TargetOrder { get; set; } = -1;
    /// <summary>Spotlight: center of the target's slice on the shared canvas (chevron direction).</summary>
    public float TargetCenterX { get; set; }
    /// <summary>Traversal: image file; rewritten to the client's cache on receipt (null when it could not be fetched).</summary>
    public string? ImagePath { get; set; }
    /// <summary>Traversal / Announce: width ÷ height of the sprite, measured once on the server.</summary>
    public float SpriteAspect { get; set; } = 1f;
    /// <summary>Traversal / Announce: sprite height as a fraction of the canvas height.</summary>
    public float HeightFraction { get; set; } = 0.5f;
    /// <summary>Traversal / Announce: run the lap against the traversal direction.</summary>
    public bool Reverse { get; set; }
    /// <summary>Announce: the text.</summary>
    public string? Text { get; set; }

    /// <summary>Seats (node ids) that draw this event; null = every seat. Players and the preview skip the others.</summary>
    public List<string>? TargetNodeIds { get; set; }
    /// <summary>Identify: label per node id ("3  GAMING-PC"), numbered over the whole room by the server.</summary>
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>True when the seat <paramref name="nodeId"/> draws this event.</summary>
    public bool IsFor(string nodeId) => TargetNodeIds == null || TargetNodeIds.Contains(nodeId);

    /// <summary>Shallow copy (all members are values or immutable strings).</summary>
    public ShowEvent Clone() => (ShowEvent)MemberwiseClone();
}
