namespace WaBiBaBuSy.Models.Testing;

public sealed class PositionCheckResult
{
    public string NodeId { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public int MonitorIndex { get; set; }
    public Verdict Verdict { get; set; }
    public string Message { get; set; } = string.Empty;
    public MarkerExpectation Expected { get; set; } = new();
    public MarkerDetection? Detected { get; set; }
    /// <summary>Where the player itself believed the marker was (device px).</summary>
    public float PlayerCenterX { get; set; }
    public float PlayerCenterY { get; set; }
    /// <summary>Detected vs expected center: what people see.</summary>
    public double ErrorPx { get; set; }
    /// <summary>Player belief vs expected center: a math / config mismatch when large.</summary>
    public double PlayerErrorPx { get; set; }
}

/// <summary>Turns expected / player-believed / detected marker positions into a verdict (design §5.2).</summary>
public static class PositionCheck
{
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
