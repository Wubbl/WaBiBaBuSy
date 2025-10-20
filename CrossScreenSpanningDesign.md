# Cross-Screen Spanning Animation System - Design Document

**Created:** 2025-10-20
**Status:** Design Phase
**Priority:** High (Most Important Feature)

## Overview

Enable animated wallpapers to seamlessly flow across multiple screens with different resolutions, creating a unified visual experience. The system will support:
- **Background layer**: Static color or stretched image
- **Animation layer**: Moving content that travels from first screen to last
- **Multi-resolution handling**: Automatic adaptation to different screen sizes
- **Composition pipeline**: Merge layers before rendering to each screen

---

## Architecture

### High-Level Flow

```
┌─────────────────────────────────────────────────────────────┐
│              Composition Pipeline                            │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  1. Virtual Canvas Manager                                   │
│     - Calculates total canvas size from all screens          │
│     - Maps screen positions to virtual coordinates           │
│     - Accounts for physical distances between screens        │
│                                                               │
│  2. Background Layer Renderer                                │
│     - Option A: Solid color fill                             │
│     - Option B: Stretched/tiled background image             │
│     - Renders per-screen background                          │
│                                                               │
│  3. Animation Layer Renderer                                 │
│     - Loads animation content (video/GIF)                    │
│     - Calculates current position based on time              │
│     - Renders visible portion per screen                     │
│     - Handles resolution scaling                             │
│                                                               │
│  4. Compositor                                               │
│     - Merges background + animation layers                   │
│     - Outputs final frame buffer per screen                  │
│                                                               │
│  5. Screen Renderer                                          │
│     - Sends composed frames to each WallpaperRenderer        │
│     - Maintains sync timing across all screens               │
│                                                               │
└─────────────────────────────────────────────────────────────┘
```

### Virtual Canvas Concept

The **Virtual Canvas** is a unified coordinate space spanning all connected screens:

```
Screen 1: 1920x1080    Screen 2: 2560x1440    Screen 3: 1920x1080
┌────────────────┐    ┌────────────────────┐    ┌────────────────┐
│                │    │                    │    │                │
│   (0,0)        │    │   (1920,0)         │    │   (4480,0)     │
│                │    │                    │    │                │
│                │    │                    │    │                │
│         1920px │    │            2560px  │    │         1920px │
└────────────────┘    └────────────────────┘    └────────────────┘
     1080px high             1440px high              1080px high

Virtual Canvas: Total width = 1920 + 2560 + 1920 = 6400px
                Height = max(1080, 1440, 1080) = 1440px
```

### Animation Movement

Animation content moves across the virtual canvas from left to right (or configurable direction):

```
Time T0: Animation at X=0 (visible on Screen 1)
Time T1: Animation at X=1920 (visible on Screen 1 & 2)
Time T2: Animation at X=3000 (visible on Screen 2)
Time T3: Animation at X=4480 (visible on Screen 2 & 3)
```

---

## Component Design

### 1. VirtualCanvasManager

**Purpose:** Calculate virtual canvas dimensions and screen mappings

**Class:** `WaBiBaBuSy.WallpaperEngine.Composition.VirtualCanvasManager`

```csharp
public class VirtualCanvasManager
{
    public Rectangle VirtualBounds { get; private set; }
    public List<ScreenMapping> ScreenMappings { get; private set; }

    public void CalculateLayout(IEnumerable<ClientNodeViewModel> clients)
    {
        // Sort clients by Order
        // Calculate cumulative X positions
        // Determine max height
        // Create ScreenMapping for each client
    }

    public ScreenMapping? GetScreenAtVirtualPosition(int x, int y)
    {
        // Returns which screen contains the given virtual coordinate
    }
}

public class ScreenMapping
{
    public string ClientId { get; set; }
    public Rectangle ScreenBounds { get; set; }  // Physical screen resolution
    public Rectangle VirtualBounds { get; set; } // Position in virtual canvas
    public int Order { get; set; }
    public int PhysicalDistanceCm { get; set; }
}
```

