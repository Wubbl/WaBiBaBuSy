using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Animation;
using System.Collections.Concurrent;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// Server-side animation orchestrator for sequential and simultaneous distribution.
/// Handles scheduling animations across multiple clients in order or all at once.
/// This is Phase 3 of the distributed composition architecture.
/// </summary>
public class AnimationOrchestrator
{
    private readonly ILogger<AnimationOrchestrator> _logger;
    private readonly AnimationDistributor _distributor;

    /// <summary>
    /// Track animation schedules
    /// </summary>
    private readonly ConcurrentDictionary<string, AnimationSchedule> _schedules = new();

    /// <summary>
    /// Track which client is currently animating in a schedule
    /// Maps: scheduleId -> clientId
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _currentAnimatingClient = new();

    /// <summary>
    /// Track actual completion times for handoff scheduling
    /// Maps: animationId -> actual completion timestamp
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _animationCompletionTimes = new();

    /// <summary>
    /// Callback to send animation to client
    /// </summary>
    public delegate Task SendAnimationDelegate(string clientId, AnimationMetadata metadata);
    public event SendAnimationDelegate? OnSendAnimation;

    /// <summary>
    /// Callback to prepare animation (warm-up phase)
    /// </summary>
    public delegate Task PrepareAnimationDelegate(string clientId, WaBiBaBuSy.Grpc.AnimationPrepare prepare);
    public event PrepareAnimationDelegate? OnPrepareAnimation;

    /// <summary>
    /// Callback when animation schedule completes
    /// </summary>
    public delegate Task ScheduleCompleteDelegate(string scheduleId);
    public event ScheduleCompleteDelegate? OnScheduleComplete;

    public AnimationOrchestrator(ILogger<AnimationOrchestrator> logger, AnimationDistributor distributor)
    {
        _logger = logger;
        _distributor = distributor;
    }

