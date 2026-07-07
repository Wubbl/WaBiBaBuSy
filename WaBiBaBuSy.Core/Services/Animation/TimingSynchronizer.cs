using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Animation;
using System.Collections.Concurrent;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// Server-side timing synchronization service for distributed animation playback.
/// Broadcasts timing sync messages to keep all clients in sync (Phase 2).
/// </summary>
public class TimingSynchronizer
{
    private readonly ILogger<TimingSynchronizer> _logger;
    private readonly AnimationDistributor _distributor;

    /// <summary>
    /// Callback to send timing sync to client
    /// </summary>
    public delegate Task SendTimingSyncDelegate(string clientId, AnimationTimingSync sync);
    public event SendTimingSyncDelegate? OnSendTimingSync;

    /// <summary>
    /// Track active sync sessions
    /// </summary>
    private readonly ConcurrentDictionary<string, TimingSyncSession> _activeSessions = new();

    /// <summary>
    /// Cancellation tokens for running sync loops
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _syncCancellationTokens = new();

    public TimingSynchronizer(ILogger<TimingSynchronizer> logger, AnimationDistributor distributor)
    {
        _logger = logger;
        _distributor = distributor;
    }

    /// <summary>
    /// Start timing synchronization for animating clients.
    /// NOTE: This drift-telemetry loop belongs to the gRPC orchestrator path
    /// (AnimationOrchestrator); the d2d_crossscreen path relies on deterministic
    /// math + clock-offset compensation instead and does not use it.
    /// </summary>
    public Task StartTimingSyncAsync(
        List<string> clientIds,
        int syncIntervalMs = 1000,
        int maxDriftToleranceMs = 50)
    {
        if (clientIds.Count == 0)
        {
            _logger.LogWarning("StartTimingSync called with no clients");
            return Task.CompletedTask;
        }

        var sessionId = Guid.NewGuid().ToString();
        var session = new TimingSyncSession
        {
            SessionId = sessionId,
            ClientIds = clientIds.ToList(),
            SyncIntervalMs = syncIntervalMs,
            MaxDriftToleranceMs = maxDriftToleranceMs,
            StartedAt = DateTimeOffset.UtcNow,
            State = SyncSessionState.Running,
        };

        _activeSessions[sessionId] = session;

        _logger.LogInformation(
            "Starting timing sync session: ID={SessionId}, Clients={ClientCount}, Interval={Interval}ms",
            sessionId, clientIds.Count, syncIntervalMs);

        // Start background sync loop
        var cts = new CancellationTokenSource();
        _syncCancellationTokens[sessionId] = cts;

        _ = RunTimingSyncLoopAsync(session, cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Run the timing synchronization loop
    /// </summary>
    private async Task RunTimingSyncLoopAsync(TimingSyncSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var currentUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Only this session's clients — two concurrent sessions must not
                // double-send to every animating client.
                var sessionClientIds = new HashSet<string>(session.ClientIds);
                var animatingClients = _distributor.GetAnimatingClients()
                    .Where(c => sessionClientIds.Contains(c.ClientId))
                    .ToList();

                if (animatingClients.Count == 0)
                {
                    _logger.LogDebug("No animating clients, stopping sync session: {SessionId}", session.SessionId);
                    break;
                }

                // Send timing sync to each animating client
                var tasks = new List<Task>();
                foreach (var clientState in animatingClients)
                {
                    var expectedPositionMs = _distributor.GetExpectedAnimationPosition(
                        clientState.ClientId, currentUtcMs);

                    var sync = new AnimationTimingSync
                    {
                        ServerTimestampUtc = currentUtcMs,
                        AnimationId = clientState.AnimationId,
                        ExpectedPositionMs = expectedPositionMs,
                    };

                    session.SyncMessagesSent++;

                    _logger.LogDebug(
                        "Sending timing sync: Client={ClientId}, Animation={AnimationId}, Expected={Expected}ms",
                        clientState.ClientId, clientState.AnimationId, expectedPositionMs);

                    if (OnSendTimingSync != null)
                    {
                        tasks.Add(OnSendTimingSync(clientState.ClientId, sync));
                    }

                    // Track sync for diagnostics
                    session.LastSyncAt = DateTimeOffset.UtcNow;
                }

                // Wait for all sends to complete
                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                }

                // Wait for next sync interval
                await Task.Delay(session.SyncIntervalMs, cancellationToken);
            }

            session.State = SyncSessionState.Completed;
            session.CompletedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "Timing sync session completed: ID={SessionId}, Messages={MessageCount}",
                session.SessionId, session.SyncMessagesSent);
        }
        catch (OperationCanceledException)
        {
            session.State = SyncSessionState.Stopped;
            _logger.LogInformation("Timing sync session stopped: {SessionId}", session.SessionId);
        }
        catch (Exception ex)
        {
            session.State = SyncSessionState.Failed;
            session.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Error in timing sync loop: {SessionId}", session.SessionId);
        }
        finally
        {
            _activeSessions.TryRemove(session.SessionId, out _);
            _syncCancellationTokens.TryRemove(session.SessionId, out var cts);
            cts?.Dispose();
        }
    }

    /// <summary>
    /// Stop timing synchronization
    /// </summary>
    public void StopTimingSync(string sessionId)
    {
        if (_syncCancellationTokens.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _logger.LogInformation("Stopped timing sync session: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Stop all timing synchronization
    /// </summary>
    public void StopAllTimingSync()
    {
        var sessionIds = _syncCancellationTokens.Keys.ToList();
        foreach (var sessionId in sessionIds)
        {
            StopTimingSync(sessionId);
        }
        _logger.LogInformation("Stopped all timing sync sessions");
    }

    /// <summary>
    /// Get timing sync session info
    /// </summary>
    public TimingSyncSession? GetSession(string sessionId)
    {
        _activeSessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>
    /// Get all active timing sync sessions
    /// </summary>
    public IEnumerable<TimingSyncSession> GetActiveSessions()
    {
        return _activeSessions.Values.ToList();
    }

    /// <summary>
    /// Get session statistics
    /// </summary>
    public (int activeSessions, long totalMessagesSent) GetStatistics()
    {
        var sessions = _activeSessions.Values.ToList();
        var totalMessages = sessions.Sum(s => s.SyncMessagesSent);
        return (sessions.Count, totalMessages);
    }
}

/// <summary>
/// Timing sync session state
/// </summary>
public enum SyncSessionState
{
    /// <summary>Session is running</summary>
    Running = 0,

    /// <summary>Session completed normally</summary>
    Completed = 1,

    /// <summary>Session stopped by user</summary>
    Stopped = 2,

    /// <summary>Session failed with error</summary>
    Failed = 3,
}

/// <summary>
/// Timing synchronization session
/// </summary>
public class TimingSyncSession
{
    /// <summary>Unique session ID</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Client IDs in this session</summary>
    public List<string> ClientIds { get; set; } = new();

    /// <summary>Sync interval in milliseconds</summary>
    public int SyncIntervalMs { get; set; } = 1000;

    /// <summary>Maximum allowed clock drift in milliseconds</summary>
    public int MaxDriftToleranceMs { get; set; } = 50;

    /// <summary>Current session state</summary>
    public SyncSessionState State { get; set; } = SyncSessionState.Running;

    /// <summary>When session started</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When session completed</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Last time sync message was sent</summary>
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Total sync messages sent</summary>
    public long SyncMessagesSent { get; set; }

    /// <summary>Error message if failed</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Get session duration</summary>
    public TimeSpan GetDuration()
    {
        var endTime = CompletedAt ?? DateTimeOffset.UtcNow;
        return endTime - StartedAt;
    }

    /// <summary>Get messages per second</summary>
    public double GetMessagesPerSecond()
    {
        var duration = GetDuration().TotalSeconds;
        if (duration == 0) return 0;
        return SyncMessagesSent / duration;
    }
}
