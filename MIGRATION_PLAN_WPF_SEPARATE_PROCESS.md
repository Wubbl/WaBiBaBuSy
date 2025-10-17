# Migration Plan: WPF + Separate Process Architecture

**Date:** 2025-10-17
**Status:** Planning Phase
**Goal:** Fix wallpaper rendering by adopting Lively's proven architecture

---

## Problem Statement

**Root Cause Identified:** Windows Forms is fundamentally incompatible with being parented to system windows (WorkerW/Progman).

**Evidence:**
1. SetParent succeeds initially but Windows Forms immediately resets the parent back to null
2. Wallpaper flashes briefly then disappears (Windows Forms is fighting the parenting)
3. All Win32 API calls succeed, but the Forms rendering pipeline doesn't work when parented
4. Lively Wallpaper uses **WPF + Separate Processes** and works perfectly

**Why Lively Works:**
- **WPF Windows** instead of WinForms Forms (better compatibility with system window parenting)
- **Separate Process Architecture** - each wallpaper runs as standalone .exe
- Main app calls SetParent on windows from different process (Forms framework can't fight back)

---

## Current Architecture

```
┌─────────────────────────────────────────────────────────┐
│ WaBiBaBuSy.UI.exe (Single Process)                      │
├─────────────────────────────────────────────────────────┤
│                                                          │
│  ┌────────────────────────────────────────┐            │
│  │ MainWindow (Avalonia UI)               │            │
│  │  - Server Control                      │            │
│  │  - Client Connection                   │            │
│  │  - Settings                            │            │
│  └────────────────────────────────────────┘            │
│                                                          │
│  ┌────────────────────────────────────────┐            │
│  │ WallpaperPlaybackService               │            │
│  │  └─> ImageWallpaperRenderer            │            │
│  │       - Windows Forms Form             │  ❌ PROBLEM │
│  │       - PictureBox                     │            │
│  │  └─> VideoWallpaperRenderer            │            │
│  │       - Windows Forms Form             │  ❌ PROBLEM │
│  │       - LibVLC VideoView               │            │
│  │  └─> GifWallpaperRenderer              │            │
│  │       - Windows Forms Form             │  ❌ PROBLEM │
│  │       - Animated PictureBox            │            │
│  └────────────────────────────────────────┘            │
│                                                          │
│  ┌────────────────────────────────────────┐            │
│  │ DesktopWindowManager                   │            │
│  │  - FindWorkerW()                       │            │
│  │  - SetParent() ← FAILS WITH WINFORMS  │            │
│  └────────────────────────────────────────┘            │
└─────────────────────────────────────────────────────────┘
```

---

## Target Architecture (Lively-Inspired)

```
┌─────────────────────────────────────────────────────────────────┐
│ WaBiBaBuSy.UI.exe (Main Process)                                │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌────────────────────────────────────────┐                    │
│  │ MainWindow (Avalonia UI)               │                    │
│  │  - Server Control                      │                    │
│  │  - Client Connection                   │                    │
│  │  - Settings                            │                    │
│  └────────────────────────────────────────┘                    │
│                                                                  │
│  ┌────────────────────────────────────────┐                    │
│  │ WallpaperPlaybackService               │                    │
│  │  - Launches wallpaper processes        │                    │
│  │  - Receives HWND via IPC               │                    │
│  │  - Calls SetParent on external HWND    │  ✅ WORKS!        │
│  │  - Controls playback via IPC           │                    │
│  └────────────────────────────────────────┘                    │
│                    │                                             │
│                    │ IPC (stdin/stdout JSON)                    │
│                    │                                             │
└────────────────────┼─────────────────────────────────────────────┘
                     │
         ┌───────────┴───────────┬───────────────┐
         │                       │               │
         ▼                       ▼               ▼
┌─────────────────┐   ┌─────────────────┐   ┌─────────────────┐
│ WaBiBaBuSy      │   │ WaBiBaBuSy      │   │ WaBiBaBuSy      │
│ .Player.Image   │   │ .Player.Video   │   │ .Player.Gif     │
│ .exe            │   │ .exe            │   │ .exe            │
├─────────────────┤   ├─────────────────┤   ├─────────────────┤
│ WPF Window      │   │ WPF Window      │   │ WPF Window      │
│  - WPF Image    │   │  - LibVLC WPF   │   │  - WPF Image +  │
│    Control      │   │    VideoView    │   │    Animation    │
│                 │   │                 │   │                 │
│ Sends HWND to   │   │ Sends HWND to   │   │ Sends HWND to   │
│ parent via IPC  │   │ parent via IPC  │   │ parent via IPC  │
└─────────────────┘   └─────────────────┘   └─────────────────┘
```

---

## Implementation Phases

### Phase 1: Create Separate Player Projects (Week 1)

**Goal:** Set up the infrastructure for separate process architecture

**Tasks:**
1. **Create Player Projects**
   - [ ] Create `WaBiBaBuSy.Player.Image` (WPF .exe)
   - [ ] Create `WaBiBaBuSy.Player.Video` (WPF .exe)
   - [ ] Create `WaBiBaBuSy.Player.Gif` (WPF .exe)
   - [ ] Create `WaBiBaBuSy.Player.Common` (shared code library)

2. **Define IPC Contract**
   - [ ] Create message models (JSON-based)
     - `PlayerMessageHwnd` - Send HWND to parent
     - `PlayerMessageLoaded` - Wallpaper loaded successfully
     - `PlayerMessageError` - Error occurred
     - `PlayerCommandLoad` - Load wallpaper file
     - `PlayerCommandPlay` - Start playback
     - `PlayerCommandPause` - Pause playback
     - `PlayerCommandSeek` - Seek to position
     - `PlayerCommandClose` - Shutdown player

3. **IPC Infrastructure**
   - [ ] Implement stdin/stdout JSON communication
   - [ ] Create `ProcessCommunicator` class for parent process
   - [ ] Create `StdInListener` for player processes
   - [ ] Add error handling and reconnection logic

**Deliverable:** Empty player .exe files that can receive commands and respond

---

### Phase 2: Convert ImageWallpaperRenderer to WPF (Week 2)

**Goal:** Get static images working with WPF + separate process

**Tasks:**
1. **Create WPF Image Player**
   - [ ] Create `WaBiBaBuSy.Player.Image` project
   - [ ] Add WPF Window with Image control
   - [ ] Load image from command-line argument
   - [ ] Send HWND to parent via stdout
   - [ ] Listen for commands on stdin

2. **Convert ImageWallpaperRenderer**
   - [ ] Replace WinForms logic with process launcher
   - [ ] Launch `WaBiBaBuSy.Player.Image.exe`
   - [ ] Receive HWND via IPC
   - [ ] Call SetParent on received HWND
   - [ ] Send play/pause/stop commands via IPC

3. **Testing**
   - [ ] Test image loading
   - [ ] Test SetParent with WPF window
   - [ ] Test playback control
   - [ ] Test process lifecycle (start/stop/crash recovery)

**Deliverable:** Working static image wallpapers using WPF + separate process

---

### Phase 3: Convert VideoWallpaperRenderer to WPF (Week 3)

**Goal:** Get videos working with WPF + separate process

**Tasks:**
1. **Create WPF Video Player**
   - [ ] Create `WaBiBaBuSy.Player.Video` project
   - [ ] Add LibVLCSharp.WPF package
   - [ ] Create WPF Window with VideoView
   - [ ] Load video from command-line argument
   - [ ] Implement play/pause/seek/stop commands
   - [ ] Send HWND to parent via stdout

2. **Convert VideoWallpaperRenderer**
   - [ ] Replace WinForms logic with process launcher
   - [ ] Launch `WaBiBaBuSy.Player.Video.exe`
   - [ ] Receive HWND via IPC
   - [ ] Call SetParent on received HWND
   - [ ] Send playback commands via IPC
   - [ ] Handle synchronization timestamps

3. **Testing**
   - [ ] Test video playback
   - [ ] Test hardware acceleration
   - [ ] Test synchronization across multiple clients
   - [ ] Test looping behavior

**Deliverable:** Working video wallpapers using WPF + separate process

---

### Phase 4: Convert GifWallpaperRenderer to WPF (Week 4)

**Goal:** Get GIFs working with WPF + separate process

**Tasks:**
1. **Create WPF GIF Player**
   - [ ] Create `WaBiBaBuSy.Player.Gif` project
   - [ ] Create WPF Window with animated Image
   - [ ] Load GIF from command-line argument
   - [ ] Extract frame delays from GIF metadata
   - [ ] Implement frame-by-frame animation
   - [ ] Send HWND to parent via stdout

2. **Convert GifWallpaperRenderer**
   - [ ] Replace WinForms logic with process launcher
   - [ ] Launch `WaBiBaBuSy.Player.Gif.exe`
   - [ ] Receive HWND via IPC
   - [ ] Call SetParent on received HWND
   - [ ] Handle animation synchronization

3. **Testing**
   - [ ] Test GIF animation
   - [ ] Test frame timing accuracy
   - [ ] Test large GIF files
   - [ ] Test looping behavior

**Deliverable:** Working GIF wallpapers using WPF + separate process

---

### Phase 5: Polish & Production (Week 5)

**Goal:** Production-ready wallpaper system

**Tasks:**
1. **Process Management**
   - [ ] Implement watchdog for crashed players
   - [ ] Auto-restart on failure
   - [ ] Graceful shutdown on exit
   - [ ] Resource cleanup

2. **Error Handling**
   - [ ] Retry logic for IPC failures
   - [ ] Fallback strategies for missing files
   - [ ] User-friendly error messages

3. **Performance**
   - [ ] Optimize IPC communication
   - [ ] Reduce process startup time
   - [ ] Memory leak testing

4. **Documentation**
   - [ ] Update CLAUDE.md
   - [ ] Update architecture diagrams
   - [ ] Add developer documentation

**Deliverable:** Production-ready wallpaper system

---

## Technical Details

### WPF Window Structure

```csharp
// WaBiBaBuSy.Player.Image/MainWindow.xaml.cs
public partial class MainWindow : Window
{
    public MainWindow(string[] args)
    {
        InitializeComponent();
        ParseArguments(args);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Get HWND
        IntPtr handle = new WindowInteropHelper(this).Handle;

        // Send HWND to parent via stdout
        SendToParent(new PlayerMessageHwnd { Hwnd = handle.ToInt32() });

        // Start listening for commands on stdin
        _ = ListenToParent();
    }

    private async Task ListenToParent()
    {
        var reader = new StreamReader(Console.OpenStandardInput());
        while (true)
        {
            string json = await reader.ReadLineAsync();
            var command = JsonConvert.DeserializeObject<PlayerCommand>(json);

            // Handle commands...
        }
    }

    private void SendToParent(object message)
    {
        Console.WriteLine(JsonConvert.SerializeObject(message));
    }
}
```

### Process Launcher Pattern

```csharp
// WaBiBaBuSy.WallpaperEngine/Renderers/ImageWallpaperRenderer.cs (NEW)
public class ImageWallpaperRenderer : IWallpaperRenderer
{
    private Process _playerProcess;
    private ProcessCommunicator _communicator;
    private IntPtr _playerWindowHandle;

    public async Task InitializeAsync(WallpaperConfig config)
    {
        // Launch player process
        _playerProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "WaBiBaBuSy.Player.Image.exe",
            Arguments = $"--file \"{config.FilePath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        });

        // Create communicator
        _communicator = new ProcessCommunicator(_playerProcess);

        // Wait for HWND message
        var hwndMsg = await _communicator.WaitForMessageAsync<PlayerMessageHwnd>();
        _playerWindowHandle = new IntPtr(hwndMsg.Hwnd);

        // Parent to WorkerW/Progman
        _desktopManager.SetAsWallpaperWindow(_playerWindowHandle, config.Screen.Bounds);
    }

    public async Task PlayAsync()
    {
        await _communicator.SendCommandAsync(new PlayerCommandPlay());
    }

    public void Dispose()
    {
        _communicator?.SendCommand(new PlayerCommandClose());
        _playerProcess?.WaitForExit(5000);
        _playerProcess?.Kill();
    }
}
```

---

## Package Dependencies

### New Packages Needed

**For Player Projects (WPF):**
- `System.Windows` (already in .NET)
- `LibVLCSharp.WPF` (for video player)
- `Newtonsoft.Json` (for IPC)
- `CommandLineParser` (for startup args)

**For Core (Process Management):**
- `System.Diagnostics.Process` (already in .NET)
- `Newtonsoft.Json` (already used)

---

## Migration Strategy

### Recommended Approach: Incremental Migration

1. **Phase 1 (Now):** Infrastructure setup - create projects, IPC framework
2. **Phase 2 (Next):** Image player - lowest risk, simplest implementation
3. **Phase 3 (After):** Video player - reuse most of Lively's VLC approach
4. **Phase 4 (Final):** GIF player - build on image player foundation

### Rollback Strategy

- Keep old WinForms renderers until WPF versions are stable
- Feature flag to switch between old/new renderers
- Thorough testing before removing old code

---

## Risks & Mitigation

| Risk | Impact | Mitigation |
|------|--------|------------|
| WPF learning curve | Medium | Copy Lively's patterns closely |
| IPC complexity | High | Use proven JSON stdin/stdout like Lively |
| Process management bugs | High | Implement watchdog, auto-restart |
| Performance overhead | Medium | Profile early, optimize IPC |
| Synchronization issues | Medium | Reuse existing sync logic, just different HWND |

---

## Success Criteria

- [ ] Image wallpapers work behind desktop icons
- [ ] Video wallpapers work behind desktop icons
- [ ] GIF wallpapers work behind desktop icons
- [ ] Desktop icons and taskbar remain accessible
- [ ] Multi-monitor support works
- [ ] Synchronization works across clients
- [ ] Process crash recovery works
- [ ] Memory usage stays under 200MB
- [ ] CPU usage stays under 15%

---

## Timeline Estimate

| Phase | Duration | Deliverable |
|-------|----------|-------------|
| Phase 1 | 1 week | IPC infrastructure |
| Phase 2 | 1 week | Image wallpapers working |
| Phase 3 | 1 week | Video wallpapers working |
| Phase 4 | 1 week | GIF wallpapers working |
| Phase 5 | 1 week | Production polish |
| **Total** | **5 weeks** | Full WPF migration |

---

## Next Steps

1. Review and approve this plan
2. Start Phase 1: Create player project structure
3. Implement basic IPC framework
4. Build proof-of-concept with Image player

---

**Questions to Answer:**
- Do we keep the old WinForms renderers during migration? (Recommended: Yes)
- Do we want feature parity with Lively? (Recommended: No, keep it simpler)
- Should we bundle player .exe files or keep them separate? (Recommended: Same folder as main .exe)
