namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Automated test mode: report the first frame rendered at or after <see cref="AtLocalUtcMs"/>
/// (or, with <see cref="ExactElapsedMs"/>, one offscreen frame at exactly that elapsed). The answer
/// arrives on stderr as <c>SIGNAL:PROBE:{json}</c>; there is no stdout reply.
/// </summary>
public class PlayerCommandProbe : PlayerMessageBase
{
    public PlayerCommandProbe()
    {
        MessageType = "cmd_probe";
    }

    public string ProbeId { get; set; } = string.Empty;
    public long AtLocalUtcMs { get; set; }
    public bool Capture { get; set; }
    public long? ExactElapsedMs { get; set; }
    public string CaptureDirectory { get; set; } = string.Empty;
}
