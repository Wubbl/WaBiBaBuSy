using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Animation;
using System.Collections.Concurrent;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// Client-side service for receiving, composing, and rendering animations locally.
/// Handles animation lifecycle: download → wait → render → report completion.
/// </summary>
public class ClientAnimationRenderer
{
    private readonly ILogger<ClientAnimationRenderer> _logger;

    /// <summary>
    /// Track animation rendering state on client
    /// </summary>
    private readonly ConcurrentDictionary<string, LocalAnimationState> _localAnimations = new();

    /// <summary>
    /// Callback when animation should start rendering
    /// </summary>
    public delegate Task AnimationRenderDelegate(AnimationMetadata metadata);
    public event AnimationRenderDelegate? OnAnimationRender;

    /// <summary>
    /// Callback when animation completes
    /// </summary>
    public delegate Task AnimationCompleteDelegate(string animationId, bool successful, string? error);
    public event AnimationCompleteDelegate? OnAnimationComplete;

    /// <summary>
    /// Callback when animation is ready after preparation
    /// </summary>
    public delegate Task AnimationReadyDelegate(string animationId);
    public event AnimationReadyDelegate? OnAnimationReady;

    /// <summary>
    /// Callback when drift is detected and correction is needed
    /// </summary>
    public delegate Task AnimationDriftDelegate(string animationId, int driftMs);
    public event AnimationDriftDelegate? OnDriftDetected;

