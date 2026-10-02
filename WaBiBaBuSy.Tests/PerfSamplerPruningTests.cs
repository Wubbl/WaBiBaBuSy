using System.Diagnostics;
using WaBiBaBuSy.Core.Services.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PerfSamplerPruningTests
{
    [Fact]
    public void PlayerThatExited_IsForgotten()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        bool playerRunning = true;
        try
        {
            var sampler = new PerfSampler(() => playerRunning ? new[] { Process.GetProcessById(child.Id) } : Array.Empty<Process>());
            sampler.Sample();
            sampler.Sample();
            Assert.Equal(2, sampler.TrackedProcessCount);   // this process + the "player"

            playerRunning = false;   // the player was replaced (new scene) or exited
            var sample = sampler.Sample();
            Assert.Equal(0, sample.PlayerProcesses);
            Assert.Equal(1, sampler.TrackedProcessCount);
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }
}
