using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Client.Linux.Rendering;

namespace WaBiBaBuSy.Client.Linux.Platform;

/// <summary>
/// Borderless window covering one monitor, placed behind all apps via <see cref="X11Desktop"/>.
/// Draws its <see cref="WallpaperScene"/> straight onto Avalonia's Skia canvas every frame.
/// </summary>
public sealed class WallpaperWindow : Window
{
    public const string WindowTitle = "WaBiBaBuSy Wallpaper";

    private readonly WallpaperScene _scene;
    private readonly MonitorGeometry _monitor;
    private readonly X11Desktop _x11;
    private readonly WindowPlacement _placement;
    private readonly ILogger _logger;
    private readonly SceneSurface _surface;
    private bool _closed;
    private bool _frameLoopStarted;

    /// <summary>Stop redrawing (e.g. while a fullscreen game runs). Time keeps flowing, so resuming stays in sync.</summary>
    public bool Paused { get; set; }

    public WallpaperWindow(WallpaperScene scene, MonitorGeometry monitor, X11Desktop x11, WindowPlacement placement, ILogger logger)
    {
        _scene = scene;
        _monitor = monitor;
        _x11 = x11;
        _placement = placement;
        _logger = logger;

        Title = WindowTitle;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(monitor.X, monitor.Y);
        ApplySize();
        _surface = new SceneSurface(this);
        Content = _surface;

        // Set the EWMH properties before the window is mapped so the WM places it correctly from the start
        if (TryGetPlatformHandle() is { } handle)
            _x11.ApplyPreMapHints(handle.Handle, _placement);

        Opened += (_, _) =>
        {
            Position = new PixelPoint(_monitor.X, _monitor.Y);
            ApplySize();
            if (TryGetPlatformHandle() is { } h)
                _x11.ApplyPostMapHints(h.Handle, _placement);
            _logger.LogInformation("[Window] Shown on {Monitor} at ({X},{Y}) {W}x{H}px, scaling={Scale}, placement={Placement}",
                _monitor.Name, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, RenderScaling, _placement);
            // Opened can fire again after Hide/Show; keep a single frame loop
            if (!_frameLoopStarted)
            {
                _frameLoopStarted = true;
                RequestAnimationFrame(OnAnimationFrame);
            }
        };
        Closed += (_, _) => _closed = true;
        ScalingChanged += (_, _) => ApplySize();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // ── BiBaBo input: while this monitor holds the bomb the window takes clicks and keys (it is the desktop) ──

    private Action<float, float>? _onClick;   // device pixels, origin = monitor top-left
    private Action<Key>? _onKey;

    /// <summary>
    /// Take clicks and keys on the wallpaper (UI thread). A desktop window below every app only gets the clicks that hit
    /// the bare desktop, like the Windows Raw Input sink. <see cref="StopInput"/> makes it click-through again.
    /// </summary>
    public void WatchInput(Action<float, float> onClick, Action<Key> onKey)
    {
        _onClick = onClick;
        _onKey = onKey;
        Focusable = true;
        if (TryGetPlatformHandle() is { } h) _x11.SetClickThrough(h.Handle, clickThrough: false);
        _logger.LogInformation("[Window] {Monitor}: taking clicks and keys (BiBaBo)", _monitor.Name);
    }

    public void StopInput()
    {
        if (_onClick == null) return;
        _onClick = null;
        _onKey = null;
        Focusable = false;
        if (TryGetPlatformHandle() is { } h) _x11.SetClickThrough(h.Handle, clickThrough: true);
        _logger.LogInformation("[Window] {Monitor}: click-through again", _monitor.Name);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_onClick is not { } onClick || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Activate();   // keyboard focus for typed keypad digits
        Focus();
        var p = e.GetPosition(this);
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        onClick((float)(p.X * scale), (float)(p.Y * scale));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => _onKey?.Invoke(e.Key);

    /// <summary>
    /// Show again after a Stop. The WM drops _NET_WM_STATE when a window is withdrawn, so the
    /// hints are re-applied around the re-map.
    /// </summary>
    public void ShowWallpaper()
    {
        if (IsVisible) return;
        if (TryGetPlatformHandle() is { } handle) _x11.ApplyPreMapHints(handle.Handle, _placement);
        Show();
        if (TryGetPlatformHandle() is { } h)
        {
            _x11.ApplyPostMapHints(h.Handle, _placement);
            if (_onClick != null) _x11.SetClickThrough(h.Handle, clickThrough: false);   // a bomb game is still on
        }
    }

    /// <summary>Window size is in DIPs; the scene renders in physical pixels.</summary>
    private void ApplySize()
    {
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        Width = _monitor.Width / scale;
        Height = _monitor.Height / scale;
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        if (_closed) return;
        if (!Paused) _surface.InvalidateVisual();
        RequestAnimationFrame(OnAnimationFrame);
    }

    /// <summary>
    /// The drawing surface. (Overriding Window.Render does not reach the screen in Avalonia 12,
    /// so the scene is drawn by a plain control hosted as the window content.)
    /// </summary>
    private sealed class SceneSurface : Control
    {
        private readonly WallpaperWindow _owner;
        public SceneSurface(WallpaperWindow owner) => _owner = owner;

        public override void Render(DrawingContext context)
            => context.Custom(new SceneDrawOperation(new Rect(Bounds.Size), _owner._scene, _owner.RenderScaling));
    }

    private sealed class SceneDrawOperation : ICustomDrawOperation
    {
        private readonly WallpaperScene _scene;
        private readonly double _scaling;

        public SceneDrawOperation(Rect bounds, WallpaperScene scene, double scaling)
        {
            Bounds = bounds;
            _scene = scene;
            _scaling = scaling;
        }

        public Rect Bounds { get; }
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            canvas.Save();
            // Avalonia's canvas is in DIPs; the scene works in monitor pixels
            float inv = (float)(1.0 / (_scaling > 0 ? _scaling : 1.0));
            canvas.Scale(inv, inv);
            _scene.Render(canvas);
            canvas.Restore();
        }
    }
}
