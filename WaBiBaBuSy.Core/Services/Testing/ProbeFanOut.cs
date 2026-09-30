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
}
