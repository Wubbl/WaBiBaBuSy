using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class DriftAnalyzerTests
{
    private const long Start = 1_000_000;
    private static readonly ScenarioThresholds T = new();   // warn 25, fail 50

    /// <summary>A node whose clock is <paramref name="clientBehindMs"/> behind the server, showing content
    /// <paramref name="contentLagMs"/> behind wall time, with 16 ms render→present latency.</summary>
    private static NodeProbeSample Node(string name, double clientBehindMs = 0, long wallElapsed = 5000, long contentLagMs = 0, int phaseMs = 0)
    {
        long renderServer = Start + wallElapsed;
        long renderLocal = renderServer - (long)clientBehindMs;
        return new NodeProbeSample
        {
            NodeId = name, NodeName = name,
            RenderedElapsedMs = wallElapsed - contentLagMs - phaseMs,
            PhaseMs = phaseMs,
            RenderLocalUtcMs = renderLocal,
            PresentLocalUtcMs = renderLocal + 16,
            ClockOffsetMs = clientBehindMs,          // offset = server − client
            RttMs = 2,
        };
    }

    [Fact]
    public void ErrorMs_HealthyNode_IsMinusPresentLatency() =>
        Assert.Equal(-16, DriftAnalyzer.ErrorMs(Node("a"), Start));

    [Fact]
    public void ErrorMs_ClockSkewIsCancelledByTheOffset() =>
        Assert.Equal(-16, DriftAnalyzer.ErrorMs(Node("a", clientBehindMs: 2000), Start));

    [Fact]
    public void ErrorMs_WavePhaseIsAddedBack() =>
        Assert.Equal(-16, DriftAnalyzer.ErrorMs(Node("a", phaseMs: 750), Start));

    [Fact]
    public void ErrorMs_FallsBackToRenderTime_WhenNoPresentTime()
    {
        var s = Node("a");
        s.PresentLocalUtcMs = null;
        Assert.Equal(0, DriftAnalyzer.ErrorMs(s, Start));
        Assert.True(DriftAnalyzer.Analyze(new[] { s }, Start, T).Errors[0].UsedRenderTime);
    }

    [Fact]
    public void Analyze_IdenticalNodes_Pass()
    {
        var r = DriftAnalyzer.Analyze(new[] { Node("a"), Node("b", clientBehindMs: 1234) }, Start, T);
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal(0, r.SpreadMs, 3);
        Assert.Equal(-16, r.MeanErrorMs, 3);
        Assert.Equal(1, r.Errors[1].ClockBoundMs);   // RTT/2
    }

    [Theory]
    [InlineData(25, Verdict.Pass)]
    [InlineData(30, Verdict.Warn)]
    [InlineData(50, Verdict.Warn)]
    [InlineData(80, Verdict.Fail)]
    public void Analyze_SpreadThresholds(long lag, Verdict expected)
    {
        var r = DriftAnalyzer.Analyze(new[] { Node("a"), Node("b", contentLagMs: lag) }, Start, T);
        Assert.Equal(lag, r.SpreadMs, 3);
        Assert.Equal(expected, r.Verdict);
    }

    [Fact]
    public void MissingNode_ExcludedFromSpread_AtLeastWarn()
    {
        var gone = NodeProbeSample.MissingFor("c", "pc-03", 0, isLocal: false, reason: "no reply within 2 s");
        var failed = Node("d");
        failed.Error = "readback failed";

        var r = DriftAnalyzer.Analyze(new[] { Node("a"), Node("b"), gone, failed }, Start, T);

        Assert.Equal(Verdict.Warn, r.Verdict);
        Assert.Equal(2, r.Errors.Count);
        Assert.Equal(new[] { "pc-03", "d" }, r.MissingNodes);
        Assert.Contains("missing", r.Message);
    }

    [Fact]
    public void MissingNode_AllMissing_Skipped()
    {
        var r = DriftAnalyzer.Analyze(new[] { NodeProbeSample.MissingFor("c", "pc-03", 0, false, "timeout") }, Start, T);
        Assert.Equal(Verdict.Skipped, r.Verdict);
    }

    [Fact]
    public void NegativeElapsed_StillComparable()
    {
        // Probe 500 ms before the shared start: both nodes show the background only; timing still agrees.
        var r = DriftAnalyzer.Analyze(new[] { Node("a", wallElapsed: -500), Node("b", wallElapsed: -500) }, Start, T);
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal(-16, r.Errors[0].ErrorMs, 3);
    }

    [Theory]
    [InlineData(Verdict.Pass, Verdict.Fail, Verdict.Fail)]
    [InlineData(Verdict.Warn, Verdict.Pass, Verdict.Warn)]
    [InlineData(Verdict.Skipped, Verdict.Pass, Verdict.Pass)]
    [InlineData(Verdict.Skipped, Verdict.Skipped, Verdict.Skipped)]
    public void Verdicts_Worst(Verdict a, Verdict b, Verdict expected) => Assert.Equal(expected, Verdicts.Worst(a, b));
}
