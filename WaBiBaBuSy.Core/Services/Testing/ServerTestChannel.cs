using System.Collections.Concurrent;
using System.Text.Json;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Grpc.Services;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary><see cref="ITestTransport"/> over the server's SyncStream + SubmitProbeResult RPC.</summary>
public sealed class ServerTestChannel : ITestTransport, IDisposable
{
    private readonly WallpaperSyncService _sync;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteProbeResult>> _waiters = new();

    public ServerTestChannel(WallpaperSyncService sync)
    {
        _sync = sync;
        _sync.ProbeResultReceived += OnProbeResultReceived;
    }

    public Task<bool> SendTestModeAsync(string clientId, bool timecode, int clockSkewMs) =>
        _sync.SendCommandToClientAsync(clientId, new SyncCommand
        {
            Type = CommandType.TestMode,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Params = new SyncParameters { TestTimecode = timecode, TestClockSkewMs = clockSkewMs },
        });

    public async Task<RemoteProbeResult?> ProbeAsync(string clientId, ProbeRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var key = Key(clientId, request.ProbeId);
        var waiter = new TaskCompletionSource<RemoteProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[key] = waiter;
        try
        {
            bool sent = await _sync.SendCommandToClientAsync(clientId, new SyncCommand
            {
                Type = CommandType.TestProbe,
                TimestampUtc = request.AtServerUtcMs,
                Params = new SyncParameters
                {
                    TestProbeId = request.ProbeId,
                    TestCapture = request.Capture,
                    TestHasExactElapsed = request.ExactElapsedMs.HasValue,
                    TestExactElapsedMs = request.ExactElapsedMs ?? 0,
                },
            });
            if (!sent)
                return new RemoteProbeResult { ClientId = clientId, ProbeId = request.ProbeId, Error = "client has no command stream" };

            var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout, ct));
            return finished == waiter.Task ? await waiter.Task : null;
        }
        finally
        {
            _waiters.TryRemove(key, out _);
        }
    }

    public async Task<string?> FetchLogsAsync(string clientId, long fromUtcMs, long toUtcMs, TimeSpan timeout) =>
        (await _sync.FetchClientLogsAsync(clientId, fromUtcMs, toUtcMs, timeout))?.LogContent;

    private void OnProbeResultReceived(object? sender, ProbeResultReceivedEventArgs e)
    {
        RemoteProbeResult result;
        try
        {
            result = JsonSerializer.Deserialize<RemoteProbeResult>(e.ResultJson) ?? new RemoteProbeResult();
        }
        catch (JsonException ex)
        {
            result = new RemoteProbeResult { Error = $"malformed probe result: {ex.Message}" };
        }
        result.ClientId = e.ClientId;
        result.ProbeId = e.ProbeId;
        result.Captures = new Dictionary<int, byte[]>(e.Captures);
        if (_waiters.TryGetValue(Key(e.ClientId, e.ProbeId), out var waiter))
            waiter.TrySetResult(result);
    }

    private static string Key(string clientId, string probeId) => $"{clientId}|{probeId}";

    public void Dispose() => _sync.ProbeResultReceived -= OnProbeResultReceived;
}
