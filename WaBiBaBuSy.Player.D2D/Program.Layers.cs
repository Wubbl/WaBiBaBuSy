using System;
using System.Collections.Generic;
using Vortice.Direct2D1;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Player.D2D;

// Scene layers (roadmap 2.3). The single-sprite code (load, layout, movement, pattern, effects, draw) keeps working on
// the per-sprite statics; each layer's copy of those statics lives in a LayerRuntime and is swapped in before that
// layer is updated or drawn. Layer 0 is the resting state: everything outside the layer loop (start command, probes,
// debug overlay, IconZone path, video, logs) sees layer 0 exactly as it saw the one sprite before layers existed.
partial class Program
{
    /// <summary>
    /// One layer's copy of the per-sprite statics. Field names mirror the statics they hold.
    /// </summary>
    // ponytail: a new per-sprite static must be added here and to SaveInto / LoadFrom, or upper layers share it with layer 0.
    // The upgrade path is turning the statics into LayerRuntime instance state (spec §3) once they stop changing.
    private sealed class LayerRuntime
    {
        public SceneLayer Config = new();
        public AnimationLayerConfig? AnimationConfig;
        public MovementConfig? MovementConfig;
        public ID2D1Bitmap[]? GifFrames;
        public List<int>? GifDelays;
        public long GifTotalDurationMs;
        public int ContentNativeWidth, ContentNativeHeight;
        public double GifSpeedMultiplier = 1.0;
        public List<ID2D1Bitmap[]> FramesPerSource = new();
        public List<List<int>> DelaysPerSource = new();
        public List<long> TotalDurationPerSource = new();
        public List<(int W, int H)> SourceDimensions = new();
        public int AnimWidth, AnimHeight;
        public float AnimX, AnimY, AnimVirtualX;
        public ContentFitMode FitMode;
        public bool CenterInitialPosition, RotateWithPath;
        public float AnimRotationRad, TileAlignStepX;
        public bool IsTravelingMode;
        public int EndlessCellOffsetI;
        public bool FacingLeft, HasPrevFacingX, LastFlipX;
        public float PrevFacingX;
        public int TraverseCount;
        public bool MaskZones;
        public NodeLayout Layout = new();
        public int Cw, Ch, VirtualCanvasWidth, VirtualCanvasHeight, MonitorOffsetX, CanvasOffsetY, NodeOrder;
        public bool PerMonitorMode;
        public float Opacity = 1f;
        public long PhaseOffsetMs;
        public bool FollowsBackground;

        public LayerRuntime ShallowCopy() => (LayerRuntime)MemberwiseClone();
    }

    private static readonly List<LayerRuntime> _layers = new();
    private static int _activeLayer;

    /// <summary>This layer's opacity, multiplied into every bitmap draw (DrawCellWithOptionalGrading).</summary>
    private static float _layerOpacity = 1f;
    /// <summary>This layer's clock offset: it updates and draws at elapsed + this.</summary>
    private static long _layerPhaseOffsetMs;
    /// <summary>Layer 0 only: follows the IconZone path, the corridor clamp and the zone mask.</summary>
    private static bool _layerFollowsBackground = true;

    /// <summary>The background mode this layer reacts to: the real one for layer 0, plain solid for every layer above it.</summary>
    private static BackgroundMode LayerBackgroundMode => _layerFollowsBackground ? _backgroundMode : BackgroundMode.SolidColor;

    /// <summary>Seat-map layout from LOAD (null = this monitor alone is the canvas).</summary>
    private static NodeLayout? _sequentialLayout;

