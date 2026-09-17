using System.Text.Json;
using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Topology;

namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// JSON persistence for the room's <see cref="SeatMap"/> (<c>%APPDATA%\WaBiBaBuSy\seatmap.json</c>).
/// Missing or unreadable file → the classic single-row wall, so existing setups are unchanged.
/// </summary>
public static class SeatMapStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WaBiBaBuSy", "seatmap.json");

    public static SeatMap Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var map = JsonSerializer.Deserialize<SeatMap>(File.ReadAllText(path), JsonOptions);
                if (map != null && map.Rows.Count > 0) return map;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SeatMapStore] Could not read {path}: {ex.Message}. Using single row.");
        }
        return SeatMap.SingleRow();
    }

    public static void Save(SeatMap map, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(map, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SeatMapStore] Could not write {path}: {ex.Message}");
        }
    }
}
