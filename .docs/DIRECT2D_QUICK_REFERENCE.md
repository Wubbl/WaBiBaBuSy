# Direct2D Animation Rendering - Quick Reference

**Last Updated**: 2025-11-04
**Status**: ✅ Ready for Testing

## TL;DR

Three new components render animations locally:
- **ComposerService**: Combines background + animation into frames
- **Direct2DRenderer**: Displays frames on screen via GDI+
- **LocalAnimationRenderingService**: Orchestrates both (60 FPS timer loop)

Everything is integrated into MainWindowViewModel via `ApplyWallpaperViaDirect2D` command.

---

## Quick Start for Testing

### 1. Run Application
```bash
cd WaBiBaBuSy
dotnet run --project WaBiBaBuSy.UI
```

### 2. Select Animation
- Wallpapers tab → Select MP4/GIF/MKV file

### 3. Apply via Direct2D
- **Option A**: Click "Apply Wallpaper Via Direct2D" button
- **Option B**: Select monitor → Right-click → "Apply Direct2D"

### 4. Observe
- Animation should display on selected monitor
- Black background (default)
- Looping continuously
- Check debug output for `[Direct2D]` messages

### 5. Stop
- Select different animation → Apply
- Or close application

---

## Component Quick Reference

### ComposerService
```csharp
// Initialize
await composer.InitializeAsync(canvasManager, backgroundConfig, animationConfig);

// Get frame
Bitmap frame = composer.ComposeSingle(screen, elapsedMs, pixelsPerSecond);

// All screens
Dictionary<string, Bitmap> frames = composer.ComposeAll(elapsedMs, pixelsPerSecond);

// Control
composer.ResetAnimation();
composer.UpdateAnimationPosition(elapsedMs, pixelsPerSecond);
composer.Dispose();
```

### Direct2DRenderer
```csharp
// Display single frame
renderer.DisplayFrame(clientId, frame, screenMapping);

// Display all frames
renderer.DisplayFrames(frames, screenMappingDict);

// Cleanup
renderer.ClearCache();
renderer.Dispose();
```

### LocalAnimationRenderingService
```csharp
// Create
var service = new LocalAnimationRenderingService(logger, desktopManager, loggerFactory);

// Initialize
await service.InitializeAsync(backgroundConfig, animationConfig, monitorIndex);

// Control
service.Start();              // Begin rendering (~60 FPS)
service.SetPlaybackSpeed(500); // Pixels per second
service.Reset();              // Back to start
service.Stop();               // Pause rendering
service.Dispose();            // Cleanup
```

### MainWindowViewModel
```csharp
// Test command
await ApplyWallpaperViaDirect2D();

// Direct implementation
await ApplyWallpaperWithDirect2DAsync(wallpaper, monitorIndex);

// Service storage
_localAnimationServices[monitorIndex];  // Access active service
```

---

## File Locations

### Implementation
- `WaBiBaBuSy.WallpaperEngine/Direct2D/Direct2DInterop.cs`
- `WaBiBaBuSy.WallpaperEngine/Direct2D/Direct2DRenderer.cs`
- `WaBiBaBuSy.WallpaperEngine/Composition/ComposerService.cs`
- `WaBiBaBuSy.WallpaperEngine/Services/LocalAnimationRenderingService.cs`
- `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs`

### Documentation
- `DIRECT2D_IMPLEMENTATION.md` - Architecture details
- `TESTING_DIRECT2D.md` - Testing procedures
- `INTEGRATION_GUIDE.md` - Usage guide
- `IMPLEMENTATION_COMPLETE.md` - Summary

---

## Common Configurations

### Standard Animation (Black Background)
```csharp
var animationConfig = new AnimationLayerConfig
{
    AnimationPath = filePath,
    TargetHeight = 720,
    Loop = true,
    VerticalAlign = VerticalAlignment.Center
};

var backgroundConfig = new BackgroundLayerConfig
{
    Mode = BackgroundMode.SolidColor,
    ColorHex = "#000000"
};
```