### 2. BackgroundLayerRenderer

**Purpose:** Render background for each screen

**Class:** `WaBiBaBuSy.WallpaperEngine.Composition.BackgroundLayerRenderer`

```csharp
public enum BackgroundMode
{
    SolidColor,
    StretchedImage,
    TiledImage
}

public class BackgroundLayerRenderer : IDisposable
{
    private BackgroundMode _mode;
    private Color _solidColor;
    private Image? _backgroundImage;

    public void Initialize(BackgroundConfig config)
    {
        // Load background image if needed
    }

    public Bitmap RenderForScreen(ScreenMapping screen)
    {
        // Create bitmap matching screen resolution
        // Fill with solid color OR draw portion of background image
        return bitmap;
    }
}
```

### 3. AnimationLayerRenderer

**Purpose:** Render animated content at current position

**Class:** `WaBiBaBuSy.WallpaperEngine.Composition.AnimationLayerRenderer`

```csharp
public class AnimationLayerRenderer : IDisposable
{
    private IWallpaperRenderer? _sourceRenderer; // Video or GIF renderer
    private int _animationWidth;
    private int _animationHeight;
    private int _currentVirtualX; // Current X position in virtual canvas

    public void Initialize(string animationPath, int targetHeight)
    {
        // Load animation source
        // Calculate scaled dimensions (maintain aspect ratio, fit to target height)
    }

    public void UpdatePosition(long timestampMs, int pixelsPerSecond)
    {
        // Calculate current X position based on time
        _currentVirtualX = (int)((timestampMs / 1000.0) * pixelsPerSecond);
    }

    public Bitmap? RenderForScreen(ScreenMapping screen)
    {
        // Check if animation overlaps with this screen's virtual bounds
        // If yes, render the visible portion
        // Return null if not visible on this screen

        var screenVirtualLeft = screen.VirtualBounds.X;
        var screenVirtualRight = screenVirtualLeft + screen.VirtualBounds.Width;
        var animationLeft = _currentVirtualX;
        var animationRight = animationLeft + _animationWidth;

        if (animationRight < screenVirtualLeft || animationLeft > screenVirtualRight)
            return null; // Not visible on this screen

        // Calculate which portion of animation to render
        // Scale to screen's physical resolution
        // Return cropped/scaled bitmap
    }
}
```

### 4. CompositionRenderer

**Purpose:** Merge background + animation layers

**Class:** `WaBiBaBuSy.WallpaperEngine.Composition.CompositionRenderer`

```csharp
public class CompositionRenderer : IDisposable
{
    private BackgroundLayerRenderer _backgroundRenderer;
    private AnimationLayerRenderer _animationRenderer;

    public Bitmap ComposeForScreen(ScreenMapping screen)
    {
        // Render background layer
        var backgroundBitmap = _backgroundRenderer.RenderForScreen(screen);

        // Render animation layer (may be null if not visible)
        var animationBitmap = _animationRenderer.RenderForScreen(screen);

        if (animationBitmap == null)
            return backgroundBitmap;

        // Composite animation on top of background
        using (var graphics = Graphics.FromImage(backgroundBitmap))
        {
            graphics.DrawImage(animationBitmap, ...); // Blend animation
        }

        return backgroundBitmap;
    }
}
```

### 5. CrossScreenWallpaperCoordinator

**Purpose:** Orchestrate the entire cross-screen system

**Class:** `WaBiBaBuSy.Core.Services.CrossScreenWallpaperCoordinator`