    public ClientAnimationRenderer(ILogger<ClientAnimationRenderer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Receive animation preparation request from server
    /// Initialize renderer but don't display yet
    /// </summary>
    public async Task OnReceiveAnimationPrepare(WaBiBaBuSy.Grpc.AnimationPrepare prepare)
    {
        var animationId = prepare.AnimationId;

        try
        {
            _logger.LogInformation("Preparing animation: ID={AnimationId}", animationId);

            var state = new LocalAnimationState
            {
                AnimationId = animationId,
                AnimationPrepare = prepare,
                State = AnimationRenderState.Preparing,
                ReceivedAt = DateTimeOffset.UtcNow,
            };

            _localAnimations[animationId] = state;

            // Initialize renderer (warm-up phase)
            // This may take 100-300ms depending on file and system
            state.RendererInitStartTime = DateTimeOffset.UtcNow;

            // Simulate or perform actual renderer initialization
            // In real implementation, would instantiate actual renderer here
            await Task.Delay(100); // Placeholder for actual renderer init

            state.RendererInitEndTime = DateTimeOffset.UtcNow;
            state.State = AnimationRenderState.Ready;

            _logger.LogInformation(
                "Animation prepared and ready: ID={AnimationId}, InitTime={InitMs}ms",
                animationId,
                (state.RendererInitEndTime!.Value - state.RendererInitStartTime!.Value).TotalMilliseconds);

            // Report to server that we're ready
            await OnAnimationReady?.Invoke(animationId)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error preparing animation: {AnimationId}", animationId);
            await ReportAnimationFailed(animationId, $"Prepare failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Receive animation metadata from server and start local rendering
    /// Should only be called after animation has been prepared
    /// </summary>
    public async Task OnReceiveAnimationStart(AnimationMetadata metadata)
    {
        if (!metadata.IsValid(out var error))
        {
            _logger.LogError("Invalid animation metadata: {Error}", error);
            await ReportAnimationFailed(metadata.AnimationId, error);
            return;
        }

        var state = new LocalAnimationState
        {
            AnimationId = metadata.AnimationId,
            AnimationMetadata = metadata,
            State = AnimationRenderState.Waiting,
            ReceivedAt = DateTimeOffset.UtcNow,
        };

        _localAnimations[metadata.AnimationId] = state;

        _logger.LogInformation(
            "Received animation: ID={AnimationId}, Duration={DurationMs}ms, StartTime={StartTime}ms from now",
            metadata.AnimationId,
            metadata.DurationMs,
            metadata.CalculateWaitTimeMs());

        // Start background task to handle this animation
        _ = ProcessAnimationAsync(metadata);
    }

    /// <summary>
    /// Process animation: wait for start time, then render
    /// </summary>
    private async Task ProcessAnimationAsync(AnimationMetadata metadata)
    {
        try
        {
            if (!_localAnimations.TryGetValue(metadata.AnimationId, out var state))
            {
                _logger.LogError("Animation state not found: {AnimationId}", metadata.AnimationId);
                return;
            }

            // 1. Wait for start time
            var waitTimeMs = metadata.CalculateWaitTimeMs();
            if (waitTimeMs > 0)
            {
                _logger.LogInformation("Animation waiting: {AnimationId}, {WaitMs}ms until start",
                    metadata.AnimationId, waitTimeMs);

                state.State = AnimationRenderState.Waiting;
                await Task.Delay((int)Math.Min(waitTimeMs, int.MaxValue));
            }

            // 2. Check if still valid
            if (state.State != AnimationRenderState.Waiting && state.State != AnimationRenderState.Ready)
            {
                _logger.LogWarning("Animation cancelled during wait: {AnimationId}", metadata.AnimationId);
                return;
            }

            // 3. Signal to render and record ACTUAL start time
            state.State = AnimationRenderState.Rendering;
            state.StartedRenderingAt = DateTimeOffset.UtcNow;  // ACTUAL rendering start
            _logger.LogInformation("Starting animation render: {AnimationId}, ActualStart={StartTime}, Duration={DurationMs}ms",
                metadata.AnimationId, state.StartedRenderingAt, metadata.DurationMs);

            // Invoke render callback (UI will handle actual frame composition/display)
            OnAnimationRender?.Invoke(metadata);

            // 4. Wait for animation to complete
            await Task.Delay((int)metadata.DurationMs);

            // 5. Report completion with actual timings
            state.State = AnimationRenderState.Completed;
            state.CompletedAt = DateTimeOffset.UtcNow;

            // Calculate actual duration based on when we actually started
            var actualDurationMs = (long)(state.CompletedAt - state.StartedRenderingAt).Value.TotalMilliseconds;

            _logger.LogInformation(
                "Animation completed: {AnimationId}, ActualDuration={ActualMs}ms (scheduled={ScheduledMs}ms)",
                metadata.AnimationId, actualDurationMs, metadata.DurationMs);

            // Pass actual timings to completion handler
            await OnAnimationComplete?.Invoke(metadata.AnimationId, true, null)!;

            // 6. Clean up
            _localAnimations.TryRemove(metadata.AnimationId, out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing animation: {AnimationId}", metadata.AnimationId);
            await ReportAnimationFailed(metadata.AnimationId, ex.Message);
        }
    }

    /// <summary>
    /// Handle timing synchronization from server to detect and correct drift
    /// </summary>
    public async Task OnReceiveTimingSync(AnimationTimingSync sync)
    {
        if (!_localAnimations.TryGetValue(sync.AnimationId, out var state))
        {
            _logger.LogDebug("Received timing sync for unknown animation: {AnimationId}", sync.AnimationId);
            return;
        }

        if (state.State != AnimationRenderState.Rendering)
        {
            return; // Not currently rendering, ignore sync
        }

        // Calculate our local elapsed time
        var localElapsedMs = (long)(DateTimeOffset.UtcNow - state.StartedRenderingAt!).Value.TotalMilliseconds;

        // Calculate drift
        var drift = sync.CalculateClockOffsetMs(sync.ExpectedPositionMs);

        _logger.LogDebug(
            "Timing sync: Animation={AnimationId}, Expected={Expected}ms, Local={Local}ms, Drift={Drift}ms",
            sync.AnimationId, sync.ExpectedPositionMs, localElapsedMs, drift);

        // Check if drift exceeds tolerance
        if (sync.IsDriftExceeded(localElapsedMs, toleranceMs: 50))
        {
            _logger.LogWarning(
                "Animation drift detected: {AnimationId}, Drift={Drift}ms, triggering correction",
                sync.AnimationId, drift);

            state.DriftCount++;
            OnDriftDetected?.Invoke(sync.AnimationId, drift);
        }

        // Track timing sync for diagnostics
        state.LastSyncAt = DateTimeOffset.UtcNow;
        state.LastDrift = drift;
    }

    /// <summary>
    /// Stop animation immediately
    /// </summary>
    public async Task OnStopAnimation(string animationId)
    {
        if (_localAnimations.TryGetValue(animationId, out var state))
        {
            state.State = AnimationRenderState.Stopped;
            _logger.LogInformation("Stopped animation: {AnimationId}", animationId);

            await OnAnimationComplete?.Invoke(animationId, false, "Animation stopped by server")!;
            _localAnimations.TryRemove(animationId, out _);
        }
    }

    /// <summary>
    /// Report animation failure
    /// </summary>
    private async Task ReportAnimationFailed(string animationId, string error)
    {
        if (_localAnimations.TryGetValue(animationId, out var state))
        {
            state.State = AnimationRenderState.Failed;
            state.ErrorMessage = error;
            _logger.LogError("Animation failed: {AnimationId}, Error={Error}", animationId, error);
        }

        await OnAnimationComplete?.Invoke(animationId, false, error)!;
        _localAnimations.TryRemove(animationId, out _);
    }

    /// <summary>
    /// Get current animation state
    /// </summary>
    public LocalAnimationState? GetAnimationState(string animationId)
    {
        _localAnimations.TryGetValue(animationId, out var state);
        return state;
    }

    /// <summary>
    /// Check if animation is currently rendering
    /// </summary>
    public bool IsAnimating(string animationId)
    {
        if (_localAnimations.TryGetValue(animationId, out var state))
        {
            return state.State == AnimationRenderState.Rendering;
        }
        return false;
    }

    /// <summary>
    /// Get all current animations
    /// </summary>
    public IEnumerable<LocalAnimationState> GetAllAnimations()
    {
        return _localAnimations.Values.ToList();
    }

    /// <summary>
    /// Clear all animation state
    /// </summary>
    public void ClearAllAnimations()
    {
        _localAnimations.Clear();
        _logger.LogInformation("Cleared all animation states");
    }
}

/// <summary>
/// Animation rendering state enum
/// </summary>
public enum AnimationRenderState
{
    /// <summary>Initializing renderer (warm-up phase)</summary>
    Preparing = 0,

    /// <summary>Renderer ready, waiting for start time</summary>
    Ready = 1,

    /// <summary>Waiting for start time</summary>
    Waiting = 2,

    /// <summary>Currently rendering</summary>
    Rendering = 3,

    /// <summary>Completed successfully</summary>
    Completed = 4,

    /// <summary>Failed with error</summary>
    Failed = 5,

    /// <summary>Stopped by server</summary>
    Stopped = 6,
}

/// <summary>
/// Local animation state on client
/// </summary>
public class LocalAnimationState
{
    /// <summary>Animation ID</summary>
    public string AnimationId { get; set; } = string.Empty;

    /// <summary>Animation prepare data (warm-up phase)</summary>
    public WaBiBaBuSy.Grpc.AnimationPrepare? AnimationPrepare { get; set; }

    /// <summary>Animation metadata</summary>
    public AnimationMetadata? AnimationMetadata { get; set; }

    /// <summary>Current rendering state</summary>
    public AnimationRenderState State { get; set; } = AnimationRenderState.Preparing;

    /// <summary>When animation metadata was received</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>When renderer initialization started</summary>
    public DateTimeOffset? RendererInitStartTime { get; set; }

    /// <summary>When renderer initialization completed</summary>
    public DateTimeOffset? RendererInitEndTime { get; set; }

    /// <summary>When animation started rendering</summary>
    public DateTimeOffset? StartedRenderingAt { get; set; }

    /// <summary>When animation completed</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Error message if failed</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Last timing sync received</summary>
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Last measured drift (ms)</summary>
    public int LastDrift { get; set; }

    /// <summary>Number of drift corrections</summary>
    public int DriftCount { get; set; }

    /// <summary>Check if animation is currently active</summary>
    public bool IsActive()
    {
        return State == AnimationRenderState.Waiting || State == AnimationRenderState.Rendering || State == AnimationRenderState.Ready;
    }

    /// <summary>Get elapsed time since actual rendering start</summary>
    public long GetElapsedMs()
    {
        if (StartedRenderingAt == null)
            return 0;

        return (long)(DateTimeOffset.UtcNow - StartedRenderingAt.Value).TotalMilliseconds;
    }

    /// <summary>Get renderer initialization time (ms)</summary>
    public long GetInitializationTimeMs()
    {
        if (RendererInitStartTime == null || RendererInitEndTime == null)
            return 0;

        return (long)(RendererInitEndTime.Value - RendererInitStartTime.Value).TotalMilliseconds;
    }
}
