namespace WaBiBaBuSy.Models.Testing;

/// <summary>Error texts a client sends back; the runner's preflight matches on them.</summary>
public static class TestModeErrors
{
    public const string Disabled = "test runs are disabled on this client (Settings → Client → Allow test runs)";
    public const string NoPlayer = "no player running on this client";
    /// <summary>Server side: the client has no SyncStream to send the command on (it disconnected).</summary>
    public const string NoCommandStream = "client has no command stream";
}
