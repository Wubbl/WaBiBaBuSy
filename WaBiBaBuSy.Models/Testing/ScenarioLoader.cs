using System.Globalization;
using System.Text.Json;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>Parses and validates scenario JSON. Nothing is played when this throws.</summary>
public static class ScenarioLoader
{
    /// <summary>camelCase, case-insensitive, comments and trailing commas allowed; <c>type</c> may come anywhere in a step.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,   // "type" does not have to be the first property
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>Load and validate a scenario file. UNC / device paths are rejected before the file is touched.</summary>
    public static TestScenario Load(string path)
    {
        if (IsRemoteOrDevicePath(path)) throw new ScenarioException($"{path}: scenario {LocalOnly}");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ScenarioException($"{path}: invalid scenario path — {ex.Message}");
        }
        if (IsRemoteOrDevicePath(full)) throw new ScenarioException($"{full}: scenario {LocalOnly}");
        if (!File.Exists(full)) throw new ScenarioException($"Scenario file not found: {full}");
        string json;
        try { json = File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ScenarioException($"{full}: cannot read the scenario — {ex.Message}");
        }
        return Parse(json, Path.GetDirectoryName(full)!, full);
    }

    /// <summary>Parse and validate scenario JSON; relative paths resolve against <paramref name="baseDirectory"/>.</summary>
    public static TestScenario Parse(string json, string baseDirectory, string sourceName = "(scenario)")
    {
        TestScenario? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<TestScenario>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ScenarioException($"{sourceName}: invalid scenario JSON — {ex.Message}");
        }
        if (scenario == null) throw new ScenarioException($"{sourceName}: empty scenario");

        scenario.BaseDirectory = baseDirectory;
        var errors = Validate(scenario);
        if (errors.Count > 0)
            throw new ScenarioException($"{sourceName}:{Environment.NewLine}" + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));
        return scenario;
    }

    /// <summary>Every problem in the scenario, empty when it can run.</summary>
    public static IReadOnlyList<string> Validate(TestScenario scenario)
    {
        var errors = new List<string>();
        // JSON "null" overrides the defaults: report it instead of crashing later.
        if (string.IsNullOrWhiteSpace(scenario.Name)) errors.Add("name is empty");
        if (scenario.Steps == null) errors.Add("steps is missing (null)");
        else if (scenario.Steps.Count == 0) errors.Add("steps is empty");
        if (scenario.Requires == null) errors.Add("requires is null");
        else if (scenario.Requires.MinRemoteNodes < 0) errors.Add("requires.minRemoteNodes must be >= 0");
        if (scenario.Thresholds == null) errors.Add("thresholds is null");

        bool scenePlaying = false;
        var steps = scenario.Steps ?? new List<TestStep>();
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step == null)
            {
                errors.Add($"step {i + 1} is null");
                continue;
            }
            string at = $"step {i + 1} ({step.Kind})";
            if (step.TimeoutMs is <= 0) errors.Add($"{at}: timeoutMs must be > 0");

            switch (step)
            {
                case TestModeStep mode:
                    if (mode.SimulatedClockSkewMs == null) errors.Add($"{at}: simulatedClockSkewMs is null");
                    break;
                case PlaySceneStep play:
                    if (play.Targets == null) errors.Add($"{at}: targets is null");
                    if (string.IsNullOrWhiteSpace(play.Scene)) errors.Add($"{at}: scene is empty");
                    else ValidateScenePath(scenario, play.Scene, at, errors);
                    scenePlaying = true;
                    break;
                case ProbeStep probe:
                    RequireLabel(probe, at, errors);
                    if (!ProbeAt.TryParse(probe.At, out _, out _)) errors.Add($"{at}: at \"{probe.At}\" is not start+Nms or now+Nms");
                    RequireScene(scenePlaying, at, errors);
                    break;
                case ProbeSeriesStep series:
                    RequireLabel(series, at, errors);
                    if (series.EveryMs <= 0) errors.Add($"{at}: everyMs must be > 0");
                    else if (series.ForMs < series.EveryMs) errors.Add($"{at}: forMs must be >= everyMs");
                    RequireScene(scenePlaying, at, errors);
                    break;
                case ExactFrameStep exact:
                    RequireLabel(exact, at, errors);
                    if (exact.ElapsedMs < 0) errors.Add($"{at}: elapsedMs must be >= 0");
                    RequireScene(scenePlaying, at, errors);
                    break;
                case WaitStep wait:
                    if (wait.Ms < 0) errors.Add($"{at}: ms must be >= 0");
                    break;
                case StopStep:
                    scenePlaying = false;
                    break;
            }
        }
        return errors;
    }

    /// <summary>Absolute path of a scenario-relative (or already absolute) path.</summary>
    public static string ResolvePath(TestScenario scenario, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(scenario.BaseDirectory, path));

    /// <summary>
    /// True for a path that would make this machine open (and authenticate to) a remote SMB share, or a
    /// device: <c>\\server\share</c>, <c>//server/share</c>, <c>\\?\UNC\…</c>, <c>\\?\…</c>, <c>\\.\…</c> and
    /// <c>\??\…</c>. Pure string check, no file system access; mapped network drives are not detected.
    /// </summary>
    public static bool IsRemoteOrDevicePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var p = path.Replace('/', '\\');
        return p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal);
    }

    /// <summary>Error text for a path rejected by <see cref="IsRemoteOrDevicePath"/>.</summary>
    internal const string LocalOnly = @"must be a local path (UNC and device paths like \\server\share, \\?\ or \??\ are rejected)";

    // Checked before File.Exists: probing a UNC path would authenticate to the share.
    private static void ValidateScenePath(TestScenario scenario, string scene, string at, List<string> errors)
    {
        if (IsRemoteOrDevicePath(scene))
        {
            errors.Add($"{at}: scene {LocalOnly}: {scene}");
            return;
        }
        string resolved;
        try { resolved = ResolvePath(scenario, scene); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"{at}: invalid scene path \"{scene}\" — {ex.Message}");
            return;
        }
        if (IsRemoteOrDevicePath(resolved)) errors.Add($"{at}: scene {LocalOnly}: {resolved}");
        else if (!File.Exists(resolved)) errors.Add($"{at}: scene file not found: {resolved}");
    }

    private static void RequireLabel(TestStep step, string at, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(step.Label)) errors.Add($"{at}: label is required");
    }

    private static void RequireScene(bool scenePlaying, string at, List<string> errors)
    {
        if (!scenePlaying) errors.Add($"{at}: needs a playScene step before it");
    }
}

