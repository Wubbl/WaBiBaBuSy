using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Animation;
using System.Collections.Concurrent;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// Server-side service for distributing animations to clients and managing animation state.
/// Handles sequential scheduling and completion tracking.
/// </summary>
public class AnimationDistributor
{
    private readonly ILogger<AnimationDistributor> _logger;

    /// <summary>
    /// Track animation state per client
    /// </summary>
    private readonly ConcurrentDictionary<string, ClientAnimationState> _clientAnimations = new();

    /// <summary>
    /// Track which clients are currently animating
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _animatingClients = new();

    public AnimationDistributor(ILogger<AnimationDistributor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Track that a client has started animating
    /// </summary>
    public void TrackAnimationStart(string clientId, string animationId, long startTimestampUtc, long durationMs)
    {
        var state = new ClientAnimationState
        {
            ClientId = clientId,
            AnimationId = animationId,
            StartTimestampUtc = startTimestampUtc,
            DurationMs = durationMs,
            State = AnimationState.Rendering,
            StartedAt = DateTimeOffset.UtcNow,
        };

        _clientAnimations[clientId] = state;
        _animatingClients[animationId] = clientId;

        _logger.LogInformation(
            "Tracking animation start: Client={ClientId}, Animation={AnimationId}, Duration={DurationMs}ms",
            clientId, animationId, durationMs);
    }

    /// <summary>
    /// Handle animation completion report from client
    /// </summary>
    public void HandleAnimationComplete(string clientId, string animationId, bool successful, string? errorMessage = null)
    {
        if (_clientAnimations.TryGetValue(clientId, out var state))
        {
            state.State = successful ? AnimationState.Completed : AnimationState.Failed;
            state.CompletedAt = DateTimeOffset.UtcNow;
            state.ErrorMessage = errorMessage ?? string.Empty;

            _logger.LogInformation(
                "Animation completed: Client={ClientId}, Animation={AnimationId}, Successful={Successful}",
                clientId, animationId, successful);

            if (successful)
            {
                _animatingClients.TryRemove(animationId, out _);
            }
        }
    }

    /// <summary>
    /// Check if a client is currently animating
    /// </summary>
    public bool IsClientAnimating(string clientId)
    {
        if (_clientAnimations.TryGetValue(clientId, out var state))
        {
            return state.State == AnimationState.Rendering || state.State == AnimationState.Waiting;
        }
        return false;
    }

    /// <summary>
    /// Get current animation state for a client
    /// </summary>
    public ClientAnimationState? GetAnimationState(string clientId)
    {
        _clientAnimations.TryGetValue(clientId, out var state);
        return state;
    }

    /// <summary>
    /// Get all currently animating clients
    /// </summary>
    public IEnumerable<ClientAnimationState> GetAnimatingClients()
    {
        return _clientAnimations.Values.Where(s => s.State == AnimationState.Rendering || s.State == AnimationState.Waiting);
    }

    /// <summary>
    /// Get expected animation position at current time
    /// </summary>
    public int GetExpectedAnimationPosition(string clientId, long currentTimestampUtc)
    {
        if (!_clientAnimations.TryGetValue(clientId, out var state))
        {
            return 0;
        }

        var elapsedMs = currentTimestampUtc - state.StartTimestampUtc;
        return (int)Math.Min(elapsedMs, state.DurationMs);
    }

    /// <summary>
    /// Check if animation has completed based on timestamp
    /// </summary>
    public bool HasAnimationCompleted(string clientId, long currentTimestampUtc)
    {
        if (!_clientAnimations.TryGetValue(clientId, out var state))
        {
            return false;
        }

        var completionTime = state.StartTimestampUtc + state.DurationMs;
        return currentTimestampUtc >= completionTime;
    }

    /// <summary>
    /// Get time until animation completes
    /// </summary>
    public long GetTimeUntilCompletion(string clientId, long currentTimestampUtc)
    {
        if (!_clientAnimations.TryGetValue(clientId, out var state))
        {
            return 0;
        }

        var completionTime = state.StartTimestampUtc + state.DurationMs;
        var timeUntil = completionTime - currentTimestampUtc;
        return Math.Max(0, timeUntil);
    }

    /// <summary>
    /// Clear animation state for a client
    /// </summary>
    public void ClearAnimationState(string clientId)
    {
        if (_clientAnimations.TryRemove(clientId, out var state))
        {
            _animatingClients.TryRemove(state.AnimationId, out _);
            _logger.LogInformation("Cleared animation state: Client={ClientId}", clientId);
        }
    }

    /// <summary>
    /// Get all animation states (for diagnostics)
    /// </summary>
    public IEnumerable<ClientAnimationState> GetAllAnimationStates()
    {
        return _clientAnimations.Values.ToList();
    }
}

/// <summary>
/// Animation state enum
/// </summary>
public enum AnimationState
{
    /// <summary>Waiting for start time</summary>
    Waiting = 0,

    /// <summary>Currently rendering</summary>
    Rendering = 1,

    /// <summary>Completed successfully</summary>
    Completed = 2,

    /// <summary>Failed with error</summary>
    Failed = 3,

    /// <summary>Stopped by server</summary>
    Stopped = 4,
}

/// <summary>
/// Client animation state tracking
/// </summary>
public class ClientAnimationState
{
    /// <summary>Client ID</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Animation ID</summary>
    public string AnimationId { get; set; } = string.Empty;

    /// <summary>When animation should start (UTC ms)</summary>
    public long StartTimestampUtc { get; set; }

    /// <summary>Animation duration (ms)</summary>
    public long DurationMs { get; set; }

    /// <summary>Current animation state</summary>
    public AnimationState State { get; set; } = AnimationState.Waiting;

    /// <summary>When this animation started on client</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When this animation completed</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Error message if failed</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Get elapsed time since animation started</summary>
    public long GetElapsedMs()
    {
        return (long)(DateTimeOffset.UtcNow - StartedAt).TotalMilliseconds;
    }

    /// <summary>Get expected completion timestamp</summary>
    public long GetCompletionTimestampUtc()
    {
        return StartTimestampUtc + DurationMs;
    }

    /// <summary>Check if animation is currently playing</summary>
    public bool IsAnimating()
    {
        return State == AnimationState.Rendering || State == AnimationState.Waiting;
    }
}
