namespace WaBiBaBuSy.Models.Configuration;

/// <summary>The settings folder (%APPDATA%\WaBiBaBuSy). Unit tests point it at a temp folder so they never rewrite real settings.</summary>
internal static class AppDataPaths
{
    internal static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaBiBaBuSy");
}