```csharp
public class CrossScreenWallpaperCoordinator : IDisposable
{
    private VirtualCanvasManager _canvasManager;
    private CompositionRenderer _compositor;
    private Dictionary<string, IWallpaperRenderer> _screenRenderers;
    private System.Threading.Timer _renderTimer;

    public async Task InitializeAsync(
        IEnumerable<ClientNodeViewModel> clients,
        CrossScreenConfig config)
    {
        // Calculate virtual canvas layout
        _canvasManager.CalculateLayout(clients);

        // Initialize background layer
        await _compositor.InitializeBackgroundAsync(config.Background);

        // Initialize animation layer
        await _compositor.InitializeAnimationAsync(config.AnimationPath, config.AnimationHeight);

        // Create renderer for each screen
        foreach (var screen in _canvasManager.ScreenMappings)
        {
            var renderer = CreateRendererForScreen(screen);
            _screenRenderers[screen.ClientId] = renderer;
        }
    }

    public async Task StartAsync()
    {
        // Start render loop (30-60 FPS)
        _renderTimer = new Timer(OnRenderFrame, null, 0, 16); // ~60 FPS
    }

    private void OnRenderFrame(object? state)
    {
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Update animation position
        _compositor.UpdateAnimationPosition(timestampMs);

        // Render frame for each screen
        foreach (var screen in _canvasManager.ScreenMappings)
        {
            var composedFrame = _compositor.ComposeForScreen(screen);
            var renderer = _screenRenderers[screen.ClientId];

            // Send frame to renderer (need to add method to IWallpaperRenderer)
            renderer.RenderFrame(composedFrame);
        }
    }
}
```

---

## Configuration Model

### CrossScreenConfig

**Location:** `WaBiBaBuSy.Models/Wallpaper/CrossScreenConfig.cs`

```csharp
public class CrossScreenConfig
{
    public BackgroundLayerConfig Background { get; set; } = new();
    public AnimationLayerConfig Animation { get; set; } = new();
    public int AnimationSpeedPxPerSecond { get; set; } = 500; // Animation movement speed
}

public class BackgroundLayerConfig
{
    public BackgroundMode Mode { get; set; } = BackgroundMode.SolidColor;
    public string ColorHex { get; set; } = "#000000"; // Black
    public string? ImagePath { get; set; }
}

public class AnimationLayerConfig
{
    public string AnimationPath { get; set; } = string.Empty; // Video or GIF file
    public int TargetHeight { get; set; } = 720; // Animation height in pixels
    public bool Loop { get; set; } = true;
}
```

---

## Integration Points

### 1. UI Changes

**File:** `WaBiBaBuSy.UI/Views/MainWindow.axaml`

Add new controls:
- "Cross-Screen Mode" toggle
- Background configuration (color picker or image selector)
- Animation file selector
- Animation speed slider
- Animation height input

### 2. IWallpaperRenderer Extension

**File:** `WaBiBaBuSy.Core/Interfaces/IWallpaperRenderer.cs`

Add new method:
```csharp
public interface IWallpaperRenderer
{
    // Existing methods...

    /// <summary>
    /// Render a pre-composed frame bitmap (for cross-screen mode)
    /// </summary>
    Task RenderFrameAsync(Bitmap frame);
}
```

### 3. Sync Command Extension

**File:** `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto`

Add new command type:
```protobuf
enum CommandType {
    LOAD = 0;
    PLAY = 1;
    PAUSE = 2;
    SEEK = 3;
    STOP = 4;
    CROSSSCREEN_START = 5;  // NEW: Start cross-screen mode
    CROSSSCREEN_STOP = 6;   // NEW: Stop cross-screen mode
}

message CrossScreenData {
    bytes frame_data = 1;        // Compressed frame bitmap
    int64 timestamp_ms = 2;      // Sync timestamp
    string screen_id = 3;        // Target screen ID
}
```

---

## Implementation Phases

### Phase 1: Foundation (1-2 days)
- ✅ Design document (this file)
- [ ] Create `VirtualCanvasManager` class
- [ ] Unit tests for canvas layout calculation
- [ ] Create `ScreenMapping` model

### Phase 2: Background Layer (1 day)
- [ ] Implement `BackgroundLayerRenderer`
- [ ] Support solid color backgrounds
- [ ] Support stretched image backgrounds
- [ ] UI controls for background configuration

### Phase 3: Animation Layer (2-3 days)
- [ ] Implement `AnimationLayerRenderer`
- [ ] Load video/GIF content
- [ ] Calculate animation position over time
- [ ] Render visible portions per screen
- [ ] Handle resolution scaling

