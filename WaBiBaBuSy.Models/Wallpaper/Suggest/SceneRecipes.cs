namespace WaBiBaBuSy.Models.Wallpaper.Suggest;

/// <summary>One kind of suggested scene: how well it fits a selection, and how to build it.</summary>
public interface ISceneRecipe
{
    /// <summary>Card title, unique.</summary>
    string Name { get; }
    /// <summary>0 = not offered for this selection, else 0..1 (higher = better fit).</summary>
    double Score(SelectionProfile s);
    /// <summary>Build the scene; pure in (selection, base scene, seed).</summary>
    Suggestion Build(SelectionProfile s, CrossScreenConfig baseScene, int seed);
}

/// <summary>A built card: recipe name, the one-line reason, the scene.</summary>
public sealed record Suggestion(string RecipeName, string Why, CrossScreenConfig Scene);

/// <summary>The recipe set (spec 2026-10-14 §2.2), modelled on the starter presets in <c>TestScenarios/showcase/scenes</c>.</summary>
public static class SceneRecipes
{
    /// <summary>All recipes in a fixed order (ties in score are broken by this order).</summary>
    public static IReadOnlyList<ISceneRecipe> All { get; } = new ISceneRecipe[]
    {
        new DvdBounce(), new LogoConveyor(), new Marquee(), new Orbit(), new PulseGrid(),
        new Fireflies(), new Comet(), new Parade(), new Panorama(), new Showcase(),
    };

    // Shared helpers -------------------------------------------------------------------------

    /// <summary>Sprite recipes never take videos or a lone full-screen file.</summary>
    private static bool SpritesUsable(SelectionProfile s) =>
        !s.HasVideoSprite && !(s.Mix == SelectionMix.Single && s.Big != null);

    private static string MonoWhy(SelectionProfile s, string tinted) => s.AllMonochrome ? tinted : "keeps its own colours";

    private static string LoopWhy(SelectionProfile s) =>
        s.ShortestLoopMs is long ms ? $", timed to the {ms / 1000.0:0.#} s loop" : string.Empty;

    private static ColorGradingConfig Tint(SelectionProfile s, ColorGradingMode monoMode, double cyclesPerSecond, int seed)
    {
        if (!s.AllMonochrome) return new ColorGradingConfig { Mode = ColorGradingMode.None };
        var party = SuggestKit.PartyColors(seed);
        return new ColorGradingConfig
        {
            Mode = monoMode,
            CyclesPerSecond = SuggestKit.SnapCyclesPerSecond(s, cyclesPerSecond),
            ColorList = party.ToList(),
            GradientA = party[0],
            GradientB = party[2],
            Seed = (int)(SuggestKit.Unit(seed, 31) * 1000) + 1,
        };
    }

    // Recipes ---------------------------------------------------------------------------------