    private static void SaveInto(LayerRuntime l)
    {
        l.AnimationConfig = _animationConfig; l.MovementConfig = _movementConfig;
        l.GifFrames = _d2dGifFrames; l.GifDelays = _d2dGifDelays; l.GifTotalDurationMs = _d2dGifTotalDurationMs;
        l.ContentNativeWidth = _contentNativeWidth; l.ContentNativeHeight = _contentNativeHeight; l.GifSpeedMultiplier = _gifSpeedMultiplier;
        l.FramesPerSource = _d2dFramesPerSource; l.DelaysPerSource = _d2dDelaysPerSource;
        l.TotalDurationPerSource = _d2dTotalDurationPerSource; l.SourceDimensions = _sourceDimensions;
        l.AnimWidth = _animWidth; l.AnimHeight = _animHeight; l.AnimX = _animX; l.AnimY = _animY; l.AnimVirtualX = _animVirtualX;
        l.FitMode = _fitMode; l.CenterInitialPosition = _centerInitialPosition; l.RotateWithPath = _rotateWithPath;
        l.AnimRotationRad = _animRotationRad; l.TileAlignStepX = _tileAlignStepX; l.IsTravelingMode = _isTravelingMode;
        l.EndlessCellOffsetI = _endlessCellOffsetI;
        l.FacingLeft = _facingLeft; l.HasPrevFacingX = _hasPrevFacingX; l.LastFlipX = _lastFlipX; l.PrevFacingX = _prevFacingX;
        l.TraverseCount = _traverseCount; l.MaskZones = _maskZones;
        l.Layout = _layout; l.Cw = _cw; l.Ch = _ch; l.VirtualCanvasWidth = _virtualCanvasWidth; l.VirtualCanvasHeight = _virtualCanvasHeight;
        l.MonitorOffsetX = _monitorOffsetX; l.CanvasOffsetY = _canvasOffsetY; l.NodeOrder = _nodeOrder; l.PerMonitorMode = _perMonitorMode;
        l.Opacity = _layerOpacity; l.PhaseOffsetMs = _layerPhaseOffsetMs; l.FollowsBackground = _layerFollowsBackground;
    }

    private static void LoadFrom(LayerRuntime l)
    {
        _animationConfig = l.AnimationConfig; _movementConfig = l.MovementConfig;
        _d2dGifFrames = l.GifFrames; _d2dGifDelays = l.GifDelays; _d2dGifTotalDurationMs = l.GifTotalDurationMs;
        _contentNativeWidth = l.ContentNativeWidth; _contentNativeHeight = l.ContentNativeHeight; _gifSpeedMultiplier = l.GifSpeedMultiplier;
        _d2dFramesPerSource = l.FramesPerSource; _d2dDelaysPerSource = l.DelaysPerSource;
        _d2dTotalDurationPerSource = l.TotalDurationPerSource; _sourceDimensions = l.SourceDimensions;
        _animWidth = l.AnimWidth; _animHeight = l.AnimHeight; _animX = l.AnimX; _animY = l.AnimY; _animVirtualX = l.AnimVirtualX;
        _fitMode = l.FitMode; _centerInitialPosition = l.CenterInitialPosition; _rotateWithPath = l.RotateWithPath;
        _animRotationRad = l.AnimRotationRad; _tileAlignStepX = l.TileAlignStepX; _isTravelingMode = l.IsTravelingMode;
        _endlessCellOffsetI = l.EndlessCellOffsetI;
        _facingLeft = l.FacingLeft; _hasPrevFacingX = l.HasPrevFacingX; _lastFlipX = l.LastFlipX; _prevFacingX = l.PrevFacingX;
        _traverseCount = l.TraverseCount; _maskZones = l.MaskZones;
        _layout = l.Layout; _cw = l.Cw; _ch = l.Ch; _virtualCanvasWidth = l.VirtualCanvasWidth; _virtualCanvasHeight = l.VirtualCanvasHeight;
        _monitorOffsetX = l.MonitorOffsetX; _canvasOffsetY = l.CanvasOffsetY; _nodeOrder = l.NodeOrder; _perMonitorMode = l.PerMonitorMode;
        _layerOpacity = l.Opacity; _layerPhaseOffsetMs = l.PhaseOffsetMs; _layerFollowsBackground = l.FollowsBackground;
    }

    /// <summary>Swap layer <paramref name="index"/>'s state into the statics (the current one is saved first).</summary>
    private static void ActivateLayer(int index)
    {
        if (index == _activeLayer || index < 0 || index >= _layers.Count) return;
        if (_activeLayer < _layers.Count) SaveInto(_layers[_activeLayer]);
        LoadFrom(_layers[index]);
        _activeLayer = index;
    }

    /// <summary>
    /// The canvas a layer plays on. Sequential: the seat-map layout from LOAD. Simultaneous (or no layout): this monitor
    /// alone — offset 0, its own size, not mirrored, no wrap, scale 1 — the same layout the room preview builds.
    /// </summary>
    private static NodeLayout LayoutFor(AnimationDistributionMode distribution)
    {
        if (distribution == AnimationDistributionMode.Sequential && _sequentialLayout != null) return _sequentialLayout;
        return new NodeLayout
        {
            OffsetX = 0, OffsetY = 0, Width = _width, Height = _height,
            CanvasWidth = _width, CanvasHeight = _height,
            Mirrored = false, Wraps = false, Order = _sequentialLayout?.Order ?? 0, Scale = 1f
        };
    }

