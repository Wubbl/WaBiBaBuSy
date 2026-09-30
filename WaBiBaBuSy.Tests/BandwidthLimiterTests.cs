using WaBiBaBuSy.Models.Networking;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class BandwidthLimiterTests
{
    private const int MB = 1024 * 1024;

    private sealed class FakeClock
    {
        public long NowMs = 1_000_000;
        public long Read() => NowMs;
    }

    [Fact]
    public void Unlimited_NeverDelays()
    {
        var limiter = new BandwidthLimiter(bytesPerSecond: 0);
        for (int i = 0; i < 100; i++)
            Assert.Equal(0, limiter.Reserve(MB));
    }

    [Fact]
    public void FirstChunk_GoesImmediately_FollowingChunksArePaced()
    {
        var clock = new FakeClock();
        var limiter = new BandwidthLimiter(bytesPerSecond: 20L * MB, clock.Read);

        Assert.Equal(0, limiter.Reserve(MB));   // occupies 0..50 ms
        Assert.Equal(50, limiter.Reserve(MB));  // starts at 50 ms
        Assert.Equal(100, limiter.Reserve(MB)); // starts at 100 ms
    }

    [Fact]
    public void Budget_IsSharedAcrossConcurrentStreams()
    {
        // 6 clients downloading at once share one 10 MB/s budget: 6 MB takes 0.6 s in total.
        var clock = new FakeClock();
        var limiter = new BandwidthLimiter(bytesPerSecond: 10L * MB, clock.Read);
        long last = 0;
        for (int client = 0; client < 6; client++)
            last = limiter.Reserve(MB);
        Assert.Equal(500, last); // the 6th chunk starts after 5 × 100 ms
    }

    [Fact]
    public void IdleTime_IsNotBankedIntoABurst()
    {
        var clock = new FakeClock();
        var limiter = new BandwidthLimiter(bytesPerSecond: 20L * MB, clock.Read);
        limiter.Reserve(MB);
        clock.NowMs += 10_000; // 10 s idle
        Assert.Equal(0, limiter.Reserve(MB));
        Assert.Equal(50, limiter.Reserve(MB)); // still paced, no 10 s worth of free credit
    }

    [Fact]
    public void FromMegabytesPerSecond_ZeroOrNegative_IsUnlimited()
    {
        Assert.Equal(0, BandwidthLimiter.FromMegabytesPerSecond(0).BytesPerSecond);
        Assert.Equal(0, BandwidthLimiter.FromMegabytesPerSecond(-5).BytesPerSecond);
        Assert.Equal(20L * MB, BandwidthLimiter.FromMegabytesPerSecond(20).BytesPerSecond);
    }

    [Fact]
    public async Task WaitAsync_Unlimited_CompletesSynchronously()
    {
        var limiter = new BandwidthLimiter(bytesPerSecond: 0);
        var task = limiter.WaitAsync(MB, CancellationToken.None);
        Assert.True(task.IsCompleted);
        await task;
    }
}
