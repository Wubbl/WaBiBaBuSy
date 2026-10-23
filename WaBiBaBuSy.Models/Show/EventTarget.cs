namespace WaBiBaBuSy.Models.Show;

public enum EventTargetKind { Everyone, Tag, Seat, Selected }

/// <summary>Who a fired event is for: every seat, the nodes selected in the room view, the seats carrying a tag, or one seat. <see cref="Label"/> is the picker text.</summary>
public sealed record EventTarget(EventTargetKind Kind, string Value, string Label)
{
    public static EventTarget Everyone { get; } = new(EventTargetKind.Everyone, "", "Everyone");

    /// <summary>The nodes selected in the room view when the event fires.</summary>
    public static EventTarget Selected { get; } = new(EventTargetKind.Selected, "", "Selected nodes");

    public override string ToString() => Label;

    /// <summary>Picker entries: Everyone, Selected nodes, then each tag, then each seat (id, label) in room order.</summary>
    public static List<EventTarget> Choices(IEnumerable<string> tags, IEnumerable<(string Id, string Label)> seats)
    {
        var list = new List<EventTarget> { Everyone, Selected };
        list.AddRange(tags.Select(t => new EventTarget(EventTargetKind.Tag, t, $"Tag: {t}")));
        list.AddRange(seats.Select(s => new EventTarget(EventTargetKind.Seat, s.Id, $"Seat {s.Label}")));
        return list;
    }

    /// <summary>
    /// The node ids this target covers; null = everyone, empty = nobody (Selected with no selection).
    /// <paramref name="nodesWithTag"/> answers a tag lookup, <paramref name="selectedNodes"/> the room view's selection.
    /// </summary>
    public List<string>? Resolve(Func<string, IReadOnlyList<string>> nodesWithTag, Func<IEnumerable<string>>? selectedNodes = null) => Kind switch
    {
        EventTargetKind.Selected => selectedNodes?.Invoke().ToList() ?? new List<string>(),
        EventTargetKind.Tag => nodesWithTag(Value).ToList(),
        EventTargetKind.Seat => new List<string> { Value },
        _ => null,
    };
}
