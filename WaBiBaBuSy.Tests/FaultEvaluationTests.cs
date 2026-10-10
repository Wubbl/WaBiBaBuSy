using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class FaultEvaluationTests
{
    private static FaultReport Drop(long? lost, long? back) =>
        new() { Kind = TestFaults.Drop, Ms = 10000, SentAtUtcMs = 0, LostAtUtcMs = lost, BackAtUtcMs = back };

    private static FaultReport Silent(long? lost, long? back) =>
        new() { Kind = TestFaults.Silent, Ms = 45000, SentAtUtcMs = 0, LostAtUtcMs = lost, BackAtUtcMs = back };

    [Theory]
    [InlineData(14000L, Verdict.Pass, 4000L)]
    [InlineData(15000L, Verdict.Pass, 5000L)]
    [InlineData(19000L, Verdict.Warn, 9000L)]
    [InlineData(21000L, Verdict.Fail, 11000L)]
    public void Drop_ReconnectTimeAfterTheFault(long back, Verdict verdict, long reconnectMs)
    {
        var r = Drop(lost: 50, back: back);
        FaultEvaluation.Evaluate(r, maxReconnectMs: 5000, maxSweepMs: 45000);
        Assert.Equal(verdict, r.Verdict);
        Assert.Equal(reconnectMs, r.ReconnectMs);
    }

    [Fact]
    public void Drop_NeverLost_Fails()
    {
        var r = Drop(lost: null, back: null);
        FaultEvaluation.Evaluate(r, 5000, 45000);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("never lost", r.Message);
    }

    [Fact]
    public void Drop_NeverBack_Fails()
    {
        var r = Drop(lost: 50, back: null);
        FaultEvaluation.Evaluate(r, 5000, 45000);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("never came back", r.Message);
    }

    [Fact]
    public void Drop_BackBeforeTheFaultEnded_Fails()
    {
        var r = Drop(lost: 50, back: 3000);
        FaultEvaluation.Evaluate(r, 5000, 45000);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("before the fault ended", r.Message);
    }

    [Theory]
    [InlineData(38000L, Verdict.Pass)]
    [InlineData(52000L, Verdict.Warn)]
    public void Silent_SweepTime(long lost, Verdict verdict)
    {
        var r = Silent(lost: lost, back: 47000 + lost);
        FaultEvaluation.Evaluate(r, 5000, maxSweepMs: 45000);
        Assert.Equal(verdict, r.Verdict);
        Assert.Equal(lost, r.SweepMs);
    }

    [Fact]
    public void Silent_NeverSwept_Fails()
    {
        var r = Silent(lost: null, back: null);
        FaultEvaluation.Evaluate(r, 5000, 45000);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("never dropped", r.Message);
    }

    [Fact]
    public void Silent_SweptButNeverBack_Fails()
    {
        var r = Silent(lost: 38000, back: null);
        FaultEvaluation.Evaluate(r, 5000, 45000);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains("never came back", r.Message);
    }
}
