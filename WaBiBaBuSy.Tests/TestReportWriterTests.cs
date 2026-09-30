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
}
