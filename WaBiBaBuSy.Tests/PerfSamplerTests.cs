using WaBiBaBuSy.Core.Services.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PerfSamplerTests
{
    [Fact]
    public void FirstSample_HasMemoryButNoCpu_SecondHasCpu()
    {
        var sampler = new PerfSampler();
        var first = sampler.Sample();
        Assert.True(first.AppMemoryMb > 0);
        Assert.Null(first.AppCpuPercent);   // CPU% needs two samples

        var spin = DateTime.UtcNow.AddMilliseconds(50);
        while (DateTime.UtcNow < spin) { }  // burn a little CPU

        var second = sampler.Sample();
        Assert.NotNull(second.AppCpuPercent);
        Assert.InRange(second.AppCpuPercent!.Value, 0, 100);
    }
}