### Phase 4: Composition (1 day)
- [ ] Implement `CompositionRenderer`
- [ ] Merge background + animation layers
- [ ] Optimize bitmap operations
- [ ] Memory management (frame buffer pooling)

### Phase 5: Coordination & Sync (2-3 days)
- [ ] Implement `CrossScreenWallpaperCoordinator`
- [ ] Render loop with target FPS
- [ ] Distribute frames to screen renderers
- [ ] Network synchronization (timestamp-based)
- [ ] Physical distance compensation

### Phase 6: UI Integration (1-2 days)
- [ ] Add cross-screen mode toggle to MainWindow
- [ ] Background configuration UI
- [ ] Animation file picker
- [ ] Speed and size controls
- [ ] Preview/test mode

### Phase 7: Testing & Polish (2-3 days)
- [ ] Multi-machine testing (3+ screens)
- [ ] Performance profiling (GPU/CPU/Network)
- [ ] Memory leak detection
- [ ] Edge case handling (disconnections, screen order changes)
- [ ] Documentation and user guide

**Total Estimated Time:** 10-16 days

---

## Performance Considerations

### Rendering Performance
- **Target:** 30-60 FPS across all screens
- **Bitmap caching:** Reuse frame buffers, avoid GC pressure
- **GPU acceleration:** Use hardware-accelerated bitmap operations
- **Network bandwidth:** Compress frames (JPEG quality 85-95%)

### Memory Management
- **Frame buffer pool:** Pre-allocate bitmaps to avoid allocations
- **Streaming:** Don't load entire animation into memory
- **Disposal:** Proper cleanup of Graphics/Bitmap objects

### Network Optimization
- **Delta encoding:** Only send changed regions (future optimization)
- **Compression:** JPEG or WebP compression for frame data
- **Batching:** Send multiple frames ahead (buffer 2-3 frames)

---

## Alternative Approaches Considered

### Approach A: Each Client Renders Independently (REJECTED)
- Each client loads full animation
- Server sends timing + position commands
- **Issue:** Doesn't handle multi-resolution well, animation scaling differs per client

### Approach B: Server-Side Composition (CURRENT DESIGN)
- Server composes frames for each screen
- Clients receive pre-rendered frame bitmaps
- **Pro:** Perfect synchronization, consistent rendering
- **Con:** Higher network bandwidth, server CPU load

### Approach C: Hybrid - Client-Side Composition (FUTURE)
- Server sends layout + timing data
- Clients compose locally using shared animation file
- **Pro:** Lower network load
- **Con:** More complex sync, file distribution needed

---

## Success Criteria

1. ✅ Animation visibly flows from first screen to last
2. ✅ Sync accuracy: ±50ms timing across all screens
3. ✅ Support 3+ screens with different resolutions
4. ✅ Background layer properly fills all screens
5. ✅ Animation scaling maintains aspect ratio
6. ✅ Smooth playback: 30+ FPS on all screens
7. ✅ CPU usage: <20% on server, <10% on clients
8. ✅ Network bandwidth: <10 Mbps per client

---

## Open Questions

1. **Frame distribution method:**
   - Option A: gRPC streaming (current infrastructure)
   - Option B: Separate UDP stream (lower latency)
   - **Decision:** Start with gRPC, measure performance

2. **Animation loop behavior:**
   - Seamless loop or reset to start?
   - **Decision:** Configurable via UI

3. **Screen ordering:**
   - Left-to-right only, or support arbitrary arrangements?
   - **Decision:** Start with linear left-to-right, extend later

4. **Error handling:**
   - What happens if one client drops frames?
   - **Decision:** Continue for other clients, log warning

---

## References

- Lively Wallpaper multi-monitor support: https://github.com/rocksdanister/lively
- Video composition techniques: FFmpeg compositing filters
- Network streaming: WebRTC frame delivery approaches

---

**Next Step:** Begin Phase 1 implementation - Create VirtualCanvasManager
