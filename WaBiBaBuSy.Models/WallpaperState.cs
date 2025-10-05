namespace WaBiBaBuSy.Models;

/// <summary>
/// Represents the current state of a wallpaper renderer.
/// </summary>
public enum WallpaperState
{
    /// <summary>
    /// Renderer is not initialized.
    /// </summary>
    Uninitialized,

    /// <summary>
    /// Renderer is initialized but not playing.
    /// </summary>
    Stopped,

    /// <summary>
    /// Renderer is currently playing.
    /// </summary>
    Playing,

    /// <summary>
    /// Renderer is paused.
    /// </summary>
    Paused,

    /// <summary>
    /// Renderer is buffering content.
    /// </summary>
    Buffering,

    /// <summary>
    /// Renderer encountered an error.
    /// </summary>
    Error
}
