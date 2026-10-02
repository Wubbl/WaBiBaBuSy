namespace WaBiBaBuSy.Models.Testing;

/// <summary>One node-monitor's marker position check (report.json <c>positions[]</c>).</summary>
public sealed class PositionCheckResult
{
    /// <summary>Node the capture came from.</summary>
    public string NodeId { get; set; } = string.Empty;
    /// <summary>Display name of the node.</summary>
    public string NodeName { get; set; } = string.Empty;
    /// <summary>Monitor index on that node.</summary>
    public int MonitorIndex { get; set; }
    /// <summary>Pass / Fail, or Skipped when nothing could be checked (no capture, marker on the edge, not started).</summary>
    public Verdict Verdict { get; set; }
    /// <summary>Human-readable reason for the verdict.</summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>Where the server-side math puts the marker (device px).</summary>
    public MarkerExpectation Expected { get; set; } = new();
    /// <summary>What the capture showed; null when there was no capture.</summary>
    public MarkerDetection? Detected { get; set; }
    /// <summary>Where the player itself believed the marker was (device px).</summary>
    public float PlayerCenterX { get; set; }
    /// <summary>Vertical counterpart of <see cref="PlayerCenterX"/> (device px).</summary>
    public float PlayerCenterY { get; set; }
    /// <summary>Detected vs expected center: what people see.</summary>
    public double ErrorPx { get; set; }
    /// <summary>Player belief vs expected center: a math / config mismatch when large.</summary>
    public double PlayerErrorPx { get; set; }
}

/// <summary>Turns expected / player-believed / detected marker positions into a verdict (design §5.2).</summary>
public static class PositionCheck
{
    /// <summary>
    /// Verdict for one sample. <paramref name="detected"/> null = no capture: nothing is checked (Skipped),
    /// whatever the expectation. <paramref name="scale"/> converts the player's canvas-unit rectangle to device
    /// px; <paramref name="tolerancePx"/> is the allowed center error.
    /// </summary>
    public static PositionCheckResult Evaluate(NodeProbeSample sample, MarkerExpectation expected, MarkerDetection? detected, float scale, double tolerancePx)
    {
        float s = scale > 0 ? scale : 1f;
        var r = new PositionCheckResult
        {
            NodeId = sample.NodeId,
            NodeName = sample.NodeName,
            MonitorIndex = sample.MonitorIndex,
            Expected = expected,
            Detected = detected,
            PlayerCenterX = (sample.PlayerAnimX + sample.PlayerAnimWidth / 2f) * s,
            PlayerCenterY = (sample.PlayerAnimY + sample.PlayerAnimHeight / 2f) * s,
        };
        bool found = detected?.Found == true;

        switch (expected.Visibility)
        {
            case MarkerVisibility.NotStarted:
                return Set(r, found ? Verdict.Fail : Verdict.Skipped, found ? "marker visible before the start" : "marker not shown yet");
            case MarkerVisibility.OffScreen:
                // Without a capture "not found" proves nothing — same as Visible without a capture.
                if (detected == null) return Set(r, Verdict.Skipped, "no capture (marker expected off-screen)");
                return Set(r, found ? Verdict.Fail : Verdict.Pass, found ? "marker on screen, expected on another node" : "off-screen as expected");
            case MarkerVisibility.Partial:
                return Set(r, Verdict.Skipped, "marker straddles the edge");
        }

        r.PlayerErrorPx = Distance(r.PlayerCenterX, r.PlayerCenterY, expected.CenterX, expected.CenterY);
        if (detected == null) return Set(r, Verdict.Skipped, "no capture");
        if (!found) return Set(r, Verdict.Fail, "marker not found in the capture");

        r.ErrorPx = Distance(detected!.CenterX, detected.CenterY, expected.CenterX, expected.CenterY);
        var expectedOrientation = sample.PlayerFlipped ? MarkerOrientation.Mirrored : MarkerOrientation.Normal;
        if (detected.Orientation != MarkerOrientation.Unknown && detected.Orientation != expectedOrientation)
            return Set(r, Verdict.Fail, $"sprite drawn {detected.Orientation.ToString().ToLowerInvariant()}, player says flipped={sample.PlayerFlipped}");

        return r.ErrorPx <= tolerancePx
            ? Set(r, Verdict.Pass, $"within {r.ErrorPx:F1} px")
            : Set(r, Verdict.Fail, $"off by {r.ErrorPx:F1} px (player math off by {r.PlayerErrorPx:F1} px)");
    }

    private static PositionCheckResult Set(PositionCheckResult r, Verdict v, string message)
    {
        r.Verdict = v;
        r.Message = message;
        return r;
    }

    private static double Distance(float x1, float y1, float x2, float y2) =>
        Math.Sqrt((x1 - x2) * (double)(x1 - x2) + (y1 - y2) * (double)(y1 - y2));
}
