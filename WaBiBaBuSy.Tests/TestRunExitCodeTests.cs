using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Review 2026-10-13 A2: Warn / Skipped verdicts that hide an unaccounted node must not exit 0.</summary>
public class TestRunExitCodeTests
{
    private static TestRunReport Run(Verdict verdict, params NodeProbeSample[] samples) => new()
    {
        Verdict = verdict,
        Steps = { new StepReport { Verdict = verdict, Probes = { new ProbeReport { Verdict = verdict, Samples = samples.ToList() } } } },
    };

    private static NodeProbeSample Ok(string name) => new() { NodeId = name, NodeName = name };

    [Fact]
    public void Pass_ExitsZero() => Assert.Equal(0, Run(Verdict.Pass, Ok("a"), Ok("b")).ExitCode().Code);

    [Fact]
    public void Warn_FromThresholdsOnly_ExitsZero() => Assert.Equal(0, Run(Verdict.Warn, Ok("a"), Ok("b")).ExitCode().Code);

    [Fact]
    public void Fail_ExitsOne() => Assert.Equal(1, Run(Verdict.Fail, Ok("a")).ExitCode().Code);

    [Fact]
    public void Aborted_ExitsTwo() => Assert.Equal(2, new TestRunReport { Aborted = true, Verdict = Verdict.Pass }.ExitCode().Code);

    [Fact]
    public void UnansweredProbe_ExitsOne_AndNamesTheNode()
    {
        var (code, reason) = Run(Verdict.Warn, Ok("a"), NodeProbeSample.MissingFor("b", "pc-02", 0, false, "no players")).ExitCode();
        Assert.Equal(1, code);
        Assert.Contains("pc-02", reason);
    }

    [Fact]
    public void LostNode_ExitsOne()
    {
        var report = Run(Verdict.Warn, Ok("a"));
        report.NodeFailures.Add(new NodeFailure { NodeId = "b", Name = "pc-02", Reason = "disconnected" });
        Assert.Equal(1, report.ExitCode().Code);
    }

    [Fact]
    public void NothingMeasured_ExitsOne() => Assert.Equal(1, new TestRunReport { Verdict = Verdict.Skipped }.ExitCode().Code);
}
