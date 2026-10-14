namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>The cards for one Suggest / Shuffle press, plus an optional info line.</summary>
public sealed record SuggestionSet(IReadOnlyList<Suggestion> Cards, string? Note);

/// <summary>Picks which recipes become cards (spec 2026-10-14 §2.4). Pure in its inputs.</summary>
public static class SceneSuggester
{
    /// <summary>Cards per press.</summary>
    public const int CardCount = 4;

    /// <summary>Shown for a full-screen file picked together with smaller ones.</summary>
    public const string LayersNote = "big + small together needs scene layers";

    /// <summary>Shown when a video is picked together with other files (only Showcase takes videos).</summary>
    public const string VideoNote = "videos are shown on their own; pick images only for moving sprites";

    /// <summary>The best-scoring recipes (ties in <see cref="SceneRecipes.All"/> order).</summary>
    public static SuggestionSet Suggest(SelectionProfile s, CrossScreenConfig baseScene, int seed)
    {
        return Build(s, baseScene, seed, Ranked(s).Take(CardCount));
    }

    /// <summary>The recipes offered for <paramref name="s"/> (score &gt; 0), best first; ties in <see cref="SceneRecipes.All"/> order.</summary>
    public static IReadOnlyList<ISceneRecipe> Ranked(SelectionProfile s) =>
        Offered(s).OrderByDescending(x => x.score).ThenBy(x => x.index).Select(x => x.recipe).ToList();

    /// <summary>Build one recipe's scene for <paramref name="s"/>, with the cm twins filled for physical-unit rooms.</summary>
    public static Suggestion BuildCard(ISceneRecipe recipe, SelectionProfile s, CrossScreenConfig baseScene, int seed) =>
        WithCentimetres(recipe.Build(s, baseScene, seed), s.PixelsPerCm);

    /// <summary>
    /// Recipes drawn by score, preferring ones not in <paramref name="shownRecipeNames"/>; shown ones fill up when
    /// fewer than <see cref="CardCount"/> fresh ones exist. A new seed varies a repeated recipe.
    /// </summary>
    public static SuggestionSet Shuffle(SelectionProfile s, CrossScreenConfig baseScene, int seed, IReadOnlyCollection<string> shownRecipeNames)
    {
        var offered = Offered(s);
        var fresh = offered.Where(x => !shownRecipeNames.Contains(x.recipe.Name)).ToList();
        var picked = new List<ISceneRecipe>();
        for (int draw = 0; picked.Count < CardCount && fresh.Count > 0; draw++)
        {
            double target = SuggestKit.Unit(seed, 1000 + draw) * fresh.Sum(x => x.score);
            int at = 0;
            for (double run = fresh[0].score; run <= target && at < fresh.Count - 1; run += fresh[++at].score) { }
            picked.Add(fresh[at].recipe);
            fresh.RemoveAt(at);
        }
        picked.AddRange(offered.Where(x => !picked.Contains(x.recipe))
            .OrderByDescending(x => x.score).ThenBy(x => x.index)
            .Take(CardCount - picked.Count).Select(x => x.recipe));
        return Build(s, baseScene, seed, picked);
    }

    private static List<(ISceneRecipe recipe, double score, int index)> Offered(SelectionProfile s) =>
        SceneRecipes.All.Select((r, i) => (recipe: r, score: r.Score(s), index: i)).Where(x => x.score > 0).ToList();

    private static SuggestionSet Build(SelectionProfile s, CrossScreenConfig baseScene, int seed, IEnumerable<ISceneRecipe> recipes) =>
        new(recipes.Select((r, i) => BuildCard(r, s, baseScene, PatternLayout.Hash3(seed, i, 7))).ToList(),
            s.Mix == SelectionMix.BigPlusSmall && s.Big?.IsPanorama != true ? LayersNote
            : s.HasVideoSprite && s.Count > 1 ? VideoNote
            : null);

    /// <summary>
    /// Fill the cm twins of the pixel size and speed, so a physical-unit room (which shows and uses the cm values)
    /// gets the same card as a pixel room. Unknown DPI leaves the defaults.
    /// </summary>
    private static Suggestion WithCentimetres(Suggestion suggestion, float pixelsPerCm)
    {
        if (pixelsPerCm <= 0f) return suggestion;
        foreach (var layer in suggestion.Scene.Layers)
        {
            layer.Animation.TargetHeightCm = PhysicalUnits.PxToCm(layer.Animation.TargetHeight, pixelsPerCm);
            layer.Movement.SpeedCmPerSecond = PhysicalUnits.PxToCm(layer.Movement.SpeedPixelsPerSecond, pixelsPerCm);
        }
        return suggestion;
    }
}
