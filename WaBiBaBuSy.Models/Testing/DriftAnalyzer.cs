namespace WaBiBaBuSy.Models.Testing;

/// <summary>One node-monitor's timing error for one probe.</summary>
public sealed class NodeTimingError
{
    public string NodeId { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public int MonitorIndex { get; set; }
    /// <summary>Content time − wall time at present, ms (negative = behind).</summary>
    public double ErrorMs { get; set; }
    /// <summary>RTT/2: how wrong the clock-offset estimate itself may be. The probe cannot see that part.</summary>
    public double ClockBoundMs { get; set; }
    /// <summary>No present time was available; render time was used.</summary>
    public bool UsedRenderTime { get; set; }
}

/// <summary>Timing verdict for one probe across all nodes.</summary>
public sealed class DriftResult
{
    public List<NodeTimingError> Errors { get; set; } = new();
    /// <summary>max(error) − min(error): the drift people can see between screens.</summary>
    public double SpreadMs { get; set; }
    /// <summary>Common latency of the whole wall behind wall time (same on all nodes → invisible).</summary>
    public double MeanErrorMs { get; set; }
    /// <summary>
    /// Spread of the render-time errors (present time ignored) across all valid nodes. <see cref="SpreadMs"/>
    /// uses present times only when ≥ 2 nodes have one, so this keeps render-time-fallback nodes visible.
    /// </summary>
    public double RenderSpreadMs { get; set; }
    public Verdict Verdict { get; set; }
    public List<string> MissingNodes { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

/// <summary>Pure drift math over probe samples (design §5.1).</summary>
public static class DriftAnalyzer
{
    /// <summary>
    /// <c>RenderedElapsedMs + PhaseMs − (presentServer − sharedStart)</c>, where
    /// <c>presentServer = (PresentLocalUtcMs ?? RenderLocalUtcMs) + ClockOffsetMs</c>.
    /// </summary>
    public static double ErrorMs(NodeProbeSample sample, long sharedStartServerUtcMs)
    {
        long presentLocal = sample.PresentLocalUtcMs ?? sample.RenderLocalUtcMs;
        double presentServer = presentLocal + sample.ClockOffsetMs;
        return sample.RenderedElapsedMs + sample.PhaseMs - (presentServer - sharedStartServerUtcMs);
    }

    /// <summary>Spread and verdict. Missing / failed samples are excluded and force at least Warn.</summary>
    public static DriftResult Analyze(IReadOnlyList<NodeProbeSample> samples, long sharedStartServerUtcMs, ScenarioThresholds thresholds)
    {
        var missing = samples.Where(s => s.Missing || s.Error != null).Select(DisplayName).ToList();
        var valid = samples.Where(s => !s.Missing && s.Error == null).ToList();
        if (valid.Count == 0)
            return new DriftResult { Verdict = Verdict.Skipped, MissingNodes = missing, Message = "no node answered" };

        var errors = valid.Select(s => new NodeTimingError
        {
            NodeId = s.NodeId,
            NodeName = s.NodeName,
            MonitorIndex = s.MonitorIndex,
            ErrorMs = ErrorMs(s, sharedStartServerUtcMs),
            ClockBoundMs = Math.Max(0, s.RttMs) / 2.0,
            UsedRenderTime = s.PresentLocalUtcMs == null,
        }).ToList();

        // A render-time error lacks the present latency (6–17 ms) a present-time error carries, so mixing
        // both inflates the spread by up to a frame: use present-time samples only when there are ≥ 2.
        var presentErrors = errors.Where(e => !e.UsedRenderTime).ToList();
        var fallbackNames = errors.Where(e => e.UsedRenderTime)
            .Select(e => e.MonitorIndex > 0 ? $"{e.NodeName}#{e.MonitorIndex}" : e.NodeName).ToList();
        bool presentOnly = presentErrors.Count >= 2;
        var basis = presentOnly ? presentErrors : errors;

        double spread = basis.Max(e => e.ErrorMs) - basis.Min(e => e.ErrorMs);
        double mean = basis.Average(e => e.ErrorMs);
        var renderErrors = valid.Select(s => RenderErrorMs(s, sharedStartServerUtcMs)).ToList();
        double renderSpread = renderErrors.Max() - renderErrors.Min();

        var verdict = spread <= thresholds.DriftWarnMs ? Verdict.Pass
            : spread <= thresholds.DriftSpreadMs ? Verdict.Warn
            : Verdict.Fail;
        if (missing.Count > 0) verdict = Verdicts.Worst(verdict, Verdict.Warn);

        var message = $"spread {spread:F1} ms (warn > {thresholds.DriftWarnMs}, fail > {thresholds.DriftSpreadMs}), common latency {mean:F1} ms";
        if (fallbackNames.Count > 0)
        {
            message += presentOnly
                ? $", present-time samples only; render-time fallback: {string.Join(", ", fallbackNames)}"
                : $", fewer than 2 present times: all samples mixed (render-time fallback: {string.Join(", ", fallbackNames)})";
            message += $", render-time spread {renderSpread:F1} ms";
        }
        if (missing.Count > 0) message += $", missing: {string.Join(", ", missing)}";

        return new DriftResult
        {
            Errors = errors, SpreadMs = spread, MeanErrorMs = mean, RenderSpreadMs = renderSpread,
            Verdict = verdict, MissingNodes = missing, Message = message,
        };
    }

    /// <summary><see cref="ErrorMs"/> with the render time, ignoring any present time.</summary>
    private static double RenderErrorMs(NodeProbeSample sample, long sharedStartServerUtcMs) =>
        sample.RenderedElapsedMs + sample.PhaseMs - (sample.RenderLocalUtcMs + sample.ClockOffsetMs - sharedStartServerUtcMs);

    private static string DisplayName(NodeProbeSample s) => s.MonitorIndex > 0 ? $"{s.NodeName}#{s.MonitorIndex}" : s.NodeName;
}