### Custom Background Image
```csharp
var backgroundConfig = new BackgroundLayerConfig
{
    Mode = BackgroundMode.StretchedImage,
    ImagePath = "C:\\path\\to\\background.jpg"
};
```

### Higher Resolution
```csharp
animationConfig.TargetHeight = 1080;  // 4K display
```

### Different Alignment
```csharp
animationConfig.VerticalAlign = VerticalAlignment.Top;     // Top
animationConfig.VerticalAlign = VerticalAlignment.Center;  // Center (default)
animationConfig.VerticalAlign = VerticalAlignment.Bottom;  // Bottom
```

---

## Debug Output

Look for `[Direct2D]` messages in Output window:

```
[Direct2D] TEST: Applying 'animation.mp4' via Direct2D
[Direct2D] Initializing animation service for monitor 0
[Direct2D] Starting animation rendering for monitor 0
[Direct2D] Successfully started animation rendering...

[Direct2D] Error: Invalid monitor index
[Direct2D] Stack trace: ...
```

---

## Performance Expectations

| Metric | Expected | Current |
|--------|----------|---------|
| Start-up | <500ms | Not measured |
| Frame composition | ~2-5ms | Not measured |
| Rendering (GDI+) | ~5-10ms | Not measured |
| Total frame time | <16ms (60 FPS) | Not measured |
| CPU usage | 10-20% | Not measured |
| Memory | ~200 MB | Not measured |

---

## Troubleshooting

### Animation doesn't display
- Check monitor index is valid
- Verify file exists and is readable
- Check debug output for errors
- Try different animation file

### High CPU usage
- Check animation resolution vs display resolution
- Verify no other heavy tasks running
- Try lower resolution animation

### Memory leak
- Check Dispose() is called
- Monitor Task Manager memory over time
- Check for exception in debug output

### Crashes
- Check animation file is not corrupted
- Try different animation format
- Update graphics drivers
- Check Event Viewer for crash details

---

## Key Data Flow

```
User selects animation
    ↓
ApplyWallpaperViaDirect2D()
    ↓
Create LocalAnimationRenderingService
    ↓
service.InitializeAsync()
    ↓
service.Start()
    ↓
Timer fires every 16ms
    ↓
ComposerService.ComposeSingle() → Bitmap
    ↓
Direct2DRenderer.DisplayFrame() → WorkerW window
    ↓
Frame disposed
    ↓
(Repeat)
```

---

## Testing Checklist

- [ ] Compile without errors
- [ ] Monitor detected in UI
- [ ] Animation file selected
- [ ] ApplyWallpaperViaDirect2D executes
- [ ] Animation displays on screen
- [ ] Debug output shows correct sequence
- [ ] Animation loops continuously
- [ ] Playback smooth (~60 FPS)
- [ ] CPU/memory acceptable
- [ ] Can switch animations
- [ ] Proper cleanup on exit

---

## Integration Points

### Where to Find Things
- **Renderer**: `WaBiBaBuSy.WallpaperEngine.Direct2D.Direct2DRenderer`
- **Composer**: `WaBiBaBuSy.WallpaperEngine.Composition.ComposerService`
- **Service**: `WaBiBaBuSy.WallpaperEngine.Services.LocalAnimationRenderingService`
- **UI Command**: `WaBiBaBuSy.UI.ViewModels.MainWindowViewModel.ApplyWallpaperViaDirect2D`
- **Service Storage**: `MainWindowViewModel._localAnimationServices`

### Dependencies
- `System.Drawing` (Bitmap)
- `Microsoft.Extensions.Logging` (Logging)
- `System.Threading.Timer` (Render loop)
- `System.Windows.Forms` (Screen enumeration)

---

## Future Upgrades