    /// <summary>Point the per-layer canvas statics at <paramref name="layout"/> (normalizing missing sizes).</summary>
    private static void ApplyLayout(NodeLayout layout, bool perMonitor)
    {
        _layout = layout;
        if (_layout.Width <= 0) _layout.Width = _width;
        if (_layout.Height <= 0) _layout.Height = _height;
        if (_layout.Scale <= 0f) _layout.Scale = 1f;
        _cw = _layout.Width;
        _ch = _layout.Height;
        _virtualCanvasWidth = _layout.CanvasWidth > 0 ? _layout.CanvasWidth : _width;
        _virtualCanvasHeight = _layout.CanvasHeight > 0 ? _layout.CanvasHeight : _height;
        _monitorOffsetX = _layout.OffsetX;
        _canvasOffsetY = _layout.OffsetY;
        _nodeOrder = _layout.Order;
        _perMonitorMode = perMonitor;
    }

    /// <summary>Fresh per-sprite statics for the next layer to load (the previous layer's are saved in its runtime).</summary>
    private static void ResetSpriteStatics(SceneLayer layer, bool isBottom)
    {
        _animationConfig = layer.Animation;
        _movementConfig = layer.Movement;
        _d2dGifFrames = null; _d2dGifDelays = null; _d2dGifTotalDurationMs = 0;
        _contentNativeWidth = 0; _contentNativeHeight = 0; _gifSpeedMultiplier = 1.0;
        _d2dFramesPerSource = new(); _d2dDelaysPerSource = new(); _d2dTotalDurationPerSource = new(); _sourceDimensions = new();
        _animWidth = 0; _animHeight = 0; _animX = 0; _animY = 0; _animVirtualX = 0;
        _animRotationRad = 0; _tileAlignStepX = 0; _isTravelingMode = false; _endlessCellOffsetI = 0;
        _facingLeft = false; _hasPrevFacingX = false; _lastFlipX = false; _prevFacingX = 0; _traverseCount = 0;
        _layerOpacity = Math.Clamp(layer.Opacity, 0f, 1f);
        _layerPhaseOffsetMs = layer.PhaseOffsetMs;
        _layerFollowsBackground = isBottom;
        bool perMonitor = layer.Distribution == AnimationDistributionMode.Simultaneous;
        ApplyLayout(LayoutFor(layer.Distribution), perMonitor);
    }

    /// <summary>
    /// Update and draw every enabled layer above layer 0 at the frame's raw elapsed (each with its own node phase,
    /// unless <paramref name="applyNodePhase"/> is false for exact frames), then swap layer 0 back in.
    /// </summary>
    private static void DrawUpperLayers(long rawElapsedMs, bool applyNodePhase)
    {
        if (_layers.Count <= 1) return;
        try
        {
            for (int i = 1; i < _layers.Count; i++)
            {
                if (!_layers[i].Config.Enabled) continue;
                ActivateLayer(i);
                if (_d2dFramesPerSource.Count == 0) continue;   // nothing loaded (missing file, unsupported format)
                // Same clock as the room preview (SyncTiming.LayerElapsed); exact frames skip the node phase.
                long? clock = applyNodePhase
                    ? SyncTiming.LayerElapsed(rawElapsedMs, _nodeOrder, _layers[i].Config)
                    : SyncTiming.ShouldDrawAnimation(rawElapsedMs) ? rawElapsedMs + _layerPhaseOffsetMs : null;
                if (clock is not long t) continue;
                UpdateAnimationPosition(t);
                DrawAnimationLayer(t);
            }
        }
        finally
        {
            ActivateLayer(0);
        }
    }

    /// <summary>Copies of the upper layers' state, for an exact frame that must leave the live animation untouched.</summary>
    private static List<LayerRuntime> SnapshotUpperLayers()
    {
        var copies = new List<LayerRuntime>(_layers.Count);
        for (int i = 1; i < _layers.Count; i++) copies.Add(_layers[i].ShallowCopy());
        return copies;
    }

    private static void RestoreUpperLayers(List<LayerRuntime> copies)
    {
        for (int i = 1; i < _layers.Count && i - 1 < copies.Count; i++) _layers[i] = copies[i - 1];
    }

    /// <summary>Dispose every upper layer's GPU frames and forget all layers. Layer 0 must be active.</summary>
    private static void DisposeUpperLayers()
    {
        for (int i = 1; i < _layers.Count; i++)
        {
            var l = _layers[i];
            if (l.GifFrames != null)
                foreach (var f in l.GifFrames) f?.Dispose();
            for (int s = 1; s < l.FramesPerSource.Count; s++)   // index 0 shares storage with GifFrames
                foreach (var f in l.FramesPerSource[s]) f?.Dispose();
        }
        _layers.Clear();
        _activeLayer = 0;
    }
}