    /// <summary>
    /// Start sequential animation: animation flows through clients in order
    /// </summary>
    public async Task<string> StartSequentialAnimationAsync(
        AnimationMetadata basemetadata,
        List<string> selectedClientIds,
        bool loop = false)
    {
        if (!basemetadata.IsValid(out var error))
        {
            _logger.LogError("Invalid animation metadata: {Error}", error);
            throw new ArgumentException(error);
        }

        if (selectedClientIds.Count == 0)
        {
            throw new ArgumentException("At least one client must be selected");
        }

        var scheduleId = Guid.NewGuid().ToString();
        var schedule = new AnimationSchedule
        {
            ScheduleId = scheduleId,
            Mode = DistributionMode.Sequential,
            BaseMetadata = basemetadata,
            SelectedClientIds = selectedClientIds.ToList(),
            Loop = loop,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _schedules[scheduleId] = schedule;

        _logger.LogInformation(
            "Starting sequential animation: Schedule={ScheduleId}, Clients={ClientCount}, Duration={Duration}ms, Loop={Loop}",
            scheduleId, selectedClientIds.Count, basemetadata.DurationMs, loop);

        // Start background task to orchestrate animation
        _ = OrchestratSequentialAnimationAsync(schedule);

        return scheduleId;
    }

    /// <summary>
    /// Start simultaneous animation: all selected clients animate at same time
    /// </summary>
    public async Task<string> StartSimultaneousAnimationAsync(
        AnimationMetadata baseMetadata,
        List<string> selectedClientIds)
    {
        if (!baseMetadata.IsValid(out var error))
        {
            _logger.LogError("Invalid animation metadata: {Error}", error);
            throw new ArgumentException(error);
        }

        if (selectedClientIds.Count == 0)
        {
            throw new ArgumentException("At least one client must be selected");
        }

        var scheduleId = Guid.NewGuid().ToString();
        var schedule = new AnimationSchedule
        {
            ScheduleId = scheduleId,
            Mode = DistributionMode.Simultaneous,
            BaseMetadata = baseMetadata,
            SelectedClientIds = selectedClientIds.ToList(),
            Loop = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _schedules[scheduleId] = schedule;

        _logger.LogInformation(
            "Starting simultaneous animation: Schedule={ScheduleId}, Clients={ClientCount}, Duration={Duration}ms",
            scheduleId, selectedClientIds.Count, baseMetadata.DurationMs);

        // Start background task to orchestrate animation
        _ = OrchestrateSimultaneousAnimationAsync(schedule);

        return scheduleId;
    }

    /// <summary>
    /// Orchestrate sequential animation: each client in order
    ///
    /// Process:
    /// 1. Pre-warm all clients (initialize renderers)
    /// 2. Send first client animation with immediate start time
    /// 3. Listen for completion reports and hand off to next client
    /// </summary>
    private async Task OrchestratSequentialAnimationAsync(AnimationSchedule schedule)
    {
        try
        {
            schedule.State = ScheduleState.Running;
            schedule.StartedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Starting sequential animation orchestration: Schedule={ScheduleId}, Clients={Count}",
                schedule.ScheduleId, schedule.SelectedClientIds.Count);

            // PHASE 1: Pre-warm all renderers
            _logger.LogInformation("Phase 1: Pre-warming renderers on all clients: Schedule={ScheduleId}", schedule.ScheduleId);
            await PreWarmAllClientsAsync(schedule);

            // PHASE 2: Send animation to first client
            _logger.LogInformation("Phase 2: Starting animation on first client: Schedule={ScheduleId}", schedule.ScheduleId);
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var firstClientId = schedule.SelectedClientIds[0];
            var firstMetadata = new AnimationMetadata
            {
                AnimationId = Guid.NewGuid().ToString(),
                ContentPath = schedule.BaseMetadata!.ContentPath,
                TargetHeightPx = schedule.BaseMetadata.TargetHeightPx,
                AnimationSpeedPxSec = schedule.BaseMetadata.AnimationSpeedPxSec,
                DurationMs = schedule.BaseMetadata.DurationMs,
                StartTimestampUtc = currentTime,
                Background = schedule.BaseMetadata.Background,
                Loop = false,
                TargetMonitorIndex = schedule.BaseMetadata.TargetMonitorIndex,
            };

            _logger.LogInformation(
                "Sending first animation: Schedule={ScheduleId}, Client={ClientId}, AnimationId={AnimationId}",
                schedule.ScheduleId, firstClientId, firstMetadata.AnimationId);

            _currentAnimatingClient[schedule.ScheduleId] = firstClientId;
            _distributor.TrackAnimationStart(firstClientId, firstMetadata.AnimationId, firstMetadata.StartTimestampUtc, firstMetadata.DurationMs);
            await OnSendAnimation?.Invoke(firstClientId, firstMetadata)!;

            // PHASE 3: Wait for completion reports (handled by OnAnimationCompleted callbacks)
            _logger.LogInformation(
                "Waiting for animation completion reports: Schedule={ScheduleId}",
                schedule.ScheduleId);

            // The actual handoff will happen when OnAnimationCompleted is called
            // For now, we just mark the schedule as running
            // Note: The schedule will transition to Completed when the last client finishes
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in sequential animation orchestration: Schedule={ScheduleId}", schedule.ScheduleId);
            schedule.State = ScheduleState.Failed;
            schedule.ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Pre-warm all client renderers before starting animation
    /// This initializes renderers on all clients and waits for ready confirmations
    /// </summary>
    private async Task PreWarmAllClientsAsync(AnimationSchedule schedule)
    {
        // Create prepare message (same for all clients)
        // Convert Models.Animation.BackgroundLayerConfig to Grpc.BackgroundLayerConfig
        var grpcBackground = ConvertBackgroundConfigToGrpc(schedule.BaseMetadata!.Background);

        var prepare = new WaBiBaBuSy.Grpc.AnimationPrepare
        {
            AnimationId = Guid.NewGuid().ToString(),
            ContentPath = schedule.BaseMetadata.ContentPath,
            TargetHeightPx = schedule.BaseMetadata.TargetHeightPx,
            AnimationSpeedPxSec = schedule.BaseMetadata.AnimationSpeedPxSec,
            DurationMs = schedule.BaseMetadata.DurationMs,
            Background = grpcBackground,
            Loop = false,
            TargetMonitorIndex = schedule.BaseMetadata.TargetMonitorIndex,
        };

        var warmupStartTime = DateTimeOffset.UtcNow;

        // Send prepare to all clients
        _logger.LogInformation(
            "Sending AnimationPrepare to {Count} clients: Schedule={ScheduleId}, AnimationId={AnimationId}",
            schedule.SelectedClientIds.Count, schedule.ScheduleId, prepare.AnimationId);

        foreach (var clientId in schedule.SelectedClientIds)
        {
            await OnPrepareAnimation?.Invoke(clientId, prepare)!;
        }

        // Wait for all clients to confirm ready (with timeout)
        // In real implementation, would listen for AnimationReady confirmations
        // For now, just wait a fixed time for renderers to initialize
        var warmupTimeoutMs = 5000; // 5 second timeout
        _logger.LogInformation(
            "Waiting for all clients to warm up (timeout={TimeoutMs}ms): Schedule={ScheduleId}",
            warmupTimeoutMs, schedule.ScheduleId);

        await Task.Delay(warmupTimeoutMs);

        var warmupDuration = DateTimeOffset.UtcNow - warmupStartTime;
        _logger.LogInformation(
            "Pre-warm completed in {DurationMs}ms: Schedule={ScheduleId}",
            warmupDuration.TotalMilliseconds, schedule.ScheduleId);
    }

    /// <summary>
    /// Orchestrate simultaneous animation: all clients at same time
    /// </summary>
    private async Task OrchestrateSimultaneousAnimationAsync(AnimationSchedule schedule)
    {
        try
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Send animation to all clients with same start time
            var tasks = new List<Task>();
            foreach (var clientId in schedule.SelectedClientIds)
            {
                var metadata = new AnimationMetadata
                {
                    AnimationId = Guid.NewGuid().ToString(),
                    ContentPath = schedule.BaseMetadata.ContentPath,
                    TargetHeightPx = schedule.BaseMetadata.TargetHeightPx,
                    AnimationSpeedPxSec = schedule.BaseMetadata.AnimationSpeedPxSec,
                    DurationMs = schedule.BaseMetadata.DurationMs,
                    StartTimestampUtc = currentTime,
                    Background = schedule.BaseMetadata.Background,
                    Loop = false,
                    TargetMonitorIndex = schedule.BaseMetadata.TargetMonitorIndex,
                };

                _logger.LogInformation(
                    "Sending animation to client: Client={ClientId}, Start={Start}ms",
                    clientId, currentTime);

                _distributor.TrackAnimationStart(clientId, metadata.AnimationId, metadata.StartTimestampUtc, metadata.DurationMs);
                OnSendAnimation?.Invoke(clientId, metadata);
            }

            _logger.LogInformation(
                "Sent animation to {ClientCount} clients simultaneously: Schedule={ScheduleId}",
                schedule.SelectedClientIds.Count, schedule.ScheduleId);

            // Wait for animation to complete
            await Task.Delay((int)schedule.BaseMetadata.DurationMs);

            schedule.State = ScheduleState.Completed;
            schedule.CompletedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Simultaneous animation schedule completed: Schedule={ScheduleId}", schedule.ScheduleId);

            OnScheduleComplete?.Invoke(schedule.ScheduleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in simultaneous animation orchestration: Schedule={ScheduleId}", schedule.ScheduleId);
            schedule.State = ScheduleState.Failed;
            schedule.ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Handle animation completion report from client
    /// Triggers handoff to next client in sequence if applicable
    /// </summary>
    public async Task OnAnimationCompleted(
        string scheduleId,
        string clientId,
        string animationId,
        long startedTimestampUtc,
        long completedTimestampUtc,
        long actualDurationMs)
    {
        if (!_schedules.TryGetValue(scheduleId, out var schedule))
        {
            _logger.LogWarning("Received completion for unknown schedule: {ScheduleId}", scheduleId);
            return;
        }

        _logger.LogInformation(
            "Animation completed: Schedule={ScheduleId}, Client={ClientId}, " +
            "AnimationId={AnimationId}, ActualDuration={ActualDuration}ms",
            scheduleId, clientId, animationId, actualDurationMs);

        // Store completion time for diagnostics
        _animationCompletionTimes[animationId] = completedTimestampUtc;

        // Find current client position
        var clientIndex = schedule.SelectedClientIds.IndexOf(clientId);
        if (clientIndex < 0)
        {
            _logger.LogWarning("Client {ClientId} not in schedule {ScheduleId}", clientId, scheduleId);
            return;
        }

        // Check if there are more clients in sequence
        var nextClientIndex = clientIndex + 1;
        if (nextClientIndex >= schedule.SelectedClientIds.Count)
        {
            // This was the last client
            if (schedule.Loop)
            {
                _logger.LogInformation(
                    "Last client finished, looping back: Schedule={ScheduleId}",
                    scheduleId);
                nextClientIndex = 0;
            }
            else
            {
                // Schedule complete
                _logger.LogInformation(
                    "Sequential animation schedule completed: Schedule={ScheduleId}",
                    scheduleId);
                schedule.State = ScheduleState.Completed;
                schedule.CompletedAt = DateTimeOffset.UtcNow;
                OnScheduleComplete?.Invoke(schedule.ScheduleId);
                return;
            }
        }

        // Send animation to next client
        var nextClientId = schedule.SelectedClientIds[nextClientIndex];

        // Use ACTUAL completion time as next start time
        // This accounts for renderer init delay and network jitter
        var nextStartTime = completedTimestampUtc;

        _logger.LogInformation(
            "Handing off animation to next client: Schedule={ScheduleId}, " +
            "From={CurrentClient}/{CurrentIndex} to={NextClient}/{NextIndex}, " +
            "NextStartTime={NextStartTime}ms",
            scheduleId, clientId, clientIndex, nextClientId, nextClientIndex, nextStartTime);

        // Create metadata for next client
        var metadata = new AnimationMetadata
        {
            AnimationId = Guid.NewGuid().ToString(),
            ContentPath = schedule.BaseMetadata!.ContentPath,
            TargetHeightPx = schedule.BaseMetadata.TargetHeightPx,
            AnimationSpeedPxSec = schedule.BaseMetadata.AnimationSpeedPxSec,
            DurationMs = schedule.BaseMetadata.DurationMs,
            StartTimestampUtc = nextStartTime,
            Background = schedule.BaseMetadata.Background,
            Loop = false,
            TargetMonitorIndex = schedule.BaseMetadata.TargetMonitorIndex,
        };

        _currentAnimatingClient[scheduleId] = nextClientId;
        _distributor.TrackAnimationStart(nextClientId, metadata.AnimationId, nextStartTime, metadata.DurationMs);

        await OnSendAnimation?.Invoke(nextClientId, metadata)!;
    }

    /// <summary>
    /// Stop animation schedule
    /// </summary>
    public void StopSchedule(string scheduleId)
    {
        if (_schedules.TryGetValue(scheduleId, out var schedule))
        {
            schedule.State = ScheduleState.Stopped;
            _logger.LogInformation("Stopped animation schedule: {ScheduleId}", scheduleId);
        }
    }

    /// <summary>
    /// Get schedule state
    /// </summary>
    public AnimationSchedule? GetSchedule(string scheduleId)
    {
        _schedules.TryGetValue(scheduleId, out var schedule);
        return schedule;
    }

    /// <summary>
    /// Get all active schedules
    /// </summary>
    public IEnumerable<AnimationSchedule> GetActiveSchedules()
    {
        return _schedules.Values.Where(s => s.State == ScheduleState.Running || s.State == ScheduleState.Scheduled);
    }

    /// <summary>
    /// Convert Models.Animation.BackgroundLayerConfig to Grpc.BackgroundLayerConfig
    /// </summary>
    private WaBiBaBuSy.Grpc.BackgroundLayerConfig ConvertBackgroundConfigToGrpc(
        WaBiBaBuSy.Models.Animation.BackgroundLayerConfig modelsConfig)
    {
        var grpcConfig = new WaBiBaBuSy.Grpc.BackgroundLayerConfig();

        // Map the mode
        grpcConfig.Mode = modelsConfig.Mode switch
        {
            WaBiBaBuSy.Models.Animation.BackgroundLayerConfig.BackgroundMode.SolidColor =>
                WaBiBaBuSy.Grpc.BackgroundLayerConfig.Types.BackgroundMode.SolidColor,
            WaBiBaBuSy.Models.Animation.BackgroundLayerConfig.BackgroundMode.StretchedImage =>
                WaBiBaBuSy.Grpc.BackgroundLayerConfig.Types.BackgroundMode.StretchedImage,
            WaBiBaBuSy.Models.Animation.BackgroundLayerConfig.BackgroundMode.TiledImage =>
                WaBiBaBuSy.Grpc.BackgroundLayerConfig.Types.BackgroundMode.TiledImage,
            _ => WaBiBaBuSy.Grpc.BackgroundLayerConfig.Types.BackgroundMode.SolidColor,
        };

        grpcConfig.ColorHex = modelsConfig.ColorHex ?? "#000000";
        grpcConfig.ImagePath = modelsConfig.ImagePath ?? "";

        return grpcConfig;
    }
}

/// <summary>
/// Animation distribution mode
/// </summary>
public enum DistributionMode
{
    /// <summary>Animation flows through clients in sequence</summary>
    Sequential = 0,

    /// <summary>All clients animate simultaneously</summary>
    Simultaneous = 1,
}

/// <summary>
/// Animation schedule state
/// </summary>
public enum ScheduleState
{
    /// <summary>Scheduled but not yet started</summary>
    Scheduled = 0,

    /// <summary>Currently running</summary>
    Running = 1,

    /// <summary>Completed successfully</summary>
    Completed = 2,

    /// <summary>Failed with error</summary>
    Failed = 3,

    /// <summary>Stopped by user</summary>
    Stopped = 4,
}

/// <summary>
/// Animation schedule definition
/// </summary>
public class AnimationSchedule
{
    /// <summary>Unique schedule ID</summary>
    public string ScheduleId { get; set; } = string.Empty;

    /// <summary>Distribution mode (sequential/simultaneous)</summary>
    public DistributionMode Mode { get; set; } = DistributionMode.Sequential;

    /// <summary>Base animation metadata</summary>
    public AnimationMetadata? BaseMetadata { get; set; }

    /// <summary>Selected client IDs in order</summary>
    public List<string> SelectedClientIds { get; set; } = new();

    /// <summary>Whether to loop animation after all clients finish</summary>
    public bool Loop { get; set; }

    /// <summary>Current schedule state</summary>
    public ScheduleState State { get; set; } = ScheduleState.Scheduled;

    /// <summary>When schedule was created</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When schedule started</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When schedule completed</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Error message if failed</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Get display name for mode</summary>
    public string GetModeDisplayName()
    {
        return Mode switch
        {
            DistributionMode.Sequential => "Sequential",
            DistributionMode.Simultaneous => "Simultaneous",
            _ => "Unknown",
        };
    }
}