### Phase 2: Native Direct2D
- Replace GDI+ with native Direct2D
- GPU acceleration via Direct3D 11
- Reduce CPU usage 50-70%
- Better 4K support

### Phase 3: Multi-Animation
- Multiple animations per monitor
- Animation sequencing
- Advanced orchestration

### Phase 4: Network Integration
- Remote client rendering
- gRPC distribution
- Unified local + remote system

---

## Related Documentation

| Document | Purpose |
|----------|---------|
| DIRECT2D_IMPLEMENTATION.md | Architecture details |
| TESTING_DIRECT2D.md | Testing procedures |
| INTEGRATION_GUIDE.md | Integration manual |
| IMPLEMENTATION_COMPLETE.md | Implementation summary |
| CLAUDE.md | Project overview |

---

## Quick Debugging Tips

1. **Enable debug output**
   - Visual Studio: Debug → Windows → Output
   - Look for `[Direct2D]` messages

2. **Check active services**
   ```csharp
   _localAnimationServices.Count           // Number active
   _localAnimationServices[0].IsRunning   // Status check
   ```

3. **Monitor Task Manager**
   - CPU usage (baseline <20%)
   - Memory (stable, <300MB)
   - No spikes or leaks

4. **Test file compatibility**
   - Try VLC player with same file
   - Check format support (MP4, MKV, GIF)
   - Verify file is not corrupted

---

## Success Indicators

✅ Animation displays on screen
✅ Runs continuously (~60 FPS)
✅ CPU usage acceptable
✅ Memory stable
✅ Can switch animations
✅ Proper cleanup on exit
✅ No crashes or exceptions

---

## Emergency Troubleshooting

| Issue | Quick Fix |
|-------|-----------|
| Animation doesn't show | Try different file |
| High CPU | Close other apps, try lower res |
| Memory leak | Restart application |
| Crash on startup | Update graphics drivers |
| Service not starting | Check monitor index |

---

## Building & Testing

```bash
# Compile
dotnet build WaBiBaBuSy.sln

# Run
dotnet run --project WaBiBaBuSy.UI

# Test animation
1. Select file in Wallpapers tab
2. Click "Apply Wallpaper Via Direct2D"
3. Check debug output for [Direct2D] messages
4. Verify animation on screen
```

---

## Key Metrics to Track During Testing

```
Startup:
  - Time to initialize: _____ ms
  - Time to start rendering: _____ ms

Performance:
  - CPU usage: _____ %
  - Memory: _____ MB
  - Frame time: _____ ms
  - Observed FPS: _____

Quality:
  - Visual artifacts? Yes / No
  - Smooth playback? Yes / No
  - Correct position? Yes / No
  - Colors accurate? Yes / No
```

---

## One-Page Execution Flow

```
START APPLICATION
  ↓
SELECT ANIMATION FILE
  ↓
CLICK "APPLY VIA DIRECT2D"
  ↓
ApplyWallpaperViaDirect2D()
  ├─ Validate wallpaper selected
  ├─ Get monitor index
  ├─ Check file format
  ├─ Call ApplyWallpaperWithDirect2DAsync()
  │   ├─ Create LocalAnimationRenderingService
  │   ├─ await service.InitializeAsync()
  │   ├─ service.SetPlaybackSpeed()
  │   ├─ service.Start()
  │   └─ Store in _localAnimationServices
  └─ Update UI
  ↓
RENDER LOOP (Timer-based, ~60 FPS)
  ├─ Calculate elapsed time
  ├─ composer.ComposeSingle() → Bitmap
  ├─ renderer.DisplayFrame() → Screen
  └─ Dispose frame
  ↓
(Repeat until stopped)
  ↓
STOP (User selects new animation or exits)
  ├─ service.Stop()
  ├─ service.Dispose()
  └─ _localAnimationServices.Remove()
  ↓
END
```

---

**Status**: ✅ READY TO TEST

See TESTING_DIRECT2D.md for comprehensive testing procedures.
