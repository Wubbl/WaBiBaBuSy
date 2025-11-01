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
    /// Callback to send animation to client
    /// </summary>
    public delegate Task SendAnimationDelegate(string clientId, AnimationMetadata metadata);
    public event SendAnimationDelegate? OnSendAnimation;

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
    /// </summary>
    private async Task OrchestratSequentialAnimationAsync(AnimationSchedule schedule)
    {
        try
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var clientIndex = 0;

            while (true)
            {
                // Get next client
                var clientId = schedule.SelectedClientIds[clientIndex];

                // Create metadata for this client
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

                // Send to client
                _logger.LogInformation(
                    "Sending animation to client {ClientIndex}/{TotalClients}: Client={ClientId}, Start={Start}ms",
                    clientIndex + 1, schedule.SelectedClientIds.Count, clientId, currentTime);

                _distributor.TrackAnimationStart(clientId, metadata.AnimationId, metadata.StartTimestampUtc, metadata.DurationMs);
                OnSendAnimation?.Invoke(clientId, metadata);

                // Wait for this animation to complete
                await Task.Delay((int)schedule.BaseMetadata.DurationMs);

                // Move to next client
                clientIndex++;
                currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Check if we've processed all clients
                if (clientIndex >= schedule.SelectedClientIds.Count)
                {
                    if (schedule.Loop)
                    {
                        _logger.LogInformation("Looping animation sequence: Schedule={ScheduleId}", schedule.ScheduleId);
                        clientIndex = 0;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            schedule.State = ScheduleState.Completed;
            schedule.CompletedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Sequential animation schedule completed: Schedule={ScheduleId}", schedule.ScheduleId);

            OnScheduleComplete?.Invoke(schedule.ScheduleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in sequential animation orchestration: Schedule={ScheduleId}", schedule.ScheduleId);
            schedule.State = ScheduleState.Failed;
            schedule.ErrorMessage = ex.Message;
        }
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

    /// <summary>Clients divided into groups, groups animate sequentially</summary>
    GroupedSequential = 2,
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
            DistributionMode.GroupedSequential => "Grouped Sequential",
            _ => "Unknown",
        };
    }
}
