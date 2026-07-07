namespace WaBiBaBuSy.Models.Networking;

/// <summary>Exponential reconnect backoff: 1s, 2s, 4s, 8s, 16s, then capped at 30s.</summary>
public class ReconnectBackoff
{
    private int _attempt;

    public TimeSpan NextDelay()
    {
        var seconds = Math.Min(30, 1 << Math.Min(_attempt, 5));
        _attempt++;
        return TimeSpan.FromSeconds(seconds);
    }

    public void Reset() => _attempt = 0;
}
