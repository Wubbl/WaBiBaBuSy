using WaBiBaBuSy.Models.Networking;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ClockOffsetEstimatorTests
{
    [Fact]
    public void NoSamples_OffsetIsZero_HasSamplesFalse()
    {
        var estimator = new ClockOffsetEstimator();
        Assert.False(estimator.HasSamples);
        Assert.Equal(0, estimator.OffsetMs);
    }

    [Fact]
    public void SymmetricRoundTrip_ComputesExactOffset()
    {
        // Client sends at t0=1000, server clock reads 1600 at the midpoint,
        // client receives at t3=1200. Midpoint of client clock = 1100.
        // Offset = server - midpoint = 1600 - 1100 = 500ms (server is ahead).
        var estimator = new ClockOffsetEstimator();
        estimator.AddSample(clientSendMs: 1000, serverTimestampMs: 1600, clientReceiveMs: 1200);
        Assert.True(estimator.HasSamples);
        Assert.Equal(500, estimator.OffsetMs);
    }

    [Fact]
    public void NegativeOffset_WhenServerBehind()
    {
        var estimator = new ClockOffsetEstimator();
        estimator.AddSample(clientSendMs: 1000, serverTimestampMs: 900, clientReceiveMs: 1200);
        Assert.Equal(-200, estimator.OffsetMs);
    }

    [Fact]
    public void MinRttSampleWins()
    {
        var estimator = new ClockOffsetEstimator();
        // High-RTT sample (200ms RTT) with skewed offset from asymmetry
        estimator.AddSample(1000, 1700, 1200);   // rtt=200, offset=600
        // Low-RTT sample (20ms RTT) — the trustworthy one
        estimator.AddSample(2000, 2510, 2020);   // rtt=20, offset=500
        // Another high-RTT sample
        estimator.AddSample(3000, 3800, 3300);   // rtt=300, offset=650
        Assert.Equal(500, estimator.OffsetMs);
    }

    [Fact]
    public void WindowEvictsOldestSamples()
    {
        var estimator = new ClockOffsetEstimator();
        // Fill window with a perfect zero-RTT sample that would always win...
        estimator.AddSample(1000, 1500, 1000);   // rtt=0, offset=500
        // ...then push 16 more samples (window size) with rtt=10, offset=100 to evict it.
        for (int i = 0; i < 16; i++)
        {
            long t0 = 10_000 + i * 1000;
            estimator.AddSample(t0, t0 + 105, t0 + 10);  // rtt=10, offset=100
        }
        Assert.Equal(100, estimator.OffsetMs);
    }

    [Fact]
    public void NegativeRttSampleDiscarded()
    {
        var estimator = new ClockOffsetEstimator();
        estimator.AddSample(clientSendMs: 2000, serverTimestampMs: 1500, clientReceiveMs: 1000);
        Assert.False(estimator.HasSamples);
    }

    [Fact]
    public void Reset_ClearsSamples()
    {
        var estimator = new ClockOffsetEstimator();
        estimator.AddSample(1000, 1600, 1200);
        estimator.Reset();
        Assert.False(estimator.HasSamples);
        Assert.Equal(0, estimator.OffsetMs);
    }
}
