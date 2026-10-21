// WaBiBaBuSy.Player.D2D/Program.Agenda.cs
using System.Drawing;
using Microsoft.Extensions.Logging;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Player.Common.Messages;
using WaBiBaBuSy.WallpaperEngine.Show;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>
/// The room's agenda (design 2026-10-21-agenda-overlay-design.md): a corner panel, or the full-size timetable on an
/// agenda screen, drawn after the show events in device pixels. Rows / layout come from WaBiBaBuSy.Models/Show
/// (AgendaMath, AgendaLayout) and are rebuilt once a second, not per frame; text is TextSprite bitmaps rendered on a
/// worker and uploaded here on the render thread.
/// </summary>
partial class Program
{
    private enum AgendaDrawKind { Fill, PulseFill, Text, TextRight }

    private readonly record struct AgendaDraw(AgendaDrawKind Kind, AgendaRect Rect, string? Key, Color4 Color, float Opacity);

    private const string AgendaWhite = "#FFFFFF";
    private const string AgendaGold = "#FFD700";
    private static readonly Color4 AgendaCard = new(0.078f, 0.078f, 0.078f, 1f);   // #141414
    private static readonly Color4 AgendaHighlight = new(1f, 0.843f, 0f, 1f);       // #FFD700
    private const int MaxAgendaNotes = 3;
    private const int MaxAgendaRowsPerDay = 14;

    private static readonly object _agendaLock = new();
    private static Agenda? _agenda;                                                       // guarded by _agendaLock
    private static bool _agendaFullScreen;                                                // guarded by _agendaLock
    private static int _agendaVersion;                                                    // guarded by _agendaLock
    private static readonly Dictionary<string, Bitmap> _agendaRendered = new();          // guarded by _agendaLock
    private static readonly HashSet<string> _agendaRendering = new();                     // guarded by _agendaLock
    private static readonly Dictionary<string, ID2D1Bitmap> _agendaSprites = new();      // render thread only
    private static readonly List<AgendaDraw> _agendaDraws = new();                        // render thread only
    private static readonly List<string> _agendaStale = new();                            // render thread only
    private static ID2D1SolidColorBrush? _agendaBrush;
    private static int _agendaBuiltVersion = -1;
    private static long _agendaBuiltSecond = long.MinValue;
    private static int _agendaBuiltWidth, _agendaBuiltHeight;

    /// <summary>Set by DrawShowEvents each frame: a Blackout covers this seat, so the agenda stays hidden under it.</summary>
    private static bool _agendaHiddenByEvent;

    /// <summary>stdin thread: keep the latest agenda (null = none).</summary>
    private static void HandleAgendaCommand(PlayerCommandAgenda cmd)
    {
        lock (_agendaLock)
        {
            _agenda = cmd.Agenda;
            _agendaFullScreen = cmd.FullScreen;
            _agendaVersion++;
        }
        _logger?.LogInformation("[Agenda] enabled={Enabled}, {Count} item(s), fullScreen={Full}",
            cmd.Agenda?.Enabled ?? false, cmd.Agenda?.Items.Count ?? 0, cmd.FullScreen);
    }

