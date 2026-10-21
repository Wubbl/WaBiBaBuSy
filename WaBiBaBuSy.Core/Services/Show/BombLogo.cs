using System;
using System.IO;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Core.Services.Show;

/// <summary>Which images a BiBaBo game uses — the bomb logo and the explosion (server and test mode alike).</summary>
public static class BombLogo
{
    /// <summary>The shipped logo's timer box (fractions of the image), as printed by BiBaBu tools/bibabo/bomb_art.py.</summary>
    private static readonly float[] ShippedTimerBox = { 0.2783f, 0.8612f, 0.1629f, 0.0665f };

    /// <summary>
    /// %APPDATA%\WaBiBaBuSy\bibabo-bomb.gif (or .png), else the shipped Content\bibabo-bomb.gif with its timer box;
    /// Path null = the players draw a bomb.
    /// </summary>
    public static (string? Path, float[] TimerBox) Resolve()
    {
        foreach (var name in new[] { "bibabo-bomb.gif", "bibabo-bomb.png" })
        {
            var path = System.IO.Path.Combine(AppDataPaths.Root, name);
            if (File.Exists(path)) return (path, Array.Empty<float>());
        }
        var shipped = System.IO.Path.Combine(AppContext.BaseDirectory, "Content", "bibabo-bomb.gif");
        return File.Exists(shipped) ? (shipped, ShippedTimerBox) : (null, Array.Empty<float>());
    }

    /// <summary>
    /// The explosion played once on BOOM: %APPDATA%\WaBiBaBuSy\bibabo-boom.gif, else the shipped Content\bibabo-boom.gif;
    /// null = none (the drawn shockwave and fire alone).
    /// </summary>
    public static string? BoomPath()
    {
        foreach (var dir in new[] { AppDataPaths.Root, System.IO.Path.Combine(AppContext.BaseDirectory, "Content") })
        {
            var path = System.IO.Path.Combine(dir, "bibabo-boom.gif");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>The image a BombOutcome carries: the explosion for BOOM, else the bomb's logo (drawn defused / recycled).</summary>
    public static string? OutcomeImage(BombResult result, string? bombImagePath) =>
        result is BombResult.WrongCable or BombResult.Timeout ? BoomPath() : bombImagePath;
}
