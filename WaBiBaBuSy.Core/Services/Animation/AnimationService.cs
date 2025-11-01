using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Models.Animation;

namespace WaBiBaBuSy.Core.Services.Animation;

/// <summary>
/// High-level animation service that coordinates all animation operations.
/// Integrates AnimationDistributor, ClientAnimationRenderer, and AnimationFileDownloader.
/// This is the primary interface for animation functionality.
/// </summary>
public class AnimationService
{
    private readonly ILogger<AnimationService> _logger;
    private readonly AnimationDistributor _distributor;
    private readonly ClientAnimationRenderer _renderer;
    private readonly AnimationFileDownloader _downloader;
    private readonly AnimationOrchestrator _orchestrator;

    /// <summary>
    /// Callback when animation should render
    /// </summary>
    public event ClientAnimationRenderer.AnimationRenderDelegate? OnAnimationRender
    {
        add { _renderer.OnAnimationRender += value; }
        remove { _renderer.OnAnimationRender -= value; }
    }

    /// <summary>
    /// Callback when animation completes
    /// </summary>
    public event ClientAnimationRenderer.AnimationCompleteDelegate? OnAnimationComplete
    {
        add { _renderer.OnAnimationComplete += value; }
        remove { _renderer.OnAnimationComplete -= value; }
    }

    /// <summary>
    /// Callback when drift is detected
    /// </summary>
    public event ClientAnimationRenderer.AnimationDriftDelegate? OnDriftDetected
    {
        add { _renderer.OnDriftDetected += value; }
        remove { _renderer.OnDriftDetected -= value; }
    }

    /// <summary>
    /// Callback to send animation to server
    /// </summary>
    public event AnimationOrchestrator.SendAnimationDelegate? OnSendAnimation
    {
        add { _orchestrator.OnSendAnimation += value; }
        remove { _orchestrator.OnSendAnimation -= value; }
    }

    /// <summary>
    /// Callback when schedule completes
    /// </summary>
    public event AnimationOrchestrator.ScheduleCompleteDelegate? OnScheduleComplete
    {
        add { _orchestrator.OnScheduleComplete += value; }
        remove { _orchestrator.OnScheduleComplete -= value; }
    }

    public AnimationService(
        ILogger<AnimationService> logger,
        AnimationDistributor distributor,
        ClientAnimationRenderer renderer,
        AnimationFileDownloader downloader,
        AnimationOrchestrator orchestrator)
    {
        _logger = logger;
        _distributor = distributor;
        _renderer = renderer;
        _downloader = downloader;
        _orchestrator = orchestrator;
    }

