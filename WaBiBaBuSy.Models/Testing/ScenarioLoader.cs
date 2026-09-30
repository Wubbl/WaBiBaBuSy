using System.Globalization;
using System.Text.Json;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>Parses and validates scenario JSON. Nothing is played when this throws.</summary>
public static class ScenarioLoader
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,   // "type" does not have to be the first property
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>Load and validate a scenario file.</summary>
    public static TestScenario Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new ScenarioException($"Scenario file not found: {full}");
        return Parse(File.ReadAllText(full), Path.GetDirectoryName(full)!, full);
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
        if (string.IsNullOrWhiteSpace(scenario.Name)) errors.Add("name is empty");
        if (scenario.Steps.Count == 0) errors.Add("steps is empty");
        if (scenario.Requires.MinRemoteNodes < 0) errors.Add("requires.minRemoteNodes must be >= 0");

        bool scenePlaying = false;
        for (int i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            string at = $"step {i + 1} ({step.Kind})";
            if (step.TimeoutMs is <= 0) errors.Add($"{at}: timeoutMs must be > 0");

            switch (step)
            {
                case PlaySceneStep play:
                    if (string.IsNullOrWhiteSpace(play.Scene)) errors.Add($"{at}: scene is empty");
                    else if (!File.Exists(ResolvePath(scenario, play.Scene)))
                        errors.Add($"{at}: scene file not found: {ResolvePath(scenario, play.Scene)}");
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
