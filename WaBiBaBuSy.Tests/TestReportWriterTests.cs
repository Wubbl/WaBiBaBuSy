using System.Globalization;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class TestReportWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-report-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private TestRunReport Report()
    {
        var sample = new NodeProbeSample { NodeId = "a", NodeName = "pc-01", Width = 400, Height = 300, CapturePath = "nodes/pc-01/02-p-02-001-mon0.png" };
        return new TestRunReport
        {
            Scenario = "sync <basic>", ResultsDirectory = _dir, Verdict = Verdict.Warn,
            StartedUtc = DateTimeOffset.UnixEpoch, FinishedUtc = DateTimeOffset.UnixEpoch.AddSeconds(65),
            Environment = new RunEnvironment { AppVersion = "2.6.3", Nodes = { new TestNodeInfo { NodeId = "a", Name = "pc-01", Width = 400, Height = 300 } } },
            Warnings = { "pc-02 runs 2.6.2" },
            Steps =
            {
                new StepReport
                {
                    Index = 2, Kind = "probeSeries", Label = "long-run", Verdict = Verdict.Warn, Message = "3 probes",
                    Probes =
                    {
                        new ProbeReport { ProbeId = "02-001", Verdict = Verdict.Pass, Samples = { sample },
                            Drift = new DriftResult { SpreadMs = 4, Verdict = Verdict.Pass },
                            Positions = { new PositionCheckResult { NodeId = "a", NodeName = "pc-01", Verdict = Verdict.Pass,
                                Expected = new MarkerExpectation { Visibility = MarkerVisibility.Visible, X = 10, Y = 10, Width = 64, Height = 64 } } } },
                        new ProbeReport { ProbeId = "02-002", Verdict = Verdict.Warn, Drift = new DriftResult { SpreadMs = 30, Verdict = Verdict.Warn } },
                        new ProbeReport { ProbeId = "02-003", Verdict = Verdict.Pass, Drift = new DriftResult { SpreadMs = 6, Verdict = Verdict.Pass } },
                    },
                },
            },
        };
    }

    [Fact]
    public void Write_CreatesJsonAndHtml()
    {
        TestReportWriter.Write(Report());
        Assert.True(File.Exists(Path.Combine(_dir, "report.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "report.html")));
    }

    [Fact]
    public void Html_EscapesText_ShowsVerdicts_ChartAndScreenshots()
    {
        var html = TestReportWriter.RenderHtml(Report());
        Assert.Contains("sync &lt;basic&gt;", html);                  // escaped, never raw markup
        Assert.DoesNotContain("sync <basic>", html);
        Assert.Contains("long-run", html);
        Assert.Contains("class=\"verdict warn\"", html);
        Assert.Contains("<polyline", html);                           // drift chart for the series
        Assert.Contains("href=\"nodes/pc-01/02-p-02-001-mon0.png\"", html);
        Assert.Contains("pc-02 runs 2.6.2", html);
        Assert.Contains("prefers-color-scheme: dark", html);
    }

    [Fact]
    public void Html_ChartUsesTheScenarioThresholds_AndListsNodeFailures()
    {
        var report = Report();
        report.Thresholds = new ScenarioThresholds { DriftWarnMs = 10, DriftSpreadMs = 20.5 };
        report.NodeFailures.Add(new NodeFailure { NodeId = "b", Name = "pc-02", AtUtcMs = 1000, Reason = "probe 02-002: client has no command stream" });

        var html = TestReportWriter.RenderHtml(report);

        Assert.Contains(">10</text>", html);
        Assert.Contains(">20.5</text>", html);
        Assert.DoesNotContain(">25</text>", html);
        Assert.Contains("Node failures", html);
        Assert.Contains("pc-02: probe 02-002: client has no command stream", html);
    }

    [Fact]
    public void Html_ShowsAProbeError()
    {
        var report = Report();
        report.Steps[0].Probes[1].Error = "probe <exploded> & gone";
        var html = TestReportWriter.RenderHtml(report);
        Assert.Contains("probe &lt;exploded&gt; &amp; gone", html);
    }

    [Fact]
    public void Html_NumbersStayInvariant_UnderAGermanCulture()
    {
        var report = Report();
        report.Thresholds = new ScenarioThresholds { DriftWarnMs = 12.5, DriftSpreadMs = 20.5 };
        report.Environment.Nodes[0].ClockOffsetMs = 1234.5;
        report.Environment.Nodes[0].RttMs = 1.5;
        report.Steps[0].Probes[1].Drift!.Errors.Add(new NodeTimingError { NodeName = "pc-01", ErrorMs = 3.5, ClockBoundMs = 0.5 });
        report.Steps[0].Probes[1].Drift!.SpreadMs = 30.25;

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        string html;
        try { html = TestReportWriter.RenderHtml(report); }
        finally { CultureInfo.CurrentCulture = previous; }

        Assert.Contains(">12.5</text>", html);
        Assert.Contains(">20.5</text>", html);
        Assert.Contains("1.5 ms", html);
        Assert.Contains("3.5 ms", html);
        Assert.DoesNotContain("12,5", html);
        Assert.DoesNotContain("20,5", html);
        Assert.DoesNotContain("1,5", html);
        Assert.DoesNotContain("3,5", html);
    }

    [Fact]
    public void Html_EscapesHostileNodeNames_Everywhere()
    {
        const string hostile = "<img src=x onerror=alert(1)>";
        var report = Report();
        report.Environment.Nodes[0].Name = hostile;
        report.NodeFailures.Add(new NodeFailure { NodeId = "b", Name = hostile, AtUtcMs = 1000, Reason = "gone" });
        var probe = report.Steps[0].Probes[0];
        probe.Samples[0].NodeName = hostile;
        probe.Samples.Add(new NodeProbeSample { NodeId = "b", NodeName = hostile, Missing = true, Error = "no reply" });
        probe.Positions[0].NodeName = hostile;
        probe.Parity.Add(new PixelParityResult { NodeId = "b", NodeName = hostile, ReferenceNodeName = hostile, Verdict = Verdict.Fail, Message = "differs" });
        report.Steps[0].Probes[1].Drift!.Errors.Add(new NodeTimingError { NodeName = hostile, ErrorMs = 3, ClockBoundMs = 1 });

        var html = TestReportWriter.RenderHtml(report);

        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
    }

    [Fact]
    public void Html_NoEmptyParagraph_ForAnEmptyDriftMessage()
    {
        var html = TestReportWriter.RenderHtml(Report());   // its probes' drift messages are empty
        Assert.DoesNotContain("<p></p>", html);
    }

    [Fact]
    public void Write_WhenTheHtmlCannotBeRendered_StillLeavesAPageThatPointsToTheJson()
    {
        var report = Report();
        report.Environment = null!;   // RenderHtml cannot cope; report.json can

        Assert.ThrowsAny<Exception>(() => TestReportWriter.Write(report));

        Assert.True(File.Exists(Path.Combine(_dir, "report.json")));
        var html = File.ReadAllText(Path.Combine(_dir, "report.html"));
        Assert.Contains("report.json", html);
        Assert.Contains("sync &lt;basic&gt;", html);
    }
}
