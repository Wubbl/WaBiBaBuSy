using System.Text.Json;
using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// JSON persistence for <see cref="ShowEventSettings"/> (<c>%APPDATA%\WaBiBaBuSy\show-events.json</c>).
/// Missing or unreadable file → defaults.
/// </summary>
public static class ShowEventSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WaBiBaBuSy", "show-events.json");

    public static ShowEventSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ShowEventSettings>(File.ReadAllText(path), JsonOptions) ?? new ShowEventSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ShowEventSettingsStore] Could not read {path}: {ex.Message}. Using defaults.");
        }
        return new ShowEventSettings();
    }

    public static void Save(ShowEventSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ShowEventSettingsStore] Could not write {path}: {ex.Message}");
        }
    }
}
