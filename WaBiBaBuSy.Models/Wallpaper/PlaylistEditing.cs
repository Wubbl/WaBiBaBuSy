using System.Globalization;
using System.Text.Json;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Helpers for the Playlist tab: durations are typed in seconds but stored in ms, rows are reordered
/// by dragging into the gap between two rows, and items are duplicated as deep copies.
/// </summary>
public static class PlaylistEditing
{
    /// <summary>Longest duration accepted from the editor (24 h), in seconds.</summary>
    public const int MaxSeconds = 86_400;

    /// <summary>
    /// Parse a duration typed in seconds ("30", "12.5", "12,5") to whole ms. Blank, zero, negative or
    /// unparsable text → null, which means "use the playlist default"; values above
    /// <see cref="MaxSeconds"/> are clamped.
    /// </summary>
    public static int? ParseSecondsToMs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return null;
        if (double.IsNaN(seconds) || seconds <= 0) return null;
        return (int)Math.Round(Math.Min(seconds, MaxSeconds) * 1000.0);
    }

    /// <summary>Ms → seconds text for the editor ("30", "12.5"); null or 0 → empty (= default).</summary>
    public static string FormatMsAsSeconds(int? ms)
        => ms is > 0 ? (ms.Value / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>
    /// Final index for moving the row at <paramref name="from"/> into the gap before row
    /// <paramref name="insertBefore"/> as currently displayed (<paramref name="count"/> = after the last
    /// row; clamped). Returns -1 when nothing moves (dropped next to itself, or <paramref name="from"/>
    /// out of range). The result is the <c>newIndex</c> argument of <c>ObservableCollection.Move</c>.
    /// </summary>
    public static int MoveTarget(int count, int from, int insertBefore)
    {
        if (from < 0 || from >= count) return -1;
        insertBefore = Math.Clamp(insertBefore, 0, count);
        int to = insertBefore > from ? insertBefore - 1 : insertBefore;
        return to == from ? -1 : to;
    }

    /// <summary>Deep copy of an item (embedded config included), named "&lt;name&gt; copy".</summary>
    public static PlaylistItem Duplicate(PlaylistItem item)
    {
        var clone = JsonSerializer.Deserialize<PlaylistItem>(JsonSerializer.Serialize(item))!;
        clone.Name = string.IsNullOrWhiteSpace(item.Name) ? "copy" : item.Name + " copy";
        return clone;
    }
}
