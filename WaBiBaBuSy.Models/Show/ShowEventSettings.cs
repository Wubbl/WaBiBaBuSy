namespace WaBiBaBuSy.Models.Show;

/// <summary>Defaults the Events strip and the hotkeys use when they fire an event (persisted by <c>ShowEventSettingsStore</c>).</summary>
public sealed class ShowEventSettings
{
    public int SpotlightDurationMs { get; set; } = 8000;
    public string SpotlightColorHex { get; set; } = "#FFD700";
    public int CelebrationDurationMs { get; set; } = 5000;
    public string CelebrationColorHex { get; set; } = "#FFD700";
    /// <summary>Empty = the running scene's sprite file.</summary>
    public string TraversalImagePath { get; set; } = string.Empty;
    public float TraversalSpeedPxPerSecond { get; set; } = 600f;
    public float TraversalHeightFraction { get; set; } = 0.5f;
    public bool TraversalReverse { get; set; }
    public int BlackoutDurationMs { get; set; } = 3000;
    public string BlackoutColorHex { get; set; } = "#000000";
    public string AnnounceText { get; set; } = string.Empty;
    public string AnnounceColorHex { get; set; } = "#FFFFFF";
    public float AnnounceSpeedPxPerSecond { get; set; } = 400f;
    public float AnnounceHeightFraction { get; set; } = 0.25f;
    public int IdentifyDurationMs { get; set; } = 10000;
    /// <summary>Seat the last Spotlight targeted (the hotkey uses it).</summary>
    public string? LastTargetSeatId { get; set; }
    public bool HotkeysEnabled { get; set; } = true;
    /// <summary>BiBaBo countdown.</summary>
    public int BiBaBoTimerMs { get; set; } = 300_000;
    /// <summary>BiBaBo cables and riddle cards (3–5).</summary>
    public int BiBaBoRiddles { get; set; } = 3;
}
