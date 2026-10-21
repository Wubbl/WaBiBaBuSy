using System;
using System.IO;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Core.Services.Show;

/// <summary>Which images a BiBaBo game uses — the bomb logo, the explosion and the beer (server and test mode alike).</summary>
public static class BombLogo
{
    /// <summary>The shipped logo's timer box (dx, dy, w, h in bomb sizes), as printed by BiBaBu tools/bibabo/bomb_art.py.</summary>
    private static readonly float[] ShippedTimerBox = { -0.1715f, 0.3319f, 0.1260f, 0.0643f };

    /// <summary>Outcome animations' size in bomb sizes (the beer starts with the logo at exactly 1 bomb size: beer_art.py).</summary>
    private const float BoomScale = 2.2f, BeerScale = 1.6f;

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

    /// <summary>%APPDATA%\WaBiBaBuSy\<paramref name="name"/>, else the shipped Content\<paramref name="name"/>; null = neither.</summary>
    private static string? Find(string name)
    {
        foreach (var dir in new[] { AppDataPaths.Root, System.IO.Path.Combine(AppContext.BaseDirectory, "Content") })
        {
            var path = System.IO.Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// The image a BombOutcome carries and its <see cref="BombGame.OutcomeImageScale"/>: the explosion on BOOM
    /// (bibabo-boom.gif; none = the drawn shockwave alone), the beer on DEFUSED (bibabo-beer.gif — it starts with the
    /// shipped logo, so only with that logo), else the bomb's logo.
    /// </summary>
    public static (string? Path, float Scale) OutcomeImage(BombResult result, string? bombImagePath, bool shippedLogo)
    {
        if (result is BombResult.WrongCable or BombResult.Timeout)
            return Find("bibabo-boom.gif") is { } boom ? (boom, BoomScale) : (null, 0f);
        if (result == BombResult.Defused && shippedLogo && Find("bibabo-beer.gif") is { } beer)
            return (beer, BeerScale);
        return (bombImagePath, 0f);
    }
}