    /// <summary>Render thread: draw the agenda for <paramref name="nowUtcMs"/> (live: now; exact frame: the probe's time).</summary>
    private static void DrawAgenda(long nowUtcMs)
    {
        if (_d2dContext == null) return;
        Agenda? agenda;
        bool full;
        int version;
        lock (_agendaLock)
        {
            agenda = _agenda;
            full = _agendaFullScreen;
            version = _agendaVersion;
        }
        if (agenda == null || !agenda.Enabled || _agendaHiddenByEvent || _width <= 0 || _height <= 0) return;

        long second = nowUtcMs / 1000;
        if (version != _agendaBuiltVersion || second != _agendaBuiltSecond || _width != _agendaBuiltWidth || _height != _agendaBuiltHeight)
        {
            BuildAgendaDraws(agenda, full, nowUtcMs);
            _agendaBuiltVersion = version;
            _agendaBuiltSecond = second;
            _agendaBuiltWidth = _width;
            _agendaBuiltHeight = _height;
        }
        UploadAgendaSprites();

        _agendaBrush ??= _d2dContext.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        var prior = _d2dContext.Transform;
        _d2dContext.Transform = System.Numerics.Matrix3x2.Identity;   // device pixels, not the canvas scale
        try
        {
            float pulse = 0.15f + 0.15f * MathF.Sin(2f * MathF.PI * 0.5f * (nowUtcMs % 2000) / 1000f);
            foreach (var d in _agendaDraws)
            {
                var rect = new RectangleF(d.Rect.X, d.Rect.Y, d.Rect.W, d.Rect.H);
                switch (d.Kind)
                {
                    case AgendaDrawKind.Fill:
                    case AgendaDrawKind.PulseFill:
                        _agendaBrush.Color = d.Color;
                        _agendaBrush.Opacity = d.Kind == AgendaDrawKind.PulseFill ? pulse : d.Opacity;
                        _d2dContext.FillRectangle(rect, _agendaBrush);
                        break;
                    case AgendaDrawKind.Text:
                    case AgendaDrawKind.TextRight:
                        if (d.Key == null || !_agendaSprites.TryGetValue(d.Key, out var bmp)) break;   // not rendered yet
                        var size = bmp.Size;
                        if (size.Width <= 0 || size.Height <= 0) break;
                        float h = rect.Height * 0.8f;
                        float w = Math.Min(h * size.Width / size.Height, rect.Width);
                        float x = d.Kind == AgendaDrawKind.TextRight ? rect.Right - w : rect.X;
                        _d2dContext.DrawBitmap(bmp, new RectangleF(x, rect.Y + (rect.Height - h) / 2f, w, h), d.Opacity, BitmapInterpolationMode.Linear, null);
                        break;
                }
            }
        }
        finally
        {
            _d2dContext.Transform = prior;
        }
    }

    /// <summary>The draw list for this second: card / dim, highlight, texts. Requests every text sprite it needs and drops the rest.</summary>
    private static void BuildAgendaDraws(Agenda a, bool full, long nowUtcMs)
    {
        _agendaDraws.Clear();
        var notes = a.Notes.Take(MaxAgendaNotes).ToList();
        if (full) BuildAgendaScreen(a, notes, nowUtcMs);
        else BuildAgendaPanel(a, notes, nowUtcMs);

        var used = new HashSet<string>(_agendaDraws.Where(d => d.Key != null).Select(d => d.Key!));
        _agendaStale.Clear();
        foreach (var key in _agendaSprites.Keys) if (!used.Contains(key)) _agendaStale.Add(key);
        foreach (var key in _agendaStale)
        {
            _agendaSprites[key].Dispose();
            _agendaSprites.Remove(key);
        }
        lock (_agendaLock)
        {
            foreach (var key in _agendaRendered.Keys.Where(k => !used.Contains(k)).ToList())
            {
                _agendaRendered[key].Dispose();
                _agendaRendered.Remove(key);
            }
        }
        foreach (var key in used) RequestAgendaSprite(key);
    }

