using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>Probe or configure all local players of a machine at once.</summary>
public static class ProbeFanOut
{
    /// <summary>One reply per target, in target order; silence or an exception becomes an error reply.</summary>
    public static async Task<IReadOnlyList<PlayerProbeReply>> ProbeAllAsync(
        IReadOnlyList<ITestProbeTarget> targets, PlayerProbeRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var tasks = targets.Select(async target =>
        {
            PlayerProbeReply? reply = null;
            string? error = null;
            try
            {
                reply = await target.ProbeAsync(request, timeout, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = ex.Message;
            }
            reply ??= new PlayerProbeReply { ProbeId = request.ProbeId, Error = error ?? "no reply from player" };
            reply.MonitorIndex = target.MonitorIndex;
            return reply;
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>Apply test mode settings to every target.</summary>
    public static Task SetTestModeAllAsync(IReadOnlyList<ITestProbeTarget> targets, bool timecode, int clockSkewMs) =>
        Task.WhenAll(targets.Select(t => t.SetTestModeAsync(timecode, clockSkewMs)));

    /// <summary>Prefix of the player's probe reply line on stderr: <c>SIGNAL:PROBE:{json}</c>.</summary>
    public const string ProbeSignalPrefix = "SIGNAL:PROBE:";

    /// <summary>
    /// Parse a player's <c>SIGNAL:PROBE:{json}</c> stderr line. Never throws: it runs on the host's stderr event thread.
    /// </summary>
    /// <param name="deserialize">The player protocol's JSON deserializer (the host passes Newtonsoft's).</param>
    /// <param name="problem">Why the line gave no reply; null on success.</param>
    /// <returns>The reply, or null when the line is not a probe signal, is malformed or carries no probe id.</returns>
    public static PlayerProbeReply? ParseProbeSignal(string line, Func<string, PlayerProbeReply?> deserialize, out string? problem)
    {
        if (!line.StartsWith(ProbeSignalPrefix, StringComparison.Ordinal))
        {
            problem = "not a SIGNAL:PROBE line";
            return null;
        }
        PlayerProbeReply? reply;
        try
        {
            reply = deserialize(line[ProbeSignalPrefix.Length..]);
        }
        catch (Exception ex)
        {
            problem = $"malformed: {ex.Message}";
            return null;
        }
        if (reply == null || string.IsNullOrEmpty(reply.ProbeId))
        {
            problem = "no probe id";
            return null;
        }
        problem = null;
        return reply;
    }

    /// <summary>
    /// Wait for a player's reply: the reply, or null once <paramref name="timeout"/> passes. A cancelled
    /// <paramref name="ct"/> throws <see cref="OperationCanceledException"/>. The timer is released as soon as the reply arrives.
    /// </summary>
    public static async Task<PlayerProbeReply?> WaitForReplyAsync(Task<PlayerProbeReply> reply, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await reply.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
