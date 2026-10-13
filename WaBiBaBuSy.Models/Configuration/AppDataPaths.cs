namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// The settings folder (%APPDATA%\WaBiBaBuSy). Unit tests point it at a temp folder so they never rewrite real
/// settings — every store under it (config, topology, seat map, show events, presets, playlists) must build its
/// default path from here (review 2026-10-13 E14: four of them used %APPDATA% directly).
/// </summary>
public static class AppDataPaths
{
    public static string Root { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaBiBaBuSy");
}
