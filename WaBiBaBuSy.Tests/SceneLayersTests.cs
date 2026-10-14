using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Scene layers (roadmap 2.3): assets, per-layer resolution, the layer clock and the editor checks.</summary>
public class SceneLayersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-layers-{Guid.NewGuid():N}");

    public SceneLayersTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private string File(string name)
    {
        var p = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        return p;
    }

    private static SceneLayer Layer(string path, params string[] extra) => new()
    {
        Name = Path.GetFileNameWithoutExtension(path),
        Animation = new AnimationLayerConfig { AnimationPath = path, AdditionalAnimationPaths = extra.ToList() },
        Movement = new MovementConfig { Type = MovementType.Linear, SpeedPixelsPerSecond = 200 },
    };

    // ── assets ───────────────────────────────────────────────────────────────

    [Fact]
    public void CollectPaths_EveryLayer_BottomIsPrimary_DuplicatesOnce()
    {
        var scene = new CrossScreenConfig
        {
            Layers = { Layer(@"X:\logo.png"), Layer(@"X:\bubble.gif", @"X:\b2.png", @"X:\logo.png"), Layer(@"X:\fish.gif") },
            Background = new BackgroundLayerConfig { Mode = BackgroundMode.StretchedImage, ImagePath = @"X:\bg.jpg" },
        };

        var paths = SceneAssets.CollectPaths(scene);

        Assert.Equal(new[]
        {
            (@"X:\logo.png", AssetRole.Primary), (@"X:\bubble.gif", AssetRole.Additional), (@"X:\b2.png", AssetRole.Additional),
            (@"X:\fish.gif", AssetRole.Additional), (@"X:\bg.jpg", AssetRole.Background),
        }, paths);
    }

    [Fact]
    public void RewriteToLocal_MissingLayerFile_DisablesOnlyThatLayer()
    {
        var fishLocal = File("fish.gif");
        var logoLocal = File("logo.png");
        var scene = new CrossScreenConfig { Layers = { Layer(@"X:\logo.png"), Layer(@"X:\gone.gif"), Layer(@"X:\fish.gif", @"X:\missing.png") } };
        var map = new Dictionary<string, string> { [@"X:\logo.png"] = logoLocal, [@"X:\fish.gif"] = fishLocal };

        var local = SceneAssets.RewriteToLocal(scene, map);

        Assert.True(local.Layers[0].Enabled);
        Assert.Equal(logoLocal, local.Layers[0].Animation.AnimationPath);
        Assert.False(local.Layers[1].Enabled);
        Assert.True(local.Layers[2].Enabled);
        Assert.Equal(fishLocal, local.Layers[2].Animation.AnimationPath);
        // A missing extra image falls back to its own layer's file, never to another layer's.
        Assert.Equal(new[] { fishLocal }, local.Layers[2].Animation.AdditionalAnimationPaths);
        Assert.Equal(@"X:\gone.gif", scene.Layers[1].Animation.AnimationPath);   // input untouched
        Assert.True(scene.Layers[1].Enabled);
    }

    // ── cm → px per layer ────────────────────────────────────────────────────

    [Fact]
    public void ResolveScene_ResolvesEachLayer_KeepsDistributionAndPhase()
    {
        var cm = Layer(@"X:\fish.gif");
        cm.Animation.SizeUnit = SizeUnit.Centimeters; cm.Animation.TargetHeightCm = 10f;
        cm.Movement.SpeedUnit = SpeedUnit.CentimetersPerSecond; cm.Movement.SpeedCmPerSecond = 25f;
        cm.PhaseOffsetMs = 7000;
        var px = Layer(@"X:\logo.png");
        px.Distribution = AnimationDistributionMode.Simultaneous;
        px.Opacity = 0.5f;

        var resolved = PhysicalUnits.ResolveScene(new CrossScreenConfig { Layers = { cm, px } }, refPixelsPerCm: 40f);

        Assert.Equal(400, resolved.Layers[0].Animation.TargetHeight);
        Assert.Equal(1000f, resolved.Layers[0].Movement.SpeedPixelsPerSecond);
        Assert.Equal(SpeedUnit.PixelsPerSecond, resolved.Layers[0].Movement.SpeedUnit);
        Assert.Equal(7000, resolved.Layers[0].PhaseOffsetMs);
        Assert.Equal(200f, resolved.Layers[1].Movement.SpeedPixelsPerSecond);
        Assert.Equal(AnimationDistributionMode.Simultaneous, resolved.Layers[1].Distribution);
        Assert.Equal(0.5f, resolved.Layers[1].Opacity);
        Assert.Equal(SizeUnit.Centimeters, cm.Animation.SizeUnit);   // the authored scene keeps its units
    }

    [Fact]
    public void ApplyRoomUnits_SetsEveryLayer()
    {
        var scene = new CrossScreenConfig { Layers = { Layer("a.png"), Layer("b.png") } };
        PhysicalUnits.ApplyRoomUnits(scene, physicalRoom: true);
        Assert.All(scene.Layers, l =>
        {
            Assert.Equal(SizeUnit.Centimeters, l.Animation.SizeUnit);
            Assert.Equal(SpeedUnit.CentimetersPerSecond, l.Movement.SpeedUnit);
        });
    }

    // ── layer clock ──────────────────────────────────────────────────────────

    [Fact]
    public void LayerElapsed_AddsPhaseOffset_AfterTheStartGate()
    {
        var layer = Layer("a.png");
        layer.PhaseOffsetMs = 7000;

        Assert.Null(SyncTiming.LayerElapsed(-1, 0, layer));            // before the shared start: nothing, phase or not
        Assert.Equal(7000, SyncTiming.LayerElapsed(0, 0, layer));
        Assert.Equal(12_345 + 7000, SyncTiming.LayerElapsed(12_345, 3, layer));   // Sequential: no node phase
    }

    [Fact]
    public void LayerElapsed_SimultaneousWave_ShiftsPerNode()
    {
        var layer = Layer("a.png");
        layer.Distribution = AnimationDistributionMode.Simultaneous;
        layer.Movement.NodePhaseDelayMs = 500;

        Assert.Equal(1000, SyncTiming.LayerElapsed(1000, 0, layer));
        Assert.Equal(0, SyncTiming.LayerElapsed(1000, 2, layer));
        Assert.Null(SyncTiming.LayerElapsed(1000, 3, layer));
    }

    [Fact]
    public void LayerPosition_IsMovementAtTheLayerClock_IndependentOfOtherLayers()
    {
        var fish = Layer("fish.gif");
        fish.PhaseOffsetMs = 2500;
        var alone = new CrossScreenConfig { Layers = { fish } };
        var withOthers = new CrossScreenConfig { Layers = { Layer("logo.png"), fish, Layer("top.png") } };

        foreach (long t in new long[] { 0, 1234, 60_000 })
        {
            var expected = MovementCalculator.Calculate(fish.Movement, t + 2500, 100, 50, 5000, 1080);
            foreach (var scene in new[] { alone, withOthers })
            {
                var layer = scene.Layers.Single(l => l.Name == "fish");
                long clock = SyncTiming.LayerElapsed(t, 1, layer)!.Value;
                Assert.Equal(expected, MovementCalculator.Calculate(layer.Movement, clock, 100, 50, 5000, 1080));
            }
        }
    }

    // ── editor checks ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_SeveralLayers_PrefixesLayerWarnings()
    {
        var empty = new SceneLayer { Name = "Bubbles" };
        var scene = new CrossScreenConfig { Layers = { Layer("logo.png"), empty } };

        Assert.Contains("Layer 2 (Bubbles): Pick an animation file from the strip", SceneChecks.Validate(scene, null));
    }

    [Fact]
    public void Validate_VideoAboveTheBottom_Warns()
    {
        var scene = new CrossScreenConfig { Layers = { Layer("logo.png"), Layer("clip.mp4") } };
        Assert.Contains(SceneChecks.Validate(scene, null), w => w.StartsWith("Layer 2 (clip): Only the bottom layer can play a video"));

        var bottomVideo = new CrossScreenConfig { Layers = { Layer("clip.mp4") } };
        Assert.DoesNotContain(SceneChecks.Validate(bottomVideo, null), w => w.Contains("Only the bottom layer"));
    }

    [Fact]
    public void Validate_LayerLimits()
    {
        var many = new CrossScreenConfig();
        for (int i = 0; i < SceneChecks.MaxLayers + 1; i++) many.Layers.Add(Layer($"l{i}.png"));
        Assert.Contains(SceneChecks.Validate(many, null), w => w.StartsWith($"More than {SceneChecks.MaxLayers} layers"));

        var patterns = new CrossScreenConfig();
        for (int i = 0; i < SceneChecks.MaxPatternLayers + 1; i++)
        {
            var l = Layer($"p{i}.png");
            l.Animation.Pattern = new PatternConfig();
            patterns.Layers.Add(l);
        }
        Assert.Contains(SceneChecks.Validate(patterns, null), w => w.StartsWith($"More than {SceneChecks.MaxPatternLayers} pattern layers"));
    }

    [Fact]
    public void Validate_DisabledLayer_IsNotChecked()
    {
        var off = new SceneLayer { Name = "Off", Enabled = false };
        Assert.Empty(SceneChecks.Validate(new CrossScreenConfig { Layers = { Layer("logo.png"), off } }, null));
    }
}
