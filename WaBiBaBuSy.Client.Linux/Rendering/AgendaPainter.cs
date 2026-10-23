using Microsoft.Extensions.Logging;
using SkiaSharp;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Client.Linux.Rendering;

/// <summary>
/// The room's agenda (design 2026-10-21-agenda-overlay-design.md) on one monitor: a corner panel, or the full-size
/// timetable on an agenda screen, in device pixels after the show events. Skia port of the Windows player's
/// Program.Agenda.cs: rows / layout from AgendaMath and AgendaLayout, rebuilt once a second; text sprites rendered on
/// a worker. <see cref="Set"/> runs on any thread, <see cref="Draw"/> on the render thread.
/// </summary>
internal sealed class AgendaPainter : IDisposable
{
    private enum DrawKind { Fill, PulseFill, Text, TextRight }

    private readonly record struct AgendaDraw(DrawKind Kind, AgendaRect Rect, string? Key, SKColor Color, float Opacity);

    private const string White = "#FFFFFF";
    private const string Gold = "#FFD700";
    private static readonly SKColor Card = new(0x14, 0x14, 0x14);
    private static readonly SKColor Highlight = new(0xFF, 0xD7, 0x00);
    private const int MaxNotes = 3;
    private const int MaxRowsPerDay = 14;

    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Agenda? _agenda;                                          // guarded by _lock
    private bool _fullScreen;                                        // guarded by _lock
    private int _version;                                            // guarded by _lock
    private readonly Dictionary<string, SKImage> _sprites = new();  // guarded by _lock
    private readonly HashSet<string> _rendering = new();             // guarded by _lock
    private readonly List<AgendaDraw> _draws = new();                // render thread only
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private int _builtVersion = -1;
    private long _builtSecond = long.MinValue;
    private int _builtWidth, _builtHeight;

    public AgendaPainter(ILogger logger) => _logger = logger;

    /// <summary>True while an agenda is on (the host keeps a stand-in scene on idle monitors then).</summary>
    public bool IsOn { get { lock (_lock) return _agenda is { Enabled: true }; } }

    /// <summary>Keep the latest agenda (null = none). <paramref name="fullScreen"/>: this monitor is an agenda screen.</summary>
    public void Set(Agenda? agenda, bool fullScreen)
    {
        lock (_lock)
        {
            _agenda = agenda;
            _fullScreen = fullScreen;
            _version++;
        }
    }

    public void Draw(SKCanvas canvas, int width, int height, long nowUtcMs)
    {
        Agenda? agenda;
        bool full;
        int version;
        lock (_lock)
        {
            agenda = _agenda;
            full = _fullScreen;
            version = _version;
        }
        if (agenda == null || !agenda.Enabled || width <= 0 || height <= 0) return;

        long second = nowUtcMs / 1000;
        if (version != _builtVersion || second != _builtSecond || width != _builtWidth || height != _builtHeight)
        {
            Build(agenda, full, width, height, nowUtcMs);
            _builtVersion = version;
            _builtSecond = second;
            _builtWidth = width;
            _builtHeight = height;
        }

        float pulse = 0.15f + 0.15f * MathF.Sin(2f * MathF.PI * 0.5f * (nowUtcMs % 2000) / 1000f);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        foreach (var d in _draws)
        {
            var rect = SKRect.Create(d.Rect.X, d.Rect.Y, d.Rect.W, d.Rect.H);
            switch (d.Kind)
            {
                case DrawKind.Fill:
                case DrawKind.PulseFill:
                    _paint.Color = d.Color.WithAlpha(SpriteLayer.ToByte(d.Kind == DrawKind.PulseFill ? pulse : d.Opacity));
                    canvas.DrawRect(rect, _paint);
                    break;
                case DrawKind.Text:
                case DrawKind.TextRight:
                {
                    SKImage? img;
                    lock (_lock) _sprites.TryGetValue(d.Key!, out img);
                    if (img == null || img.Width <= 0 || img.Height <= 0) break;   // not rendered yet
                    float h = rect.Height * 0.8f;
                    float w = Math.Min(h * img.Width / img.Height, rect.Width);
                    float x = d.Kind == DrawKind.TextRight ? rect.Right - w : rect.Left;
                    _paint.Color = SKColors.White.WithAlpha(SpriteLayer.ToByte(d.Opacity));
                    canvas.DrawImage(img, SKRect.Create(x, rect.Top + (rect.Height - h) / 2f, w, h), sampling, _paint);
                    break;
                }
            }
        }
    }

    /// <summary>The draw list for this second; requests every text sprite it needs and drops the rest.</summary>
    private void Build(Agenda a, bool full, int width, int height, long nowUtcMs)
    {
        _draws.Clear();
        var notes = a.Notes.Take(MaxNotes).ToList();
        if (full) BuildScreen(a, notes, width, height, nowUtcMs);
        else BuildPanel(a, notes, width, height, nowUtcMs);

        var used = new HashSet<string>(_draws.Where(d => d.Key != null).Select(d => d.Key!));
        lock (_lock)
        {
            foreach (var key in _sprites.Keys.Where(k => !used.Contains(k)).ToList())
            {
                _sprites[key].Dispose();
                _sprites.Remove(key);
            }
        }
        foreach (var key in used) Request(key);
    }

