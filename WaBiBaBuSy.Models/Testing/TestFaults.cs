namespace WaBiBaBuSy.Models.Testing;

/// <summary>Faults a test run can inject on a remote client (TEST_FAULT, automated test mode design §12).</summary>
public static class TestFaults
{
    /// <summary>The link is lost for the fault length; the client then reconnects through its real backoff.</summary>
    public const string Drop = "drop";
    /// <summary>No heartbeats, sync responses or thumbnails for the fault length; the connection stays open (a powered-off client as the server sees it).</summary>
    public const string Silent = "silent";

    public const int MinMs = 1000;
    public const int MaxMs = 120000;

    public static bool IsKnown(string? kind) => kind is Drop or Silent;

    /// <summary>The fault length a client applies.</summary>
    public static int ClampMs(int ms) => Math.Clamp(ms, MinMs, MaxMs);
}