    /// <summary>Preset 02.</summary>
    private sealed class DvdBounce : ISceneRecipe
    {
        public string Name => "DVD Bounce";
        public double Score(SelectionProfile s) =>
            !SpritesUsable(s) || s.Sprites.Count != 1 ? 0
            : 0.6 + (s.AllSpritesSmall ? 0.2 : 0) + (s.AllSpritesHaveAlpha ? 0.2 : 0);
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites.Take(1).ToList(), 0.22);
            scene.Layers[0].Movement.Type = MovementType.Bounce;
            scene.Layers[0].Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 320, 480);
            scene.Layers[0].Movement.DirectionAngleDegrees = SuggestKit.Range(seed, 2, 25, 40);
            scene.Layers[0].Animation.ColorGrading = Tint(s, ColorGradingMode.CycleColorList, 0.25, seed);
            return new(Name, "Bounces off the edges, " + MonoWhy(s, "cycles party colours") + LoopWhy(s), scene);
        }
    }

    /// <summary>Preset 01.</summary>
    private sealed class LogoConveyor : ISceneRecipe
    {
        public string Name => "Logo Conveyor";
        public double Score(SelectionProfile s) => !SpritesUsable(s) ? 0 : s.AllMonochrome ? 0.7 : 0.5;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites, 0.13);
            scene.Layers[0].Animation.Pattern = new PatternConfig { Sizing = PatternConfig.SizingMode.Fill, SpacingX = 80, SpacingY = 80, Seed = seed };
            scene.Layers[0].Movement.Type = MovementType.Linear;
            scene.Layers[0].Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 150, 220);
            scene.Layers[0].Movement.DirectionAngleDegrees = 0;
            scene.Layers[0].Movement.Endless = s.AllMonochrome; // Endless needs a Traveling colour mode
            scene.Layers[0].Animation.ColorGrading = Tint(s, ColorGradingMode.TravelingRainbow, 0.1, seed);
            return new(Name, "A belt of logos across all screens, " + MonoWhy(s, "every one its own colour"), scene);
        }
    }

    /// <summary>Preset 03, flattened for wordmarks.</summary>
    private sealed class Marquee : ISceneRecipe
    {
        public string Name => "Marquee";
        public double Score(SelectionProfile s) =>
            SpritesUsable(s) && s.Sprites.All(f => f.Aspect == AspectClass.Wide) ? 0.9 : 0;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites, 0.15);
            scene.Layers[0].Movement.Type = MovementType.SineWave;
            scene.Layers[0].Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 200, 300);
            scene.Layers[0].Movement.WaveAmplitudePixels = (float)(0.08 * s.ScreenHeight);
            scene.Layers[0].Movement.WaveFrequencyHz = (float)SuggestKit.SnapCyclesPerSecond(s, 0.2);
            scene.Layers[0].Movement.DirectionAngleDegrees = 0;
            scene.Layers[0].Animation.ColorGrading = Tint(s, ColorGradingMode.Gradient, 0.15, seed);
            return new(Name, "Wide wordmark scrolls on a gentle wave, " + MonoWhy(s, "with a colour gradient"), scene);
        }
    }

    /// <summary>Preset 06.</summary>
    private sealed class Orbit : ISceneRecipe
    {
        public string Name => "Orbit";
        public double Score(SelectionProfile s) =>
            !SpritesUsable(s) ? 0
            : s.Sprites.Count == 1 ? (s.Sprites[0].Aspect == AspectClass.Square ? 0.7 : 0.4) + (s.AllSpritesHaveAlpha ? 0.1 : 0)
            : 0.4;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites, 0.2);
            scene.Layers[0].Movement.Type = MovementType.Circular;
            scene.Layers[0].Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 250, 350);
            scene.Layers[0].Movement.OrbitRadiusPixels = (float)(0.3 * s.ScreenHeight);
            scene.Layers[0].Movement.NodePhaseDelayMs = 500;
            scene.Layers[0].Animation.FaceTravelDirection = true;
            if (s.Sprites.Count == 1) // effects need a single image
            {
                double turnsPerSecond = SuggestKit.SnapCyclesPerSecond(s, 0.25);
                float sign = SuggestKit.Unit(seed, 5) < 0.5 ? -1 : 1;
                scene.Layers[0].Animation.Effects = new SpriteEffectsConfig { SpinDegPerSecond = sign * (float)(360 * turnsPerSecond) };
            }
            scene.Layers[0].Animation.ColorGrading = Tint(s, ColorGradingMode.Rainbow, 0.1, seed);
            return new(Name, "Circles the screen" + (s.Sprites.Count == 1 ? " and spins" : string.Empty) + LoopWhy(s), scene);
        }
    }

    /// <summary>Preset 10.</summary>
    private sealed class PulseGrid : ISceneRecipe
    {
        public string Name => "Pulse Grid";
        public double Score(SelectionProfile s)
        {
            if (!SpritesUsable(s) || s.Sprites.Any(f => f.Size == SizeClass.FullScreen)) return 0;
            double score = 0.4 + (s.Sprites.Count >= 2 ? 0.4 : 0) + (s.AllSpritesSmall ? 0.1 : 0);
            return s.AllSpritesHaveAlpha ? score : Math.Min(score, 0.3);
        }
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites, 0.1);
            scene.Layers[0].Animation.Pattern = new PatternConfig { Sizing = PatternConfig.SizingMode.Fill, SpacingX = 60, SpacingY = 60, Seed = seed };
            scene.Layers[0].Movement.Type = MovementType.Static;
            scene.Layers[0].Animation.ColorGrading = Tint(s, ColorGradingMode.Rainbow, 0.08, seed);
            string spread = s.Sprites.Count > 1 ? $"{s.Sprites.Count} files spread over a grid" : "A grid of copies";
            return new(Name, spread + ", " + MonoWhy(s, "rainbow colour wave") + LoopWhy(s), scene);
        }
    }

    /// <summary>Preset 07.</summary>
    private sealed class Fireflies : ISceneRecipe
    {
        public string Name => "Fireflies";
        public double Score(SelectionProfile s)
        {
            if (!SpritesUsable(s) || s.Sprites.Any(f => f.Size == SizeClass.FullScreen)) return 0;
            double score = (s.AllSpritesSmall ? 0.6 : 0.3) + (s.Sprites.Count >= 2 ? 0.2 : 0);
            return s.AllSpritesHaveAlpha ? score : Math.Min(score, 0.3);
        }
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var copies = Enumerable.Range(0, Math.Max(5, s.Sprites.Count)).Select(i => s.Sprites[i % s.Sprites.Count]).ToList();
            var scene = SuggestKit.NewScene(b, s, copies, 0.08);
            scene.Background.ColorHex = SuggestKit.BackgroundHex(s, forceDark: true);
            scene.Layers[0].Animation.MultiImageSpread = (float)(0.8 * s.ScreenWidth);
            scene.Layers[0].Animation.MultiImagePhaseJitterMs = 2500;
            scene.Layers[0].Movement.Type = MovementType.RandomWalk;
            scene.Layers[0].Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 150, 250);
            scene.Layers[0].Movement.RandomSeed = (int)(SuggestKit.Unit(seed, 9) * 100000);
            scene.Layers[0].Movement.RandomStepIntervalMs = 1800;
            scene.Layers[0].Movement.IterationStepCount = 0;
            scene.Layers[0].Animation.ColorGrading = s.AllMonochrome
                ? new ColorGradingConfig
                {
                    Mode = ColorGradingMode.RandomColors,
                    CyclesPerSecond = SuggestKit.SnapCyclesPerSecond(s, 0.3),
                    ColorList = new() { "#FFE066", "#FFB703", "#FB8500", "#C6FF6B", "#FFF3B0" },
                    Seed = (int)(SuggestKit.Unit(seed, 31) * 1000) + 1,
                }
                : new ColorGradingConfig { Mode = ColorGradingMode.None };
            return new(Name, "Drifting swarm on a dark background, " + MonoWhy(s, "glowing warm colours"), scene);
        }
    }

    /// <summary>Preset 13.</summary>
    private sealed class Comet : ISceneRecipe
    {
        public string Name => "Comet";
        public double Score(SelectionProfile s) =>
            !SpritesUsable(s) || s.Sprites.Count != 1 ? 0
            : !s.AllSpritesHaveAlpha ? 0.2
            : s.AllSpritesSmall ? 0.9 : 0.8;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites.Take(1).ToList(), 0.12);
            scene.Background.ColorHex = SuggestKit.BackgroundHex(s, forceDark: true);
            scene.Layers[0].Movement.Type = MovementType.Linear;
            scene.Layers[0].Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 600, 900);
            scene.Layers[0].Movement.DirectionAngleDegrees = SuggestKit.Range(seed, 2, 10, 25);
            scene.Layers[0].Animation.ColorGrading = Tint(s, ColorGradingMode.Rainbow, 0.5, seed);
            scene.Layers[0].Animation.Effects = new SpriteEffectsConfig
            {
                Trail = new TrailConfig { Count = 8, SpacingMs = 70, StartOpacity = 0.55f, ScaleFalloff = 0.9f, UseGradingColor = s.AllMonochrome },
                Pulse = new PulseConfig { Amplitude = 0.08f, FrequencyHz = (float)SuggestKit.SnapCyclesPerSecond(s, 1.2) },
                Entry = BezelEffect.Ripple,
                Exit = BezelEffect.Glow,
                BezelColorHex = s.SharedPalette.Count > 0 ? s.SharedPalette[0] : "#7FDBFF",
                BezelDurationMs = 600,
            };
            return new(Name, "Shoots across with a trail" + (s.AllMonochrome ? " in rainbow colours" : string.Empty), scene);
        }
    }

    /// <summary>Preset 05.</summary>
    private sealed class Parade : ISceneRecipe
    {
        public string Name => "Parade";
        public double Score(SelectionProfile s) =>
            !SpritesUsable(s) ? 0
            : s.Mix switch
            {
                SelectionMix.Uniform => 0.8,
                SelectionMix.BigPlusSmall => s.Sprites.Count >= 2 ? 0.5 : 0.3,
                SelectionMix.Mixed => 0.4,
                _ => 0.3,
            };
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var scene = SuggestKit.NewScene(b, s, s.Sprites, 0.18);
            scene.Layers[0].Animation.Pattern = new PatternConfig
            {
                Sizing = PatternConfig.SizingMode.Fill, SpacingX = 140, SpacingY = (float)(0.5 * s.ScreenHeight),
                RandomOffsetMaxPx = 30, Seed = seed,
            };
            scene.Layers[0].Movement.Type = MovementType.Linear;
            scene.Layers[0].Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 200, 260);
            scene.Layers[0].Movement.DirectionAngleDegrees = 0;
            scene.Layers[0].Movement.Reversed = SuggestKit.Unit(seed, 4) < 0.5;
            scene.Layers[0].Animation.ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.None };
            return new(Name, s.Sprites.Count > 1 ? $"{s.Sprites.Count} files march in a row" : "A row of copies marches by", scene);
        }
    }

    /// <summary>A panorama spans the room as the background; picked sprites ride across it.</summary>
    private sealed class Panorama : ISceneRecipe
    {
        public string Name => "Panorama";
        public double Score(SelectionProfile s) => s.Big?.IsPanorama == true && !s.HasVideoSprite ? 1.0 : 0;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var pano = s.Big ?? s.Files[0];
            bool riders = s.Mix == SelectionMix.BigPlusSmall && s.Sprites.Count > 0;
            var scene = riders ? SuggestKit.NewScene(b, s, s.Sprites, 0.15) : new CrossScreenConfig { SelectedMonitorIds = new List<string>(b.SelectedMonitorIds) };
            scene.Background = new BackgroundLayerConfig { Mode = BackgroundMode.Panorama, ImagePath = pano.Path, ColorHex = "#000000" };
            string name = Path.GetFileName(pano.Path);
            if (!riders) return new(Name, $"Spans {name} across all screens", scene);

            var layer = scene.Layers[0];
            layer.Distribution = AnimationDistributionMode.Sequential;   // the riders cross the same room the picture spans
            layer.Movement.Type = MovementType.Linear;
            layer.Movement.SpeedPixelsPerSecond = SuggestKit.Range(seed, 1, 200, 300);
            layer.Movement.DirectionAngleDegrees = 0;
            layer.Animation.ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.None };
            return new(Name, $"Spans {name} across all screens, " + (s.Sprites.Count > 1 ? $"{s.Sprites.Count} files ride" : "the sprite rides") + " across it", scene);
        }
    }

    /// <summary>The calm "just show it" option for full-screen files and videos.</summary>
    private sealed class Showcase : ISceneRecipe
    {
        public string Name => "Showcase";
        public double Score(SelectionProfile s) =>
            s.Big?.IsPanorama == true ? (s.HasVideoSprite ? 0.6 : 0)   // one screen would squeeze the strip: Panorama shows it
            : s.Big != null ? (s.Mix == SelectionMix.BigPlusSmall ? 0.9 : 1.0)
            : s.Count == 1 && s.Files[0].Kind == MediaKind.Video ? 0.8
            : s.HasVideoSprite ? 0.6 // sprite recipes refuse videos: show the video on its own
            : 0;
        public Suggestion Build(SelectionProfile s, CrossScreenConfig b, int seed)
        {
            var file = (s.Big?.IsPanorama == true ? null : s.Big) ?? s.Sprites.FirstOrDefault(f => f.Kind == MediaKind.Video) ?? s.Files[0];
            var scene = SuggestKit.NewScene(b, s, new[] { file }, 1.0);
            scene.Background.ColorHex = "#000000";
            scene.Layers[0].Animation.FitMode = ContentFitMode.Fill;
            scene.Layers[0].Movement.Type = MovementType.Static;
            scene.Layers[0].Animation.ColorGrading = new ColorGradingConfig { Mode = ColorGradingMode.None };
            return new(Name, $"Shows {Path.GetFileName(file.Path)} full screen, as it is", scene);
        }
    }
}
