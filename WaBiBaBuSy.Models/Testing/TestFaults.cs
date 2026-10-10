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

/// <summary>One injected fault as the runner saw it from the server (UTC ms on the server clock).</summary>
public sealed class FaultReport
{
    public string NodeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Ms { get; set; }
    public long SentAtUtcMs { get; set; }
    /// <summary>First poll at which the server no longer listed the client.</summary>
    public long? LostAtUtcMs { get; set; }
    /// <summary>First poll after <see cref="LostAtUtcMs"/> at which the server listed the client again.</summary>
    public long? BackAtUtcMs { get; set; }
    /// <summary>drop: back − (sent + fault length).</summary>
    public long? ReconnectMs { get; set; }
    /// <summary>silent: lost − sent.</summary>
    public long? SweepMs { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Skipped;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Verdict of a fault from its timeline (design §12): pure, so the thresholds are unit-tested.</summary>
public static class FaultEvaluation
{
    public static void Evaluate(FaultReport r, int maxReconnectMs, int maxSweepMs)
    {
        if (r.Kind == TestFaults.Drop)
        {
            if (r.LostAtUtcMs == null) { Set(r, Verdict.Fail, "the server never lost the node (fault not applied?)"); return; }
            if (r.BackAtUtcMs == null) { Set(r, Verdict.Fail, $"lost after {r.LostAtUtcMs - r.SentAtUtcMs} ms, never came back"); return; }
            long reconnect = r.BackAtUtcMs.Value - (r.SentAtUtcMs + r.Ms);
            if (reconnect < 0) { Set(r, Verdict.Fail, $"back {-reconnect} ms before the fault ended (fault not applied?)"); return; }
            r.ReconnectMs = reconnect;
            var v = reconnect <= maxReconnectMs ? Verdict.Pass : reconnect <= 2L * maxReconnectMs ? Verdict.Warn : Verdict.Fail;
            Set(r, v, $"reconnected {reconnect} ms after the link came back (limit {maxReconnectMs} ms)");
        }
        else
        {
            if (r.LostAtUtcMs == null) { Set(r, Verdict.Fail, "the server never dropped the silent node"); return; }
            long sweep = r.LostAtUtcMs.Value - r.SentAtUtcMs;
            r.SweepMs = sweep;
            if (r.BackAtUtcMs == null) { Set(r, Verdict.Fail, $"dropped after {sweep} ms, never came back after the silence"); return; }
            var v = sweep <= maxSweepMs ? Verdict.Pass : Verdict.Warn;
            Set(r, v, $"dropped after {sweep} ms (limit {maxSweepMs} ms), back {r.BackAtUtcMs - r.SentAtUtcMs} ms after the fault started");
        }
    }

    private static void Set(FaultReport r, Verdict v, string message)
    {
        r.Verdict = v;
        r.Message = message;
    }
}
