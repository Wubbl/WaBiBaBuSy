using WaBiBaBuSy.Models.Networking;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ReconnectBackoffTests
{
    [Fact]
    public void DelaysFollowExponentialSequenceCappedAt30()
    {
        var backoff = new ReconnectBackoff();
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(4), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(16), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
    }

    [Fact]
    public void Reset_RestartsSequence()
    {
        var backoff = new ReconnectBackoff();
        backoff.NextDelay();
        backoff.NextDelay();
        backoff.NextDelay();
        backoff.Reset();
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
    }
}
