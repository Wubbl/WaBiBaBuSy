using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>One monitor's D2D player as seen by the automated test mode.</summary>
public interface ITestProbeTarget
{
    /// <summary>Monitor index on this machine.</summary>
    int MonitorIndex { get; }

    /// <summary>Timecode strip on/off and simulated clock skew. Apply before the scene's start command.</summary>
    Task SetTestModeAsync(bool timecode, int clockSkewMs);

    /// <summary>Probe the player; null when it does not answer within <paramref name="timeout"/>.</summary>
    Task<PlayerProbeReply?> ProbeAsync(PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct);
}
