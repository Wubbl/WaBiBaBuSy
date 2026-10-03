namespace WaBiBaBuSy.Models.Show;

/// <summary>Global hotkeys of the server (Ctrl+Alt+1..5) and the event each fires.</summary>
public static class ShowEventHotkeys
{
    private static readonly ShowEventType[] Types =
        { ShowEventType.Spotlight, ShowEventType.Celebration, ShowEventType.Traversal, ShowEventType.Blackout, ShowEventType.Announce };

    /// <summary>The hotkey slots, 1..5.</summary>
    public static IReadOnlyList<int> Slots { get; } = Enumerable.Range(1, Types.Length).ToArray();

    /// <summary>The event a slot fires, or null for an unknown slot.</summary>
    public static ShowEventType? TypeForSlot(int slot) => slot >= 1 && slot <= Types.Length ? Types[slot - 1] : null;

    /// <summary>Display text of a slot's gesture, e.g. "Ctrl+Alt+3".</summary>
    public static string Gesture(int slot) => $"Ctrl+Alt+{slot}";
}
