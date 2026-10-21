using System.Text.Json;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Models.Configuration;

/// <summary>What the Agenda tab edits: the text as typed (re-parsed on every build), persisted as is.</summary>
public sealed class AgendaDocument
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = "";

    /// <summary>"Fri 18:00 Pizza" lines (<see cref="AgendaText"/>).</summary>
    public string ItemsText { get; set; } = "";

    public string NotesText { get; set; } = "";
    public DateOnly FirstDay { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public AgendaCorner Corner { get; set; } = AgendaCorner.TopRight;

    /// <summary>The agenda to send: items parsed in <paramref name="tz"/>, notes = the non-blank lines.</summary>
    public Agenda Build(TimeZoneInfo tz, IReadOnlyList<string> screenNodeIds) => new()
    {
        Enabled = Enabled,
        Title = Title.Trim(),
        Items = AgendaText.Parse(ItemsText, FirstDay, tz),
        Notes = (NotesText ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList(),
        Corner = Corner,
        UtcOffsetMinutes = (int)tz.GetUtcOffset(DateTime.UtcNow).TotalMinutes,
        ScreenNodeIds = screenNodeIds.ToList(),
    };
}

/// <summary>Server-side JSON of the agenda (<c>%APPDATA%\WaBiBaBuSy\agenda.json</c>).</summary>
public static class AgendaStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "agenda.json");

    /// <summary>Load from disk; a missing or unreadable file yields an empty, disabled agenda.</summary>
    public static AgendaDocument Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AgendaDocument>(File.ReadAllText(path), JsonOptions) ?? new AgendaDocument();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AgendaStore] Could not read {path}: {ex.Message}. Starting empty.");
        }
        return new AgendaDocument();
    }

    public static void Save(AgendaDocument doc, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
    }
}
