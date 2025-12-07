using WaBiBaBuSy.Models;

namespace WaBiBaBuSy.Core.Interfaces;

/// <summary>
/// Event arguments for frame rendered events.
/// </summary>
public class FrameRenderedEventArgs : EventArgs
{
    /// <summary>
    /// Current playback position in milliseconds.
    /// </summary>
    public long PositionMs { get; set; }

    /// <summary>
    /// Frame timestamp.
    /// </summary>
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Interface for wallpaper renderers.
/// Implementations handle specific wallpaper types (Video, Image, GIF).
/// </summary>
public interface IWallpaperRenderer : IDisposable
{
    /// <summary>
    /// Gets the current state of the renderer.
    /// </summary>
    WallpaperState State { get; }

    /// <summary>
    /// Gets the current playback position in milliseconds.
    /// </summary>
    long PositionMs { get; }

    /// <summary>
    /// Event raised when a frame is rendered.
    /// </summary>
    event EventHandler<FrameRenderedEventArgs>? FrameRendered;

    /// <summary>
    /// Event raised when the state changes.
    /// </summary>
    event EventHandler<WallpaperState>? StateChanged;

    /// <summary>
    /// Initializes the renderer with the specified configuration.
    /// </summary>
    Task InitializeAsync(WallpaperConfig config);

    /// <summary>
    /// Starts playback.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Pauses playback.
    /// </summary>
    Task PauseAsync();

    /// <summary>
    /// Resumes playback after pause.
    /// </summary>
    Task ResumeAsync();

    /// <summary>
    /// Stops playback and releases resources.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Seeks to the specified position.
    /// </summary>
    Task SeekAsync(TimeSpan position);

    /// <summary>
    /// Gets the frame at a specific timestamp (used by composition system).
    /// For GIFs: returns the appropriate frame based on frame delays.
    /// For videos: returns frame at that timestamp (or cached if available).
    /// For images: returns the single frame.
    /// </summary>
    System.Drawing.Bitmap GetFrameAtPosition(long timestampMs);
}
