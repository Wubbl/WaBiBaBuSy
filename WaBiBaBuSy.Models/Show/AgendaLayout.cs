namespace WaBiBaBuSy.Models.Show;

/// <summary>A rectangle in screen pixels (player) or node pixels (preview).</summary>
public sealed record AgendaRect(float X, float Y, float W, float H);

/// <summary>Card and line boxes of the agenda. <see cref="Title"/> has height 0 when there is no title.</summary>
public sealed record AgendaPanelLayout(AgendaRect Card, AgendaRect Title, IReadOnlyList<AgendaRect> Rows, IReadOnlyList<AgendaRect> Notes);

/// <summary>Where the agenda goes on a screen: the corner panel and the full-size agenda screen. Pure, shared by player and preview.</summary>
public static class AgendaLayout
{
    private const float MaxPanelHeight = 0.30f;   // of the screen height
    private const float PanelWidth = 0.26f;       // of the screen width
    private const float MinPanelWidthPx = 360f;
    private const float Margin = 0.02f;           // of the screen height
    private const float PreferredRow = 0.032f;    // of the screen height
    private const float TitleRow = 1.3f;          // × row height
    private const float NoteRow = 0.8f;           // × row height
    private const float Padding = 0.4f;           // × row height, top and bottom (and left / right)

    public static AgendaPanelLayout Panel(float screenW, float screenH, AgendaCorner corner, bool hasTitle, int rows, int noteLines)
    {
        float units = (hasTitle ? TitleRow : 0) + rows + NoteRow * noteLines;
        float row = Math.Min(PreferredRow * screenH, MaxPanelHeight * screenH / (units + 2 * Padding));
        float margin = Margin * screenH;
        float w = Math.Min(Math.Max(PanelWidth * screenW, MinPanelWidthPx), screenW - 2 * margin);
        float h = row * (units + 2 * Padding);
        bool right = corner is AgendaCorner.TopRight or AgendaCorner.BottomRight;
        bool bottom = corner is AgendaCorner.BottomLeft or AgendaCorner.BottomRight;
        var card = new AgendaRect(right ? screenW - margin - w : margin, bottom ? screenH - margin - h : margin, w, h);

        float x = card.X + Padding * row, inner = w - 2 * Padding * row, y = card.Y + Padding * row;
        var title = new AgendaRect(x, y, inner, hasTitle ? TitleRow * row : 0);
        y += title.H;
        var lines = new List<AgendaRect>(rows);
        for (int i = 0; i < rows; i++, y += row) lines.Add(new AgendaRect(x, y, inner, row));
        var notes = new List<AgendaRect>(noteLines);
        for (int i = 0; i < noteLines; i++, y += NoteRow * row) notes.Add(new AgendaRect(x, y, inner, NoteRow * row));
        return new AgendaPanelLayout(card, title, lines, notes);
    }

    /// <summary>
    /// The full-size agenda: title on top, one column per day (left to right), notes at the bottom. <see cref="AgendaPanelLayout.Rows"/>
    /// is column-major: <paramref name="maxRowsPerDay"/> slots per column — the caller puts the day name in slot 0.
    /// </summary>
    public static AgendaPanelLayout Screen(float screenW, float screenH, int dayColumns, int maxRowsPerDay, int noteLines)
    {
        float margin = 0.04f * screenH;
        var card = new AgendaRect(0, 0, screenW, screenH);
        float innerW = screenW - 2 * margin;
        var title = new AgendaRect(margin, margin, innerW, 0.08f * screenH);
        float noteH = 0.04f * screenH;
        float top = title.Y + title.H + 0.02f * screenH;
        float bottom = screenH - margin - noteLines * noteH;
        int columns = Math.Max(1, dayColumns), slots = Math.Max(1, maxRowsPerDay);
        float row = Math.Min(0.06f * screenH, (bottom - top) / slots);
        float gap = 0.02f * screenW;
        float colW = (innerW - gap * (columns - 1)) / columns;

        var rows = new List<AgendaRect>(columns * slots);
        for (int c = 0; c < columns; c++)
            for (int r = 0; r < slots; r++)
                rows.Add(new AgendaRect(margin + c * (colW + gap), top + r * row, colW, row));
        var notes = new List<AgendaRect>(noteLines);
        for (int i = 0; i < noteLines; i++) notes.Add(new AgendaRect(margin, bottom + i * noteH, innerW, noteH));
        return new AgendaPanelLayout(card, title, rows, notes);
    }
}
