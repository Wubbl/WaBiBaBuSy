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
    public void Message_LocalMonitorName_IsNotSuffixedTwice()
    {
        // Local nodes are named "server #1" already; remote multi-monitor nodes get "#n" appended.
        var local = Node("server #1"); local.MonitorIndex = 1; local.PresentLocalUtcMs = null;
        var remote = Node("pc-02"); remote.MonitorIndex = 1; remote.PresentLocalUtcMs = null;
        var missing = Node("server #2"); missing.MonitorIndex = 2; missing.Missing = true;

        var result = DriftAnalyzer.Analyze(new[] { local, remote, missing, Node("a") }, Start, T);

        Assert.Contains("server #1", result.Message);
        Assert.DoesNotContain("server #1#1", result.Message);
        Assert.Contains("pc-02#1", result.Message);
        Assert.Contains("server #2", result.Message);
        Assert.DoesNotContain("server #2#2", result.Message);
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
    public void MixedProbe_VerdictFromPresentSamplesOnly()
    {
        // Two nodes with present times (error -16), one render-time fallback (error 0).
        // Mixed, the spread would be 16 ms; the present-only spread is 0.
        var fallback = Node("c");
        fallback.PresentLocalUtcMs = null;
        var tight = new ScenarioThresholds { DriftWarnMs = 10, DriftSpreadMs = 15 };

        var r = DriftAnalyzer.Analyze(new[] { Node("a"), Node("b"), fallback }, Start, tight);

        Assert.Equal(0, r.SpreadMs, 3);
        Assert.Equal(-16, r.MeanErrorMs, 3);
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal(3, r.Errors.Count);
        Assert.Equal(0, r.RenderSpreadMs, 3);   // render-time errors: all 0
        Assert.Contains("render-time fallback: c", r.Message);
    }

    [Fact]
    public void MixedProbe_RenderSpreadCoversAllValidNodes()
    {
        var fallback = Node("c", contentLagMs: 40);
        fallback.PresentLocalUtcMs = null;

        var r = DriftAnalyzer.Analyze(new[] { Node("a"), Node("b"), fallback }, Start, T);

        Assert.Equal(0, r.SpreadMs, 3);          // present-time nodes agree
        Assert.Equal(40, r.RenderSpreadMs, 3);   // the lagging fallback node is still visible here
    }

    [Fact]
    public void AllFallback_UsesAllSamples_AndSaysSo()
    {
        var a = Node("a");
        var b = Node("b", contentLagMs: 30);
        var c = Node("c");
        a.PresentLocalUtcMs = null;
        b.PresentLocalUtcMs = null;

        var r = DriftAnalyzer.Analyze(new[] { a, b, c }, Start, T);

        // Only one present sample → all samples, mixed errors: a 0, b -30, c -16.
        Assert.Equal(30, r.SpreadMs, 3);
        Assert.Equal(Verdict.Warn, r.Verdict);
        Assert.Equal(-46 / 3.0, r.MeanErrorMs, 3);
        Assert.Equal(30, r.RenderSpreadMs, 3);
        Assert.Contains("fewer than 2 present times", r.Message);
    }

    [Fact]
    public void DwmEstimatedSamples_CountAsPresentTimes_AndAreNamed()
    {
        var a = Node("a");
        var b = Node("b");
        b.MonitorIndex = 1;
        b.PresentEstimated = true;
        b.PresentSource = PresentSources.Dwm;
        var c = Node("c");
        c.PresentEstimated = true;
        c.PresentSource = PresentSources.Nominal;

        var r = DriftAnalyzer.Analyze(new[] { a, b, c }, Start, T);

        Assert.Equal(0, r.SpreadMs, 3);
        Assert.All(r.Errors, e => Assert.False(e.UsedRenderTime));
        Assert.Equal(PresentSources.Dwm, r.Errors[1].PresentSource);
        Assert.Contains("estimated present time: b#1 (dwm), c (nominal)", r.Message);
    }

    [Fact]
    public void DxgiSamples_NoEstimateMention()
    {
        var a = Node("a");
        a.PresentSource = PresentSources.Dxgi;
        var b = Node("b");
        b.PresentSource = PresentSources.DxgiStepBack;
        b.PresentEstimated = true;

        var r = DriftAnalyzer.Analyze(new[] { a, b }, Start, T);

        Assert.DoesNotContain("estimated present", r.Message);
    }

    [Fact]
    public void FromReply_CarriesPresentSourceAndNote()
    {
        var reply = new PlayerProbeReply
        {
            PresentLocalUtcMs = 5, PresentEstimated = true,
            PresentSource = PresentSources.Dwm, PresentNote = "DXGI frame statistics all zero",
        };
        var s = NodeProbeSample.FromReply(reply, "n", "n", isLocal: true, clockOffsetMs: 0, rttMs: 0);
        Assert.Equal(PresentSources.Dwm, s.PresentSource);
        Assert.Equal("DXGI frame statistics all zero", s.PresentNote);
    }

    [Fact]
    public void OldReplyJson_WithoutPresentSource_StillParses()
    {
        const string old = "{\"ProbeId\":\"p\",\"RenderLocalUtcMs\":10,\"PresentLocalUtcMs\":26,\"PresentEstimated\":false}";
        var viaStj = System.Text.Json.JsonSerializer.Deserialize<PlayerProbeReply>(old)!;
        Assert.Null(viaStj.PresentSource);
        Assert.Null(viaStj.PresentNote);
        Assert.Equal(26, viaStj.PresentLocalUtcMs);
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
    [InlineData(Verdict.Warn, Verdict.Fail, Verdict.Fail)]
    [InlineData(Verdict.Fail, Verdict.Warn, Verdict.Fail)]
    [InlineData(Verdict.Skipped, Verdict.Warn, Verdict.Warn)]
    [InlineData(Verdict.Skipped, Verdict.Pass, Verdict.Pass)]
    [InlineData(Verdict.Skipped, Verdict.Skipped, Verdict.Skipped)]
    public void Verdicts_Worst(Verdict a, Verdict b, Verdict expected) => Assert.Equal(expected, Verdicts.Worst(a, b));
}
