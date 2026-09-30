using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>How the test runner reaches remote clients (gRPC in the app, a fake in tests).</summary>
public interface ITestTransport
{
    /// <summary>False when the client has no command stream.</summary>
    Task<bool> SendTestModeAsync(string clientId, bool timecode, int clockSkewMs);

    /// <summary>The client's result, or null when none arrived within <paramref name="timeout"/>.</summary>
    Task<RemoteProbeResult?> ProbeAsync(string clientId, ProbeRequest request, TimeSpan timeout, CancellationToken ct);

    /// <summary>Log lines between the two UTC instants, or null on timeout.</summary>
    Task<string?> FetchLogsAsync(string clientId, long fromUtcMs, long toUtcMs, TimeSpan timeout);
}
