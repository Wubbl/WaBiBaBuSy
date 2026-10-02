using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ScenarioLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-scenario-{Guid.NewGuid():N}");

    public ScenarioLoaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "scenes"));
        File.WriteAllBytes(Path.Combine(_dir, "scenes", "marker.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(_dir, "scenes", "linear.json"),
            """{ "Animation": { "AnimationPath": "marker.png", "TargetHeight": 64 }, "Movement": { "Type": "Linear", "SpeedPixelsPerSecond": 400 }, "DistributionMode": "Sequential" }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string Valid = """
    {
      "name": "sync-basic",
      "requires": { "minRemoteNodes": 1 },
      "thresholds": { "driftSpreadMs": 40 },
      "steps": [
        { "type": "testMode", "timecode": true, "simulatedClockSkewMs": { "pc-02": 2000 } },
        { "type": "playScene", "scene": "scenes/linear.json", "targets": "all", "marker": true },
        { "label": "clean-start", "type": "probe", "at": "start+150ms", "capture": true },
        { "type": "probeSeries", "label": "long-run", "everyMs": 1000, "forMs": 5000 },
        { "type": "exactFrame", "label": "px", "elapsedMs": 12345 },
        { "type": "wait", "ms": 10 },
        { "type": "stop" }
      ]
    }
    """;

    [Fact]
    public void Parse_FullExample_ReadsEveryStepType()
    {
        var s = ScenarioLoader.Parse(Valid, _dir);

        Assert.Equal("sync-basic", s.Name);
        Assert.Equal(1, s.Requires.MinRemoteNodes);
        Assert.Equal(40, s.Thresholds.DriftSpreadMs);
        Assert.Equal(25, s.Thresholds.DriftWarnMs);   // default kept
        Assert.Collection(s.Steps,
            st => Assert.Equal(2000, Assert.IsType<TestModeStep>(st).SimulatedClockSkewMs["pc-02"]),
            st => Assert.True(Assert.IsType<PlaySceneStep>(st).Marker),
            st => Assert.Equal("start+150ms", Assert.IsType<ProbeStep>(st).At),   // "type" after "label" still works
            st => Assert.Equal(5000, Assert.IsType<ProbeSeriesStep>(st).ForMs),
            st => Assert.Equal(12345, Assert.IsType<ExactFrameStep>(st).ElapsedMs),
            st => Assert.Equal(10, Assert.IsType<WaitStep>(st).Ms),
            st => Assert.IsType<StopStep>(st));
    }

    [Theory]
    [InlineData("\"type\": \"teleport\"")]
    [InlineData("\"type\": \"playscene\", \"scene\": \"scenes/linear.json\"")]   // discriminators are case-sensitive
    [InlineData("\"scene\": \"scenes/linear.json\"")]                           // no type at all
    public void Parse_InvalidStepType_Throws(string stepBody)
    {
        var json = $$"""{ "name": "x", "steps": [ { {{stepBody}} } ] }""";
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir));
        Assert.Contains("invalid scenario JSON", ex.Message);
    }

    [Fact]
    public void Parse_Invalid_MissingSceneFile_NamesThePath()
    {
        var json = """{ "name": "x", "steps": [ { "type": "playScene", "scene": "scenes/nope.json" } ] }""";
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir));
        Assert.Contains("scene file not found", ex.Message);
        Assert.Contains("nope.json", ex.Message);
    }

    [Theory]
    [InlineData("""{ "type": "probe", "label": "p", "at": "later" }""", "is not start+Nms or now+Nms")]
    [InlineData("""{ "type": "probe", "at": "now+10ms" }""", "label is required")]
    [InlineData("""{ "type": "probeSeries", "label": "s", "everyMs": 1000, "forMs": 500 }""", "forMs must be >= everyMs")]
    [InlineData("""{ "type": "probeSeries", "label": "s", "everyMs": 0, "forMs": 500 }""", "everyMs must be > 0")]
    [InlineData("""{ "type": "exactFrame", "label": "e", "elapsedMs": -1 }""", "elapsedMs must be >= 0")]
    [InlineData("""{ "type": "wait", "ms": 10, "timeoutMs": 0 }""", "timeoutMs must be > 0")]
    [InlineData("""{ "type": "wait", "ms": -1 }""", "ms must be >= 0")]
    [InlineData("""{ "type": "playScene", "scene": "" }""", "scene is empty")]
    [InlineData("""{ "type": "playScene", "scene": "a\u0000b.json" }""", "invalid scene path")]
    public void Parse_Invalid_StepParameters(string step, string expected)
    {
        var json = $$"""{ "name": "x", "steps": [ { "type": "playScene", "scene": "scenes/linear.json" }, {{step}} ] }""";
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Parse_Invalid_ProbeBeforeAnyScene()
    {
        var json = """{ "name": "x", "steps": [ { "type": "probe", "label": "p", "at": "now+10ms" } ] }""";
        Assert.Contains("needs a playScene step before it",
            Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir)).Message);
    }

    [Fact]
    public void Parse_Invalid_EmptyNameAndSteps()
    {
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse("""{ "name": "", "steps": [] }""", _dir));
        Assert.Contains("name is empty", ex.Message);
        Assert.Contains("steps is empty", ex.Message);
    }

    [Theory]
    [InlineData("""{ "name": "x", "steps": null }""", "steps is missing")]
    [InlineData("""{ "name": "x", "steps": [ null ] }""", "step 1 is null")]
    [InlineData("""{ "name": "x", "requires": null, "thresholds": null, "steps": [ { "type": "wait", "ms": 1 } ] }""", "requires is null")]
    [InlineData("""{ "name": "x", "thresholds": null, "steps": [ { "type": "wait", "ms": 1 } ] }""", "thresholds is null")]
    [InlineData("""{ "name": "x", "steps": [ { "type": "testMode", "simulatedClockSkewMs": null } ] }""", "simulatedClockSkewMs is null")]
    [InlineData("""{ "name": "x", "steps": [ { "type": "playScene", "scene": "scenes/linear.json", "targets": null } ] }""", "targets is null")]
    public void Parse_JsonNulls_AreScenarioErrors(string json, string expected)
    {
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir, "nulls.json"));
        Assert.Contains("nulls.json", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("""{ "Animation": null }""", "Animation is null")]
    [InlineData("""{ "Animation": { "AnimationPath": "marker.png" }, "Background": null }""", "Background is null")]
    [InlineData("""{ "Animation": { "AnimationPath": "marker.png" }, "Movement": null }""", "Movement is null")]
    [InlineData("""{ "Animation": { "AnimationPath": "marker.png", "AdditionalAnimationPaths": null } }""", "AdditionalAnimationPaths is null")]
    public void SceneFile_Load_JsonNulls_AreScenarioErrors(string json, string expected)
    {
        var path = Path.Combine(_dir, "scenes", "nulls.json");
        File.WriteAllText(path, json);
        var ex = Assert.Throws<ScenarioException>(() => SceneFile.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void SceneFile_Load_MissingFile_IsAScenarioError()
    {
        var path = Path.Combine(_dir, "scenes", "gone.json");
        var ex = Assert.Throws<ScenarioException>(() => SceneFile.Load(path));
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Load_MissingScenarioFile_IsAScenarioError()
    {
        var path = Path.Combine(_dir, "nope.json");
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Load(path));
        Assert.Contains("Scenario file not found", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Load_FromFile_ResolvesScenesAgainstTheScenarioFolder()
    {
        var path = Path.Combine(_dir, "unit.json");
        File.WriteAllText(path, Valid);
        var s = ScenarioLoader.Load(path);
        Assert.Equal(_dir, s.BaseDirectory);
        Assert.Equal(Path.Combine(_dir, "scenes", "linear.json"), ScenarioLoader.ResolvePath(s, "scenes/linear.json"));
    }

    [Fact]
    public void ResolvePath_AbsoluteStaysAbsolute_RelativeIsNormalized()
    {
        var s = ScenarioLoader.Parse(Valid, _dir);
        var elsewhere = Path.Combine(Path.GetTempPath(), "elsewhere", "scene.json");
        Assert.Equal(elsewhere, ScenarioLoader.ResolvePath(s, elsewhere));
        Assert.Equal(Path.Combine(_dir, "scene.json"), ScenarioLoader.ResolvePath(s, "scenes/../scene.json"));
    }

    [Fact]
    public void SceneFile_Load_JsonNullDocument_IsEmptyScene()
    {
        var path = Path.Combine(_dir, "scenes", "null.json");
        File.WriteAllText(path, "null");
        Assert.Contains("empty scene", Assert.Throws<ScenarioException>(() => SceneFile.Load(path)).Message);
    }

    [Fact]
    public void SceneFile_Load_MakesAdditionalAndBackgroundPathsAbsolute()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "scenes", "sub"));
        File.WriteAllBytes(Path.Combine(_dir, "scenes", "sub", "b.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_dir, "bg.png"), new byte[] { 1 });
        var path = Path.Combine(_dir, "scenes", "multi.json");
        File.WriteAllText(path, """
            { "Animation": { "AnimationPath": "marker.png", "AdditionalAnimationPaths": [ "sub/b.png" ] },
              "Background": { "Mode": "StretchedImage", "ImagePath": "../bg.png" } }
            """);

        var scene = SceneFile.Load(path);

        Assert.Equal(new[] { Path.Combine(_dir, "scenes", "sub", "b.png") }, scene.Animation.AdditionalAnimationPaths);
        Assert.Equal(Path.Combine(_dir, "bg.png"), scene.Background.ImagePath);
    }

    // ── Remote / device paths: rejected before any File.Exists (no SMB authentication) ─────────

    public static TheoryData<string> RemotePaths => new()
    {
        @"\\wbbs-test.invalid\share\scene.json",
        "//wbbs-test.invalid/share/scene.json",
        @"\\?\UNC\wbbs-test.invalid\share\scene.json",
        @"\??\UNC\wbbs-test.invalid\share\scene.json",
        @"\\.\pipe\wbbs-test",
    };

    [Theory]
    [MemberData(nameof(RemotePaths))]
    public void IsRemoteOrDevicePath_DetectsUncAndDevicePaths(string path) =>
        Assert.True(ScenarioLoader.IsRemoteOrDevicePath(path));

    [Theory]
    [InlineData(@"C:\scenes\a.json")]
    [InlineData("scenes/a.json")]
    [InlineData(@"\scenes\a.json")]
    [InlineData("")]
    public void IsRemoteOrDevicePath_LocalPathsAreFine(string path) =>
        Assert.False(ScenarioLoader.IsRemoteOrDevicePath(path));

    [Theory]
    [MemberData(nameof(RemotePaths))]
    public void Parse_RemoteScenePath_IsRejected(string scene)
    {
        var json = $$"""{ "name": "x", "steps": [ { "type": "playScene", "scene": {{System.Text.Json.JsonSerializer.Serialize(scene)}} } ] }""";
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir));
        Assert.Contains("must be a local path", ex.Message);
        Assert.DoesNotContain("not found", ex.Message);
    }

    [Fact]
    public void Parse_RelativeSceneUnderUncScenarioFolder_IsRejected()
    {
        var json = """{ "name": "x", "steps": [ { "type": "playScene", "scene": "scenes/linear.json" } ] }""";
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, @"\\wbbs-test.invalid\share"));
        Assert.Contains("must be a local path", ex.Message);
    }

    [Theory]
    [MemberData(nameof(RemotePaths))]
    public void Load_RemoteScenarioPath_IsRejected(string path)
    {
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Load(path));
        Assert.Contains("must be a local path", ex.Message);
    }

    [Theory]
    [MemberData(nameof(RemotePaths))]
    public void SceneFile_Load_RemoteScenePath_IsRejected(string path)
    {
        var ex = Assert.Throws<ScenarioException>(() => SceneFile.Load(path));
        Assert.Contains("must be a local path", ex.Message);
    }

    [Theory]
    [InlineData("""{ "Animation": { "AnimationPath": "\\\\wbbs-test.invalid\\share\\m.png" } }""")]
    [InlineData("""{ "Animation": { "AnimationPath": "marker.png", "AdditionalAnimationPaths": [ "//wbbs-test.invalid/share/b.png" ] } }""")]
    [InlineData("""{ "Animation": { "AnimationPath": "marker.png" }, "Background": { "Mode": "TiledImage", "ImagePath": "\\\\?\\UNC\\wbbs-test.invalid\\s\\bg.png" } }""")]
    public void SceneFile_Load_RemoteAssetPath_IsRejected(string json)
    {
        var path = Path.Combine(_dir, "scenes", "remote-asset.json");
        File.WriteAllText(path, json);
        var ex = Assert.Throws<ScenarioException>(() => SceneFile.Load(path));
        Assert.Contains("must be a local path", ex.Message);
        Assert.DoesNotContain("asset not found", ex.Message);
    }

    [Fact]
    public void SceneFile_Load_InvalidPath_IsAScenarioError() =>
        Assert.Throws<ScenarioException>(() => SceneFile.Load("a\0b.json"));

    [Theory]
    [InlineData("start+150ms", ProbeAnchor.Start, 150)]
    [InlineData("now+2000ms", ProbeAnchor.Now, 2000)]
    [InlineData("START+5", ProbeAnchor.Start, 5)]
    [InlineData("now", ProbeAnchor.Now, 0)]
    public void ProbeAt_TryParse_Accepts(string text, ProbeAnchor anchor, long offset)
    {
        Assert.True(ProbeAt.TryParse(text, out var a, out var o));
        Assert.Equal(anchor, a);
        Assert.Equal(offset, o);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("start-100ms")]
    [InlineData("soon+1ms")]
    [InlineData("now+abc")]
    public void ProbeAt_TryParse_Rejects(string? text) => Assert.False(ProbeAt.TryParse(text, out _, out _));

    [Fact]
    public void ProbeAt_Resolve_NeverEarlierThanMinLead()
    {
        // start+150 lies in the past (start was 10 s ago) → pushed to now + lead
        Assert.Equal(100_500, ProbeAt.Resolve(ProbeAnchor.Start, 150, sharedStartUtcMs: 90_000, nowUtcMs: 100_000, minLeadMs: 500));
        Assert.Equal(103_000, ProbeAt.Resolve(ProbeAnchor.Now, 3000, sharedStartUtcMs: 0, nowUtcMs: 100_000, minLeadMs: 500));
    }

    [Fact]
    public void SceneFile_Load_MakesAssetPathsAbsolute_AndReadsEnumNames()
    {
        var scene = SceneFile.Load(Path.Combine(_dir, "scenes", "linear.json"));
        Assert.Equal(Path.Combine(_dir, "scenes", "marker.png"), scene.Animation.AnimationPath);
        Assert.Equal(MovementType.Linear, scene.Movement.Type);
        Assert.Equal(AnimationDistributionMode.Sequential, scene.DistributionMode);
    }

    [Fact]
    public void SceneFile_Load_MissingAsset_Throws()
    {
        var path = Path.Combine(_dir, "scenes", "broken.json");
        File.WriteAllText(path, """{ "Animation": { "AnimationPath": "gone.gif" } }""");
        Assert.Contains("asset not found", Assert.Throws<ScenarioException>(() => SceneFile.Load(path)).Message);
    }

    [Theory]
    [InlineData("smoke-local.json")]
    [InlineData("sync-basic.json")]
    [InlineData("parity-matrix.json")]
    [InlineData("clock-skew.json")]
    public void BundledScenario_IsValid(string file)
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "TestScenarios"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var scenario = ScenarioLoader.Load(Path.Combine(root!, "TestScenarios", file));
        foreach (var play in scenario.Steps.OfType<PlaySceneStep>())
            SceneFile.Load(ScenarioLoader.ResolvePath(scenario, play.Scene));   // every scene + asset resolves
    }
}