/// <summary>A scenario or scene file that cannot run; the message names the file and every problem.</summary>
public sealed class ScenarioException : Exception
{
    /// <summary>Create the exception; <paramref name="message"/> names the file and every problem.</summary>
    public ScenarioException(string message) : base(message) { }
}

/// <summary>What a probe's <c>at</c> is relative to.</summary>
public enum ProbeAnchor { Start, Now }

/// <summary><c>start+150ms</c> / <c>now+2000ms</c> parsing and resolution to a server instant.</summary>
public static class ProbeAt
{
    /// <summary>Parse <c>start|now</c> optionally followed by <c>+N</c> or <c>+Nms</c> (case-insensitive).</summary>
    public static bool TryParse(string? text, out ProbeAnchor anchor, out long offsetMs)
    {
        anchor = ProbeAnchor.Now;
        offsetMs = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.Trim().ToLowerInvariant();
        string rest;
        if (t.StartsWith("start", StringComparison.Ordinal)) { anchor = ProbeAnchor.Start; rest = t[5..]; }
        else if (t.StartsWith("now", StringComparison.Ordinal)) { anchor = ProbeAnchor.Now; rest = t[3..]; }
        else return false;

        if (rest.Length == 0) return true;
        if (rest[0] != '+') return false;
        rest = rest[1..];
        if (rest.EndsWith("ms", StringComparison.Ordinal)) rest = rest[..^2];
        return long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out offsetMs);
    }

    /// <summary>Server UTC ms of the probe, never earlier than <paramref name="nowUtcMs"/> + <paramref name="minLeadMs"/>.</summary>
    public static long Resolve(ProbeAnchor anchor, long offsetMs, long sharedStartUtcMs, long nowUtcMs, long minLeadMs)
    {
        long target = (anchor == ProbeAnchor.Start ? sharedStartUtcMs : nowUtcMs) + offsetMs;
        return Math.Max(target, nowUtcMs + minLeadMs);
    }
}