    /// <summary>
    /// CLIENT: Receive animation metadata and start rendering
    /// </summary>
    public async Task ReceiveAnimationAsync(AnimationMetadata metadata)
    {
        _logger.LogInformation("Receiving animation: ID={AnimationId}", metadata.AnimationId);

        try
        {
            // Pass to renderer which handles lifecycle
            await _renderer.OnReceiveAnimationStart(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving animation: {AnimationId}", metadata.AnimationId);
            throw;
        }
    }

    /// <summary>
    /// CLIENT: Receive timing synchronization message
    /// </summary>
    public async Task ReceiveTimingSyncAsync(AnimationTimingSync sync)
    {
        try
        {
            await _renderer.OnReceiveTimingSync(sync);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving timing sync: {AnimationId}", sync.AnimationId);
            throw;
        }
    }

    /// <summary>
    /// CLIENT: Stop animation
    /// </summary>
    public async Task StopAnimationAsync(string animationId)
    {
        _logger.LogInformation("Stopping animation: {AnimationId}", animationId);

        try
        {
            await _renderer.OnStopAnimation(animationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping animation: {AnimationId}", animationId);
            throw;
        }
    }

    /// <summary>
    /// CLIENT: Download animation file with caching
    /// </summary>
    public async Task<string> DownloadAnimationFileAsync(string serverFilePath, string fileHash, long fileSize)
    {
        _logger.LogInformation(
            "Downloading animation file: Path={FilePath}, Hash={Hash}",
            serverFilePath, fileHash);

        try
        {
            var localPath = await _downloader.DownloadAnimationFileAsync(serverFilePath, fileHash, fileSize);
            _logger.LogInformation("Animation file downloaded: {LocalPath}", localPath);
            return localPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading animation file: {FilePath}", serverFilePath);
            throw;
        }
    }

    /// <summary>
    /// SERVER: Track animation start
    /// </summary>
    public void TrackAnimationStart(string clientId, string animationId, long startTimestampUtc, long durationMs)
    {
        _distributor.TrackAnimationStart(clientId, animationId, startTimestampUtc, durationMs);
    }

    /// <summary>
    /// SERVER: Handle animation completion
    /// </summary>
    public void HandleAnimationComplete(string clientId, string animationId, bool successful, string? errorMessage = null)
    {
        _distributor.HandleAnimationComplete(clientId, animationId, successful, errorMessage);
    }

    /// <summary>
    /// SERVER: Start sequential animation across clients
    /// </summary>
    public async Task<string> StartSequentialAnimationAsync(
        AnimationMetadata baseMetadata,
        List<string> clientIds,
        bool loop = false)
    {
        _logger.LogInformation(
            "Starting sequential animation: Clients={ClientCount}, Duration={Duration}ms, Loop={Loop}",
            clientIds.Count, baseMetadata.DurationMs, loop);

        try
        {
            var scheduleId = await _orchestrator.StartSequentialAnimationAsync(baseMetadata, clientIds, loop);
            _logger.LogInformation("Sequential animation schedule created: {ScheduleId}", scheduleId);
            return scheduleId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting sequential animation");
            throw;
        }
    }

    /// <summary>
    /// SERVER: Start simultaneous animation on all clients
    /// </summary>
    public async Task<string> StartSimultaneousAnimationAsync(
        AnimationMetadata baseMetadata,
        List<string> clientIds)
    {
        _logger.LogInformation(
            "Starting simultaneous animation: Clients={ClientCount}, Duration={Duration}ms",
            clientIds.Count, baseMetadata.DurationMs);

        try
        {
            var scheduleId = await _orchestrator.StartSimultaneousAnimationAsync(baseMetadata, clientIds);
            _logger.LogInformation("Simultaneous animation schedule created: {ScheduleId}", scheduleId);
            return scheduleId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting simultaneous animation");
            throw;
        }
    }

    /// <summary>
    /// SERVER: Stop animation schedule
    /// </summary>
    public void StopSchedule(string scheduleId)
    {
        _orchestrator.StopSchedule(scheduleId);
    }

    /// <summary>
    /// Get animation state (server-side)
    /// </summary>
    public ClientAnimationState? GetServerAnimationState(string clientId)
    {
        return _distributor.GetAnimationState(clientId);
    }

    /// <summary>
    /// Get animation state (client-side)
    /// </summary>
    public LocalAnimationState? GetClientAnimationState(string animationId)
    {
        return _renderer.GetAnimationState(animationId);
    }

    /// <summary>
    /// Check if server has animation running on client
    /// </summary>
    public bool IsClientAnimating(string clientId)
    {
        return _distributor.IsClientAnimating(clientId);
    }

    /// <summary>
    /// Check if client is currently rendering
    /// </summary>
    public bool IsClientRendering(string animationId)
    {
        return _renderer.IsAnimating(animationId);
    }

    /// <summary>
    /// Get all currently animating clients (server-side)
    /// </summary>
    public IEnumerable<ClientAnimationState> GetAnimatingClients()
    {
        return _distributor.GetAnimatingClients();
    }

    /// <summary>
    /// Get all local animations (client-side)
    /// </summary>
    public IEnumerable<LocalAnimationState> GetLocalAnimations()
    {
        return _renderer.GetAllAnimations();
    }

    /// <summary>
    /// Get animation schedule (server-side)
    /// </summary>
    public AnimationSchedule? GetSchedule(string scheduleId)
    {
        return _orchestrator.GetSchedule(scheduleId);
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    public (int fileCount, long totalSize) GetCacheStats()
    {
        return _downloader.GetCacheStats();
    }

    /// <summary>
    /// Get cache directory path
    /// </summary>
    public string GetCachePath()
    {
        return _downloader.GetCachePath();
    }

    /// <summary>
    /// Clear animation cache
    /// </summary>
    public void ClearCache()
    {
        _downloader.ClearCache();
    }

    /// <summary>
    /// Check if file is cached
    /// </summary>
    public bool IsCached(string serverFilePath, string fileHash)
    {
        return _downloader.IsCached(serverFilePath, fileHash);
    }
}