    private void BuildPanel(Agenda a, List<string> notes, int width, int height, long nowUtcMs)
    {
        var rows = AgendaMath.Rows(a, nowUtcMs);
        bool hasTitle = !string.IsNullOrWhiteSpace(a.Title);
        var layout = AgendaLayout.Panel(width, height, a.Corner, hasTitle, Math.Max(1, rows.Count), notes.Count);
        _draws.Add(new AgendaDraw(DrawKind.Fill, layout.Card, null, Card, 0.85f));
        if (hasTitle) _draws.Add(new AgendaDraw(DrawKind.Text, layout.Title, Key(Gold, a.Title.Trim()), default, 1f));
        if (rows.Count == 0)
            _draws.Add(new AgendaDraw(DrawKind.Text, layout.Rows[0], Key(White, "No items yet"), default, 0.5f));
        for (int i = 0; i < rows.Count; i++) AddRow(rows[i], layout.Rows[i], withDay: rows[i].DayHeader);
        for (int i = 0; i < notes.Count; i++)
            _draws.Add(new AgendaDraw(DrawKind.Text, layout.Notes[i], Key(White, notes[i]), default, 0.75f));
    }

    private void BuildScreen(Agenda a, List<string> notes, int width, int height, long nowUtcMs)
    {
        var days = AgendaMath.Days(a, nowUtcMs);
        int slots = 1 + Math.Min(MaxRowsPerDay, days.Count == 0 ? 1 : days.Max(d => d.Rows.Count));
        var layout = AgendaLayout.Screen(width, height, Math.Max(1, days.Count), slots, notes.Count);
        _draws.Add(new AgendaDraw(DrawKind.Fill, layout.Card, null, SKColors.Black, 0.6f));
        if (!string.IsNullOrWhiteSpace(a.Title))
            _draws.Add(new AgendaDraw(DrawKind.Text, layout.Title, Key(Gold, a.Title.Trim()), default, 1f));
        for (int c = 0; c < days.Count; c++)
        {
            _draws.Add(new AgendaDraw(DrawKind.Text, layout.Rows[c * slots], Key(Gold, days[c].Day), default, 1f));
            var rows = days[c].Rows;
            for (int r = 0; r < rows.Count && r < slots - 1; r++)
                AddRow(rows[r], layout.Rows[c * slots + 1 + r], withDay: false);
        }
        for (int i = 0; i < notes.Count; i++)
            _draws.Add(new AgendaDraw(DrawKind.Text, layout.Notes[i], Key(White, notes[i]), default, 0.75f));
    }

    /// <summary>One row: highlight (current) / pulse (next soon), "Sat 18:00  Pizza" left, countdown or "now" right.</summary>
    private void AddRow(AgendaRow row, AgendaRect rect, bool withDay)
    {
        if (row.State == AgendaRowState.Current)
            _draws.Add(new AgendaDraw(DrawKind.Fill, rect, null, Highlight, 0.25f));
        else if (row.Pulse)
            _draws.Add(new AgendaDraw(DrawKind.PulseFill, rect, null, Highlight, 0f));
        float opacity = row.State == AgendaRowState.Past ? 0.5f : 1f;
        string right = row.State == AgendaRowState.Current ? "now" : row.Countdown ?? "";
        var leftRect = right.Length > 0 ? rect with { W = rect.W * 0.72f } : rect;
        string left = withDay ? $"{row.Day}  {row.Time}  {row.Text}" : $"{row.Time}  {row.Text}";
        _draws.Add(new AgendaDraw(DrawKind.Text, leftRect, Key(White, left), default, opacity));
        if (right.Length > 0)
            _draws.Add(new AgendaDraw(DrawKind.TextRight, rect with { X = rect.X + rect.W * 0.74f, W = rect.W * 0.26f },
                Key(row.State == AgendaRowState.Current ? Gold : White, right), default, opacity));
    }

    private static string Key(string color, string text) => color + "\u0001" + text;

    /// <summary>Render the text sprite of <paramref name="key"/> on a worker unless it is rendered or rendering.</summary>
    private void Request(string key)
    {
        lock (_lock)
        {
            if (_sprites.ContainsKey(key) || !_rendering.Add(key)) return;
        }
        int split = key.IndexOf('\u0001');
        string color = key[..split], text = key[(split + 1)..];
        _ = Task.Run(() =>
        {
            SKImage? img = null;
            try { img = TextSprite.Render(text, color); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Agenda] Could not render \"{Text}\"", text); }
            lock (_lock)
            {
                _rendering.Remove(key);
                if (img != null) _sprites[key] = img;
            }
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sprites.Values) s.Dispose();
            _sprites.Clear();
        }
        _paint.Dispose();
    }
}