    private static void BuildAgendaPanel(Agenda a, List<string> notes, long nowUtcMs)
    {
        var rows = AgendaMath.Rows(a, nowUtcMs);
        bool hasTitle = !string.IsNullOrWhiteSpace(a.Title);
        int rowCount = Math.Max(1, rows.Count);
        var layout = AgendaLayout.Panel(_width, _height, a.Corner, hasTitle, rowCount, notes.Count);
        _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.Fill, layout.Card, null, AgendaCard, 0.85f));
        if (hasTitle) _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.Text, layout.Title, Key(AgendaGold, a.Title.Trim()), default, 1f));
        if (rows.Count == 0)
            _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.Text, layout.Rows[0], Key(AgendaWhite, "No items yet"), default, 0.5f));
        for (int i = 0; i < rows.Count; i++) AddAgendaRow(rows[i], layout.Rows[i], withDay: rows[i].DayHeader);
        for (int i = 0; i < notes.Count; i++)
            _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.Text, layout.Notes[i], Key(AgendaWhite, notes[i]), default, 0.75f));
    }

    private static void BuildAgendaScreen(Agenda a, List<string> notes, long nowUtcMs)
    {
        var days = AgendaMath.Days(a, nowUtcMs);
        int slots = 1 + Math.Min(MaxAgendaRowsPerDay, days.Count == 0 ? 1 : days.Max(d => d.Rows.Count));
        var layout = AgendaLayout.Screen(_width, _height, Math.Max(1, days.Count), slots, notes.Count);
        _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.Fill, layout.Card, null, new Color4(0f, 0f, 0f, 1f), 0.6f));
        if (!string.IsNullOrWhiteSpace(a.Title))
            _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.Text, layout.Title, Key(AgendaGold, a.Title.Trim()), default, 1f));
        for (int c = 0; c < days.Count; c++)
        {
            _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.Text, layout.Rows[c * slots], Key(AgendaGold, days[c].Day), default, 1f));
            var rows = days[c].Rows;
            for (int r = 0; r < rows.Count && r < slots - 1; r++)
                AddAgendaRow(rows[r], layout.Rows[c * slots + 1 + r], withDay: false);
        }
        for (int i = 0; i < notes.Count; i++)
            _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.Text, layout.Notes[i], Key(AgendaWhite, notes[i]), default, 0.75f));
    }

    /// <summary>One row: highlight (current) / pulse (next soon), "Sat 18:00  Pizza" left, countdown or "now" right.</summary>
    private static void AddAgendaRow(AgendaRow row, AgendaRect rect, bool withDay)
    {
        if (row.State == AgendaRowState.Current)
            _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.Fill, rect, null, AgendaHighlight, 0.25f));
        else if (row.Pulse)
            _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.PulseFill, rect, null, AgendaHighlight, 0f));
        float opacity = row.State == AgendaRowState.Past ? 0.5f : 1f;
        string right = row.State == AgendaRowState.Current ? "now" : row.Countdown ?? "";
        var leftRect = right.Length > 0 ? rect with { W = rect.W * 0.72f } : rect;
        string left = withDay ? $"{row.Day}  {row.Time}  {row.Text}" : $"{row.Time}  {row.Text}";
        _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.Text, leftRect, Key(AgendaWhite, left), default, opacity));
        if (right.Length > 0)
            _agendaDraws.Add(new AgendaDraw(AgendaDrawKind.TextRight, rect with { X = rect.X + rect.W * 0.74f, W = rect.W * 0.26f },
                Key(row.State == AgendaRowState.Current ? AgendaGold : AgendaWhite, right), default, opacity));
    }

    private static string Key(string color, string text) => color + "\u0001" + text;

    /// <summary>Render the text sprite of <paramref name="key"/> on a worker unless it is uploaded, rendered or rendering.</summary>
    private static void RequestAgendaSprite(string key)
    {
        if (_agendaSprites.ContainsKey(key)) return;
        lock (_agendaLock)
        {
            if (_agendaRendered.ContainsKey(key) || !_agendaRendering.Add(key)) return;
        }
        int split = key.IndexOf('\u0001');
        string color = key[..split], text = key[(split + 1)..];
        _ = Task.Run(() =>
        {
            Bitmap? bmp = null;
            try { bmp = TextSprite.Render(text, color); }
            catch (Exception ex) { _logger?.LogWarning(ex, "[Agenda] Could not render \"{Text}\"", text); }
            lock (_agendaLock)
            {
                _agendaRendering.Remove(key);
                if (bmp != null) _agendaRendered[key] = bmp;
            }
        });
    }

    /// <summary>Render thread: rendered text bitmaps → GPU.</summary>
    private static void UploadAgendaSprites()
    {
        List<KeyValuePair<string, Bitmap>>? ready = null;
        lock (_agendaLock)
        {
            if (_agendaRendered.Count > 0)
            {
                ready = _agendaRendered.ToList();   // a few times a minute, not per frame
                _agendaRendered.Clear();
            }
        }
        if (ready == null) return;
        foreach (var (key, bmp) in ready)
        {
            using (bmp)
            {
                try
                {
                    if (_agendaSprites.Remove(key, out var old)) old.Dispose();
                    _agendaSprites[key] = UploadBitmapToD2D(bmp);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Agenda] Could not upload a text sprite");
                }
            }
        }
    }

    /// <summary>Exit / final cleanup only (the D2D device survives scene loads).</summary>
    private static void DisposeAgendaResources()
    {
        _agendaBrush?.Dispose();
        _agendaBrush = null;
        foreach (var s in _agendaSprites.Values) s.Dispose();
        _agendaSprites.Clear();
    }
}
