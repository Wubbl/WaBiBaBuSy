# One-Shot Show Events (Roadmap 2.4, slice 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** While a scene plays, the server fires one-shot moments — Spotlight a seat, a Celebration burst over the whole room, a Traversal (the club logo swims the ring once), a Blackout, an Announce text lap — that every seat draws above the scene from a shared `T0` for a fixed duration, pixel-identical across machines, from the main window or global hotkeys.

**Architecture:** A `ShowEvent` (Models) carries `T0Utc`, duration, seed and per-type parameters. All visuals are pure functions in `WaBiBaBuSy.Models/Show/` of `(event, node layout, event-local ms)`, where event-local ms = `sceneRawElapsed + sceneStartUtc − T0` (works for live frames and exact frames alike). Transport: a new `SyncCommand` type `EVENT` (`T0` in `timestamp_utc`, JSON in the new `event_json` field), handled on the client before the timestamp wait, then forwarded to the D2D players over the existing stdin JSON IPC (`cmd_show_event`). Both playback hosts keep a `ShowEventLog` and replay active events to every newly started player. The player draws events in a new `Program.ShowEvents.cs`; the room preview draws them with the same math in `ScenePainter`.

**Tech Stack:** C# 12 / .NET 9, gRPC (Google.Protobuf), Vortice Direct2D + Magick.NET + System.Drawing (player), Avalonia 11 + CommunityToolkit.Mvvm (UI), Win32 `RegisterHotKey`, xUnit, System.Text.Json + Newtonsoft.Json (player IPC).

**Spec:** [`2026-10-03-show-events-design.md`](2026-10-03-show-events-design.md) (parent: [`2026-09-17-crossing-effects-and-events-design.md`](2026-09-17-crossing-effects-and-events-design.md) §5, §7).

## Global Constraints

- Determinism (CLAUDE.md "Architectural Invariants"): event visuals depend only on the event, the node layout and event-local ms. No `DateTime.Now`, no `Random` in players or preview; the seed is drawn once on the server (`Random.Shared`) and broadcast.
- Proto: add fields with **new numbers only, never renumber**: `CommandType.EVENT = 12`, `SyncCommand.event_json = 6`. Old clients must ignore EVENT (they log "Unknown command type" in `WallpaperPlaybackService`'s `default:` branch).
- `T0` travels in `SyncCommand.timestamp_utc` (the client converts it to its own clock with the existing offset + test skew); the client overwrites `ShowEvent.T0Utc` with it.
- Durations clamped to 250 ms … 10 min (`ShowEventMath.ClampDuration`); fade in/out 250 ms; Traversal lap 2 s … 120 s; default speed 600 canvas px/s.
- Spotlight: target border 16 canvas units, pulse 1.5 Hz, opacity `env × (0.6 + 0.4 sin)`; non-targets dimmed with black at `0.6 × env` (40 % brightness), chevron 0.18 × node height, slides 40 units at 1 Hz.
- Celebration: `N = clamp(ceil(CanvasW / 1000 × 250), 50, 6000)`; gravity 2 × CanvasH/s²; fall speed 0.15 × CanvasH/s; drift ±150 units/s; sway 30 units; size 8…18 units; launch spread 800 ms; colors `ColorHex` + `#FF4136 #2ECC40 #0074D9 #FFDC00 #F012BE`.
- Default durations: Spotlight 8000 ms (`#FFD700`), Celebration 5000 ms (`#FFD700`), Blackout 3000 ms (`#000000`); Traversal height fraction 0.5; Announce height fraction 0.25, speed 400 px/s, color `#FFFFFF`.
- Event settings persisted at `%APPDATA%\WaBiBaBuSy\show-events.json`.
- Player render loop: no per-frame heap allocations (scratch arrays, cached brushes); D2D resources created on the render thread only; nothing blocks the render loop (image decode runs on a worker task).
- Events are drawn above the scene and **below** the debug overlay and the timecode strip (a Blackout must never break drift probes).
- Hotkeys Ctrl+Alt+1 Spotlight, 2 Celebration, 3 Traversal, 4 Blackout, 5 Announce — registered only while the server runs.
- Code style: file-scoped namespaces, XML docs on public APIs, match surrounding comment density. Do not edit `graphify-out/` (the post-commit hook rebuilds it).
- Every commit on `main` (trunk-based), after `dotnet build WaBiBaBuSy.sln` → 0 errors and `dotnet test WaBiBaBuSy.Tests` → green, message ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never launch the app without telling the user.

## Review Focus

1. **Playlist item switch during an event** (players are respawned per item): the replayed event must continue at the same position, not restart and not appear twice — dedupe by `EventId` and `LocalMs` independent of the scene start. Tests in Task 2 (`Add_SameEventId_IsIgnored`, `LocalMs_SameWallInstant_SameValueForAnySceneStart`).
2. **Server/client clock offset:** `T0` must come from the (converted) command timestamp, never from the JSON body — test in Task 7 (`TryFromCommand_TakesT0FromTheTimestamp_NotTheJson`).
3. **Hand-edited or absurd values** (`DurationMs` 0 / −5 / int.MaxValue, `HeightFraction` 50, `SpriteAspect` NaN / 0, `ColorHex` "red"): clamped, never a crash or a stalled player — tests in Tasks 2, 5 (`Duration_Clamped`, `SpriteSize_ClampsAbsurdValues`) and 3 (`ParseColor_Invalid_IsWhite`).
4. **Huge rooms** (20 seats × 4K): the particle count is capped and each node only evaluates its own index range — test in Task 4 (`Capacity_IsBoundedForHugeRooms`).
5. **Traversal image not downloadable on a client:** the event still reaches the player (others may still be drawn) with `ImagePath = null`, and the player skips the sprite — test in Task 8 (`AssetNotDownloadable_ForwardsWithoutImage`).

---

## File Structure

| File | Responsibility |
|---|---|
| `WaBiBaBuSy.Models/Show/ShowEvent.cs` (new) | `ShowEventType`, `ShowEvent` |
| `WaBiBaBuSy.Models/Show/ShowEventSettings.cs` (new) | Persisted defaults for the triggers |
| `WaBiBaBuSy.Models/Configuration/ShowEventSettingsStore.cs` (new) | JSON persistence of the settings |
| `WaBiBaBuSy.Models/Show/ShowEventMath.cs` (new) | Timing (local ms, active, envelope, clamp), blackout opacity, color parse |
| `WaBiBaBuSy.Models/Show/ShowEventLog.cs` (new) | Thread-safe event list: dedupe, cap, active, prune, copy |
| `WaBiBaBuSy.Models/Show/SpotlightMath.cs` (new) | Spotlight visual per node |
| `WaBiBaBuSy.Models/Show/CelebrationMath.cs` (new) | Closed-form particle burst |
| `WaBiBaBuSy.Models/Show/TraversalMath.cs` (new) | One-lap pose + duration (Traversal and Announce) |
| `WaBiBaBuSy.Models/Show/ShowEventFactory.cs` (new) | Builds an event from type, seat, layout, settings |
| `WaBiBaBuSy.Models/Show/ShowEventHotkeys.cs` (new) | Hotkey slot ↔ event type |
| `WaBiBaBuSy.Models/Content/ContentAssetRef.cs` | `AssetRole.Event` |
| `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` | `EVENT = 12`, `event_json = 6` |
| `WaBiBaBuSy.Core/Services/Show/ShowEventCodec.cs` (new) | Event ↔ `SyncCommand` |
| `WaBiBaBuSy.Core/Services/WallpaperSyncCoordinator.cs` | `BroadcastShowEventAsync` |
| `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs` | EVENT before the timing wait, asset fetch, `ShowEventDelegate` |
| `WaBiBaBuSy.Core/Services/ClientPlaybackDelegates.cs`, `WaBiBaBuSyService.cs` | Delegate plumbing |
| `WaBiBaBuSy.Player.Common/Messages/PlayerCommandShowEvent.cs` (new) | `cmd_show_event` |
| `WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs`, `Composition/D2DCompositionService.cs` | `SendShowEventAsync` |
| `WaBiBaBuSy.WallpaperEngine/Show/TextSprite.cs` (new, Task 16) | GDI+ text bitmap + aspect for Announce |
| `WaBiBaBuSy.Player.D2D/Program.ShowEvents.cs` (new) | Event list, sprite decode/upload, drawing |
| `WaBiBaBuSy.Player.D2D/Program.cs` | `cmd_show_event` dispatch, draw hooks, disposal |
| `WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs` | `FireEventAsync`, log, replay, prefetch, settings |
| `WaBiBaBuSy.UI/Services/RemotePlaybackHost.cs` | Delegate, log, replay |
| `WaBiBaBuSy.UI/Services/EventImages.cs` (new) | Image aspect via Avalonia |
| `WaBiBaBuSy.UI/Controls/ScenePainter.Events.cs` (new) | Preview drawing |
| `WaBiBaBuSy.UI/Controls/RoomView.cs` | `ShowEvents` property, paint call |
| `WaBiBaBuSy.UI/ViewModels/ShowEventsViewModel.cs` (new) | Events strip |
| `WaBiBaBuSy.UI/Views/MainWindow.axaml`, `MainWindowViewModel.cs` | Strip + bindings |
| `WaBiBaBuSy.UI/Services/GlobalHotkeyService.cs` (new) | Win32 hotkeys |
| `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs` | Hotkey ownership |
| `WaBiBaBuSy.Models/Testing/TestScenario.cs`, `ScenarioLoader.cs`, `WaBiBaBuSy.Core/Services/Testing/ITestHost.cs`, `TestRunner.cs`, `WaBiBaBuSy.UI/Services/Testing/TestHostAdapter.cs` | `showEvent` step |
| `TestScenarios/showcase/events.json` (new) | Visual check |
| `WaBiBaBuSy.Models/Wallpaper/Playlist.cs`, `WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs`, `PlaylistViewModel.cs`, `PlaylistPanel.axaml` (Task 17, optional) | Scheduled interrupts |

Tests (all new unless noted): `ShowEventModelTests`, `ShowEventTimingTests`, `ShowEventLogTests`, `SpotlightMathTests`, `CelebrationMathTests`, `TraversalMathTests`, `ShowEventFactoryTests`, `ShowEventCodecTests`, `ShowEventClientTests`, `ShowEventHotkeysTests`, `ScenarioLoaderTests` (existing), `TestRunnerTests` (existing), `TestRunCoordinatorTests` (existing, fake host only), `PlaylistInterruptTests` (Task 17).

Build/test commands used throughout:

```bash
dotnet build WaBiBaBuSy.sln
dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEvent"
dotnet test WaBiBaBuSy.Tests
```

---

### Task 1: Event model, settings and store

**Files:**
- Create: `WaBiBaBuSy.Models/Show/ShowEvent.cs`, `WaBiBaBuSy.Models/Show/ShowEventSettings.cs`, `WaBiBaBuSy.Models/Configuration/ShowEventSettingsStore.cs`
- Test: `WaBiBaBuSy.Tests/ShowEventModelTests.cs`

**Interfaces:**
- Produces: `enum ShowEventType { Spotlight, Celebration, Traversal, Blackout, Announce }` (System.Text.Json: strings); `ShowEvent { string EventId; ShowEventType Type; long T0Utc; int DurationMs; int Seed; string ColorHex = "#FFD700"; int TargetOrder = -1; float TargetCenterX; string? ImagePath; float SpriteAspect = 1f; float HeightFraction = 0.5f; bool Reverse; string? Text; ShowEvent Clone() }`; `ShowEventSettings` (fields below); `ShowEventSettingsStore.Load(string? path = null)`, `.Save(ShowEventSettings, string? path = null)`, `.DefaultPath`.

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/ShowEventModelTests.cs
using System.Text.Json;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventModelTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wbbs-events-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    private static ShowEvent Full() => new()
    {
        EventId = "e1", Type = ShowEventType.Traversal, T0Utc = 1_700_000_000_123, DurationMs = 9000, Seed = -42,
        ColorHex = "#7FDBFF", TargetOrder = 3, TargetCenterX = 5760.5f, ImagePath = @"C:\logo.png",
        SpriteAspect = 2.5f, HeightFraction = 0.4f, Reverse = true, Text = "Round 3",
    };

    private static void AssertSame(ShowEvent a, ShowEvent b)
    {
        Assert.Equal(a.EventId, b.EventId); Assert.Equal(a.Type, b.Type); Assert.Equal(a.T0Utc, b.T0Utc);
        Assert.Equal(a.DurationMs, b.DurationMs); Assert.Equal(a.Seed, b.Seed); Assert.Equal(a.ColorHex, b.ColorHex);
        Assert.Equal(a.TargetOrder, b.TargetOrder); Assert.Equal(a.TargetCenterX, b.TargetCenterX);
        Assert.Equal(a.ImagePath, b.ImagePath); Assert.Equal(a.SpriteAspect, b.SpriteAspect);
        Assert.Equal(a.HeightFraction, b.HeightFraction); Assert.Equal(a.Reverse, b.Reverse); Assert.Equal(a.Text, b.Text);
    }

    [Fact]
    public void SystemTextJson_RoundTrip_KeepsEveryField() =>
        AssertSame(Full(), JsonSerializer.Deserialize<ShowEvent>(JsonSerializer.Serialize(Full()))!);

    [Fact]
    public void SystemTextJson_WritesTheTypeAsAString() =>
        Assert.Contains("\"Traversal\"", JsonSerializer.Serialize(Full()));

    [Fact]
    public void Newtonsoft_RoundTrip_KeepsEveryField() =>   // the player IPC serializes with Newtonsoft
        AssertSame(Full(), Newtonsoft.Json.JsonConvert.DeserializeObject<ShowEvent>(Newtonsoft.Json.JsonConvert.SerializeObject(Full()))!);

    [Fact]
    public void Clone_IsIndependent()
    {
        var a = Full();
        var b = a.Clone();
        b.T0Utc = 1; b.ImagePath = null;
        Assert.Equal(1_700_000_000_123, a.T0Utc);
        Assert.Equal(@"C:\logo.png", a.ImagePath);
    }

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var s = new ShowEventSettings();
        Assert.Equal(8000, s.SpotlightDurationMs);
        Assert.Equal(5000, s.CelebrationDurationMs);
        Assert.Equal(3000, s.BlackoutDurationMs);
        Assert.Equal("#000000", s.BlackoutColorHex);
        Assert.Equal(600f, s.TraversalSpeedPxPerSecond);
        Assert.Equal(0.5f, s.TraversalHeightFraction);
        Assert.Equal(400f, s.AnnounceSpeedPxPerSecond);
        Assert.True(s.HotkeysEnabled);
    }

    [Fact]
    public void Store_MissingFile_GivesDefaults() =>
        Assert.Equal(8000, ShowEventSettingsStore.Load(_path).SpotlightDurationMs);

    [Fact]
    public void Store_CorruptFile_GivesDefaults()
    {
        File.WriteAllText(_path, "{ not json");
        Assert.Equal(5000, ShowEventSettingsStore.Load(_path).CelebrationDurationMs);
    }

    [Fact]
    public void Store_SaveThenLoad_RoundTrips()
    {
        ShowEventSettingsStore.Save(new ShowEventSettings { TraversalImagePath = @"C:\club.png", LastTargetSeatId = "pc-07", SpotlightDurationMs = 12000 }, _path);
        var back = ShowEventSettingsStore.Load(_path);
        Assert.Equal(@"C:\club.png", back.TraversalImagePath);
        Assert.Equal("pc-07", back.LastTargetSeatId);
        Assert.Equal(12000, back.SpotlightDurationMs);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventModelTests"`
Expected: build error — `WaBiBaBuSy.Models.Show` does not exist.

- [ ] **Step 3: Implement**

```csharp
// WaBiBaBuSy.Models/Show/ShowEvent.cs
using System.Text.Json.Serialization;

namespace WaBiBaBuSy.Models.Show;

/// <summary>Kind of one-shot show event (roadmap 2.4, design 2026-10-03-show-events-design.md).</summary>
/// <remarks>System.Text.Json writes the name, so an old client that does not know a newer type fails to parse it and ignores the event.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShowEventType { Spotlight, Celebration, Traversal, Blackout, Announce }

/// <summary>
/// One one-shot moment drawn by every seat above the scene from <see cref="T0Utc"/> for <see cref="DurationMs"/>.
/// Everything a node draws is a pure function of this object, its node layout and the event-local time.
/// </summary>
public sealed class ShowEvent
{
    /// <summary>Unique per firing; players and hosts dedupe replays by it.</summary>
    public string EventId { get; set; } = string.Empty;
    public ShowEventType Type { get; set; }
    /// <summary>Shared start, UTC ms. On clients: the command's timestamp after clock conversion.</summary>
    public long T0Utc { get; set; }
    public int DurationMs { get; set; }
    /// <summary>Drawn once on the server and broadcast (Celebration particles).</summary>
    public int Seed { get; set; }
    public string ColorHex { get; set; } = "#FFD700";
    /// <summary>Spotlight: traversal index (<c>NodeLayout.Order</c>) of the target seat; -1 = none.</summary>
    public int TargetOrder { get; set; } = -1;
    /// <summary>Spotlight: center of the target's slice on the shared canvas (chevron direction).</summary>
    public float TargetCenterX { get; set; }
    /// <summary>Traversal: image file; rewritten to the client's cache on receipt (null when it could not be fetched).</summary>
    public string? ImagePath { get; set; }
    /// <summary>Traversal / Announce: width ÷ height of the sprite, measured once on the server.</summary>
    public float SpriteAspect { get; set; } = 1f;
    /// <summary>Traversal / Announce: sprite height as a fraction of the canvas height.</summary>
    public float HeightFraction { get; set; } = 0.5f;
    /// <summary>Traversal / Announce: run the lap against the traversal direction.</summary>
    public bool Reverse { get; set; }
    /// <summary>Announce: the text.</summary>
    public string? Text { get; set; }

    /// <summary>Shallow copy (all members are values or immutable strings).</summary>
    public ShowEvent Clone() => (ShowEvent)MemberwiseClone();
}
```

```csharp
// WaBiBaBuSy.Models/Show/ShowEventSettings.cs
namespace WaBiBaBuSy.Models.Show;

/// <summary>Defaults the Events strip and the hotkeys use when they fire an event (persisted by <c>ShowEventSettingsStore</c>).</summary>
public sealed class ShowEventSettings
{
    public int SpotlightDurationMs { get; set; } = 8000;
    public string SpotlightColorHex { get; set; } = "#FFD700";
    public int CelebrationDurationMs { get; set; } = 5000;
    public string CelebrationColorHex { get; set; } = "#FFD700";
    /// <summary>Empty = the running scene's sprite file.</summary>
    public string TraversalImagePath { get; set; } = string.Empty;
    public float TraversalSpeedPxPerSecond { get; set; } = 600f;
    public float TraversalHeightFraction { get; set; } = 0.5f;
    public bool TraversalReverse { get; set; }
    public int BlackoutDurationMs { get; set; } = 3000;
    public string BlackoutColorHex { get; set; } = "#000000";
    public string AnnounceText { get; set; } = string.Empty;
    public string AnnounceColorHex { get; set; } = "#FFFFFF";
    public float AnnounceSpeedPxPerSecond { get; set; } = 400f;
    public float AnnounceHeightFraction { get; set; } = 0.25f;
    /// <summary>Seat the last Spotlight targeted (the hotkey uses it).</summary>
    public string? LastTargetSeatId { get; set; }
    public bool HotkeysEnabled { get; set; } = true;
}
```

```csharp
// WaBiBaBuSy.Models/Configuration/ShowEventSettingsStore.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Models.Configuration;

/// <summary>
/// JSON persistence for <see cref="ShowEventSettings"/> (<c>%APPDATA%\WaBiBaBuSy\show-events.json</c>).
/// Missing or unreadable file → defaults.
/// </summary>
public static class ShowEventSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WaBiBaBuSy", "show-events.json");

    public static ShowEventSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ShowEventSettings>(File.ReadAllText(path), JsonOptions) ?? new ShowEventSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ShowEventSettingsStore] Could not read {path}: {ex.Message}. Using defaults.");
        }
        return new ShowEventSettings();
    }

    public static void Save(ShowEventSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ShowEventSettingsStore] Could not write {path}: {ex.Message}");
        }
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventModelTests"`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Show WaBiBaBuSy.Models/Configuration/ShowEventSettingsStore.cs WaBiBaBuSy.Tests/ShowEventModelTests.cs
git commit -m "feat(events): ShowEvent model, settings and store

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Event timing and the event log

**Files:**
- Create: `WaBiBaBuSy.Models/Show/ShowEventMath.cs`, `WaBiBaBuSy.Models/Show/ShowEventLog.cs`
- Test: `WaBiBaBuSy.Tests/ShowEventTimingTests.cs`, `WaBiBaBuSy.Tests/ShowEventLogTests.cs`

**Interfaces:**
- Consumes: `ShowEvent` (Task 1); `SpriteEffects.ParseBezelColor(string?)` (existing, white fallback).
- Produces: `ShowEventMath.{MinDurationMs=250, MaxDurationMs=600_000, FadeMs=250}`, `int ClampDuration(int)`, `long LocalMs(ShowEvent, long sceneStartUtcMs, long sceneRawElapsedMs)`, `bool IsActive(ShowEvent, long localMs)`, `bool Ended(ShowEvent, long nowUtcMs)`, `float Envelope(long localMs, int durationMs, int fadeMs = FadeMs)`, `float BlackoutOpacity(ShowEvent, long localMs)`, `(byte R, byte G, byte B) ParseColor(string?)`, `double Sin(long ms, double hz)`. `ShowEventLog { const int Capacity = 16; const long KeepAfterEndMs = 60_000; int Count; bool Add(ShowEvent); IReadOnlyList<ShowEvent> Active(long nowUtcMs); int Prune(long nowUtcMs); int CopyTo(ShowEvent[] target); bool Contains(string eventId); void Clear() }`.

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/ShowEventTimingTests.cs
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventTimingTests
{
    private static ShowEvent At(long t0, int duration = 4000) => new() { EventId = "e", T0Utc = t0, DurationMs = duration };

    [Fact]
    public void LocalMs_LiveAndExactFrame_Agree()
    {
        var e = At(t0: 10_500);
        long sceneStart = 10_000;
        // Live frame at wall time 12_000 → scene raw elapsed 2000. An exact frame at elapsed 2000 must see the same.
        Assert.Equal(1500, ShowEventMath.LocalMs(e, sceneStart, sceneRawElapsedMs: 12_000 - sceneStart));
        Assert.Equal(1500, ShowEventMath.LocalMs(e, sceneStart, sceneRawElapsedMs: 2000));
    }

    [Theory]
    [InlineData(10_000L)]
    [InlineData(11_900L)]
    [InlineData(-5_000L)]
    public void LocalMs_SameWallInstant_SameValueForAnySceneStart(long sceneStart)
    {
        // A replay after a playlist item switch: new scene start, same wall instant → same event position.
        var e = At(t0: 11_000);
        long wall = 13_250;
        Assert.Equal(2250, ShowEventMath.LocalMs(e, sceneStart, wall - sceneStart));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(3999, true)]
    [InlineData(4000, false)]
    public void IsActive_HalfOpenWindow(long local, bool expected) => Assert.Equal(expected, ShowEventMath.IsActive(At(0), local));

    [Theory]
    [InlineData(0, 250)]
    [InlineData(-5, 250)]
    [InlineData(100, 250)]
    [InlineData(5000, 5000)]
    [InlineData(int.MaxValue, 600_000)]
    public void Duration_Clamped(int input, int expected) => Assert.Equal(expected, ShowEventMath.ClampDuration(input));

    [Fact]
    public void Ended_LateJoinerSeesExpiredEventAsEnded()
    {
        var e = At(t0: 1000, duration: 2000);
        Assert.False(ShowEventMath.Ended(e, 2999));
        Assert.True(ShowEventMath.Ended(e, 3000));
        Assert.False(ShowEventMath.Ended(e, 0));         // T0 in the future: not ended
    }

    [Theory]
    [InlineData(-1, 0f)]
    [InlineData(0, 0f)]
    [InlineData(125, 0.5f)]
    [InlineData(250, 1f)]
    [InlineData(2000, 1f)]
    [InlineData(3875, 0.5f)]
    [InlineData(4000, 0f)]
    public void Envelope_FadesInAndOut(long local, float expected) =>
        Assert.Equal(expected, ShowEventMath.Envelope(local, 4000), 3);

    [Fact]
    public void Envelope_ShortEvent_FadeIsAtMostHalf() =>
        Assert.Equal(1f, ShowEventMath.Envelope(125, 250), 3);   // 250 ms event: 125 ms in, 125 ms out

    [Fact]
    public void BlackoutOpacity_IsTheEnvelope() =>
        Assert.Equal(ShowEventMath.Envelope(1000, 3000), ShowEventMath.BlackoutOpacity(At(0, 3000), 1000));

    [Fact]
    public void ParseColor_Invalid_IsWhite() => Assert.Equal(((byte)255, (byte)255, (byte)255), ShowEventMath.ParseColor("red"));

    [Fact]
    public void Sin_FoldsLongUptimes()
    {
        long forty = 40L * 24 * 3600 * 1000;   // 40 days
        Assert.Equal(ShowEventMath.Sin(250, 1.0), ShowEventMath.Sin(forty + 250, 1.0), 6);
    }
}
```

```csharp
// WaBiBaBuSy.Tests/ShowEventLogTests.cs
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventLogTests
{
    private static ShowEvent E(string id, long t0, int duration = 1000) => new() { EventId = id, T0Utc = t0, DurationMs = duration };

    [Fact]
    public void Add_SameEventId_IsIgnored()
    {
        var log = new ShowEventLog();
        Assert.True(log.Add(E("a", 0)));
        Assert.False(log.Add(E("a", 500)));
        Assert.Equal(1, log.Count);
    }

    [Fact]
    public void Add_EmptyId_IsRejected() => Assert.False(new ShowEventLog().Add(E("", 0)));

    [Fact]
    public void Add_BeyondCapacity_DropsTheOldest()
    {
        var log = new ShowEventLog();
        for (int i = 0; i < ShowEventLog.Capacity + 3; i++) log.Add(E($"e{i}", i));
        Assert.Equal(ShowEventLog.Capacity, log.Count);
        Assert.False(log.Contains("e0"));
        Assert.True(log.Contains($"e{ShowEventLog.Capacity + 2}"));
    }

    [Fact]
    public void Active_ExcludesEnded_IncludesFuture()
    {
        var log = new ShowEventLog();
        log.Add(E("past", 0, 1000));      // ended at 1000
        log.Add(E("now", 4500, 1000));
        log.Add(E("future", 9000, 1000));
        var ids = log.Active(5000).Select(e => e.EventId).ToList();
        Assert.Equal(new[] { "now", "future" }, ids);
    }

    [Fact]
    public void Prune_KeepsEndedEventsForAMinute()
    {
        var log = new ShowEventLog();
        log.Add(E("a", 0, 1000));
        Assert.Equal(0, log.Prune(1000 + ShowEventLog.KeepAfterEndMs - 1));
        Assert.Equal(1, log.Prune(1000 + ShowEventLog.KeepAfterEndMs));
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void CopyTo_FillsTheBufferInOrder()
    {
        var log = new ShowEventLog();
        log.Add(E("a", 0)); log.Add(E("b", 0));
        var buffer = new ShowEvent[ShowEventLog.Capacity];
        Assert.Equal(2, log.CopyTo(buffer));
        Assert.Equal("a", buffer[0].EventId);
        Assert.Equal("b", buffer[1].EventId);
    }

    [Fact]
    public void Clear_Empties()
    {
        var log = new ShowEventLog();
        log.Add(E("a", 0));
        log.Clear();
        Assert.Equal(0, log.Count);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventTimingTests|FullyQualifiedName~ShowEventLogTests"`
Expected: build error — `ShowEventMath` / `ShowEventLog` do not exist.

- [ ] **Step 3: Implement**

```csharp
// WaBiBaBuSy.Models/Show/ShowEventMath.cs
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>
/// Pure timing of one-shot show events, shared by the D2D players and the room preview. An event's local time
/// is derived from the scene clock, so a live frame and a test-mode exact frame at the same elapsed agree.
/// </summary>
public static class ShowEventMath
{
    public const int MinDurationMs = 250;
    public const int MaxDurationMs = 600_000;
    public const int FadeMs = 250;

    /// <summary><paramref name="durationMs"/> clamped to 250 ms … 10 min.</summary>
    public static int ClampDuration(int durationMs) => Math.Clamp(durationMs, MinDurationMs, MaxDurationMs);

    /// <summary>
    /// Milliseconds since the event's T0 at a frame of the scene clock: <paramref name="sceneRawElapsedMs"/> is the
    /// un-phased scene elapsed (live: now − scene start; exact frame: the probe's elapsed). Wave-mode phase does not apply.
    /// </summary>
    public static long LocalMs(ShowEvent evt, long sceneStartUtcMs, long sceneRawElapsedMs) =>
        sceneRawElapsedMs + sceneStartUtcMs - evt.T0Utc;

    /// <summary>True inside [T0, T0 + duration).</summary>
    public static bool IsActive(ShowEvent evt, long localMs) => localMs >= 0 && localMs < ClampDuration(evt.DurationMs);

    /// <summary>True once the event is over at wall time <paramref name="nowUtcMs"/> (a late joiner does not draw it).</summary>
    public static bool Ended(ShowEvent evt, long nowUtcMs) => nowUtcMs >= evt.T0Utc + ClampDuration(evt.DurationMs);

    /// <summary>0 outside the event, 0→1 over the first <paramref name="fadeMs"/>, 1→0 over the last (at most half the duration each).</summary>
    public static float Envelope(long localMs, int durationMs, int fadeMs = FadeMs)
    {
        int d = ClampDuration(durationMs);
        if (localMs < 0 || localMs >= d) return 0f;
        int f = Math.Min(fadeMs, d / 2);
        if (f <= 0) return 1f;
        float rise = Math.Min(1f, localMs / (float)f);
        float fall = Math.Min(1f, (d - localMs) / (float)f);
        return Math.Min(rise, fall);
    }

    /// <summary>Blackout fill opacity.</summary>
    public static float BlackoutOpacity(ShowEvent evt, long localMs) => Envelope(localMs, evt.DurationMs);

    /// <summary>"#RRGGBB" as RGB; white when invalid. Never throws (parsed on every frame).</summary>
    public static (byte R, byte G, byte B) ParseColor(string? hex) => SpriteEffects.ParseBezelColor(hex);

    /// <summary>sin(2π·hz·t), with t folded into one period in double first (exact after days of uptime).</summary>
    public static double Sin(long ms, double hz)
    {
        if (hz <= 0) return 0;
        double period = 1000.0 / hz;
        double t = ms % period;
        if (t < 0) t += period;
        return Math.Sin(2.0 * Math.PI * t / period);
    }
}
```

```csharp
// WaBiBaBuSy.Models/Show/ShowEventLog.cs
namespace WaBiBaBuSy.Models.Show;

/// <summary>
/// The events a host or player knows about, oldest first. Thread-safe. Ended events are kept for a minute (an
/// exact frame requested shortly after still finds them; drawing skips them by time), then pruned.
/// </summary>
public sealed class ShowEventLog
{
    /// <summary>Most events kept; the oldest is dropped beyond it.</summary>
    public const int Capacity = 16;

    /// <summary>How long an ended event stays in the log.</summary>
    public const long KeepAfterEndMs = 60_000;

    private readonly List<ShowEvent> _events = new();
    private readonly object _gate = new();

    public int Count { get { lock (_gate) return _events.Count; } }

    /// <summary>Add <paramref name="evt"/>; false when its id is empty or already known (a replay).</summary>
    public bool Add(ShowEvent evt)
    {
        if (string.IsNullOrEmpty(evt.EventId)) return false;
        lock (_gate)
        {
            if (_events.Any(e => e.EventId == evt.EventId)) return false;
            _events.Add(evt);
            while (_events.Count > Capacity) _events.RemoveAt(0);
            return true;
        }
    }

    public bool Contains(string eventId) { lock (_gate) return _events.Any(e => e.EventId == eventId); }

    /// <summary>Events not yet ended at <paramref name="nowUtcMs"/>, including ones whose T0 is still ahead.</summary>
    public IReadOnlyList<ShowEvent> Active(long nowUtcMs)
    {
        lock (_gate) return _events.Where(e => !ShowEventMath.Ended(e, nowUtcMs)).ToList();
    }

    /// <summary>Drop events that ended at least <see cref="KeepAfterEndMs"/> ago; returns how many.</summary>
    public int Prune(long nowUtcMs)
    {
        lock (_gate)
            return _events.RemoveAll(e => nowUtcMs >= e.T0Utc + ShowEventMath.ClampDuration(e.DurationMs) + KeepAfterEndMs);
    }

    /// <summary>Copy the events into <paramref name="target"/> (no allocation; for the render loop); returns how many.</summary>
    public int CopyTo(ShowEvent[] target)
    {
        lock (_gate)
        {
            int n = Math.Min(target.Length, _events.Count);
            for (int i = 0; i < n; i++) target[i] = _events[i];
            return n;
        }
    }

    public void Clear() { lock (_gate) _events.Clear(); }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventTimingTests|FullyQualifiedName~ShowEventLogTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Show/ShowEventMath.cs WaBiBaBuSy.Models/Show/ShowEventLog.cs WaBiBaBuSy.Tests/ShowEventTimingTests.cs WaBiBaBuSy.Tests/ShowEventLogTests.cs
git commit -m "feat(events): event timing (local ms, envelope, expiry) and event log

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Spotlight math

**Files:**
- Create: `WaBiBaBuSy.Models/Show/SpotlightMath.cs`
- Test: `WaBiBaBuSy.Tests/SpotlightMathTests.cs`

**Interfaces:**
- Consumes: `ShowEventMath.Envelope`, `ShowEventMath.Sin`, `NodeLayout`, `NodeMapping.PositiveModulo`.
- Produces: `readonly record struct SpotlightVisual(bool IsTarget, float DimOpacity, float BorderOpacity, float BorderWidth, int ChevronDirection, float ChevronCenterX, float ChevronCenterY, float ChevronSize, float ChevronOpacity)`; `SpotlightMath.{BorderWidth=16, DimMax=0.6, PulseHz=1.5, ChevronSlide=40, ChevronHz=1, ChevronHeightFraction=0.18}`, `SpotlightVisual Visual(ShowEvent, NodeLayout, bool sharedCanvas, long localMs)`, `int ChevronDirection(ShowEvent, NodeLayout, bool sharedCanvas)`. All in node-local canvas units; `ChevronDirection` is screen-space (+1 = points right on this screen).

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/SpotlightMathTests.cs
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class SpotlightMathTests
{
    private static NodeLayout Node(int order, int offsetX, bool mirrored = false, bool wraps = false, int canvasW = 10_000) => new()
    {
        Id = $"n{order}", Order = order, OffsetX = offsetX, Width = 1000, Height = 500,
        CanvasWidth = canvasW, CanvasHeight = 500, Mirrored = mirrored, Wraps = wraps,
    };

    private static ShowEvent Spot(int targetOrder, float targetCenterX) => new()
    {
        EventId = "s", Type = ShowEventType.Spotlight, DurationMs = 8000, TargetOrder = targetOrder, TargetCenterX = targetCenterX,
    };

    [Fact]
    public void Target_GetsAPulsingBorder_NoDim()
    {
        var v = SpotlightMath.Visual(Spot(2, 2500), Node(2, 2000), sharedCanvas: true, localMs: 4000);
        Assert.True(v.IsTarget);
        Assert.Equal(0f, v.DimOpacity);
        Assert.Equal(SpotlightMath.BorderWidth, v.BorderWidth);
        Assert.InRange(v.BorderOpacity, 0.2f, 1f);
    }

    [Fact]
    public void Others_AreDimmedTo40Percent_WithAChevron()
    {
        var v = SpotlightMath.Visual(Spot(2, 2500), Node(0, 0), sharedCanvas: true, localMs: 4000);
        Assert.False(v.IsTarget);
        Assert.Equal(0.6f, v.DimOpacity, 3);
        Assert.Equal(1, v.ChevronDirection);                    // target is to the right
        Assert.Equal(250f, v.ChevronCenterY, 3);
        Assert.Equal(0.18f * 500, v.ChevronSize, 3);
        Assert.InRange(v.ChevronCenterX, 500f - 41f, 500f + 41f);
    }

    [Fact]
    public void Chevron_IsFlippedOnMirroredNodes() =>
        Assert.Equal(-1, SpotlightMath.ChevronDirection(Spot(2, 2500), Node(0, 0, mirrored: true), sharedCanvas: true));

    [Fact]
    public void Chevron_PointsLeftWhenTheTargetIsBehind() =>
        Assert.Equal(-1, SpotlightMath.ChevronDirection(Spot(0, 500), Node(5, 5000), sharedCanvas: true));

    [Fact]
    public void Chevron_OnARing_TakesTheShorterWayAcrossTheSeam()
    {
        // Node near the end of the ring (center 9500), target near the start (500): 1000 to the right across the seam.
        Assert.Equal(1, SpotlightMath.ChevronDirection(Spot(0, 500), Node(9, 9000, wraps: true), sharedCanvas: true));
        Assert.Equal(-1, SpotlightMath.ChevronDirection(Spot(0, 500), Node(9, 9000, wraps: false), sharedCanvas: true));
    }

    [Fact]
    public void Chevron_WithoutASharedCanvas_ComparesOrders() =>
        Assert.Equal(1, SpotlightMath.ChevronDirection(Spot(4, 0), Node(1, 0), sharedCanvas: false));

    [Theory]
    [InlineData(-1L)]
    [InlineData(8000L)]
    public void OutsideTheEvent_DrawsNothing(long local) =>
        Assert.Equal(default, SpotlightMath.Visual(Spot(2, 2500), Node(0, 0), true, local));

    [Fact]
    public void NoTarget_DrawsNothing() =>
        Assert.Equal(default, SpotlightMath.Visual(Spot(-1, 0), Node(0, 0), true, 4000));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SpotlightMathTests"`
Expected: build error — `SpotlightMath` does not exist.

- [ ] **Step 3: Implement**

```csharp
// WaBiBaBuSy.Models/Show/SpotlightMath.cs
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>What one node draws for a Spotlight, in node-local canvas units. Default = nothing.</summary>
/// <param name="ChevronDirection">+1 = points right on this screen, -1 = left, 0 = no chevron.</param>
public readonly record struct SpotlightVisual(
    bool IsTarget, float DimOpacity, float BorderOpacity, float BorderWidth,
    int ChevronDirection, float ChevronCenterX, float ChevronCenterY, float ChevronSize, float ChevronOpacity);

/// <summary>
/// Spotlight (design §3.2): the target seat's border pulses in the event color; every other seat dims to 40 % and
/// shows a chevron pointing along the traversal toward the target.
/// </summary>
public static class SpotlightMath
{
    public const float BorderWidth = 16f;
    public const float DimMax = 0.6f;
    public const double PulseHz = 1.5;
    public const float ChevronSlide = 40f;
    public const double ChevronHz = 1.0;
    public const float ChevronHeightFraction = 0.18f;

    /// <param name="sharedCanvas">Sequential (one canvas across the room); false in Simultaneous mode.</param>
    public static SpotlightVisual Visual(ShowEvent evt, NodeLayout node, bool sharedCanvas, long localMs)
    {
        if (evt.TargetOrder < 0) return default;
        float env = ShowEventMath.Envelope(localMs, evt.DurationMs);
        if (env <= 0f) return default;

        if (node.Order == evt.TargetOrder)
        {
            float pulse = 0.6f + 0.4f * (float)ShowEventMath.Sin(localMs, PulseHz);
            return new SpotlightVisual(true, 0f, env * pulse, BorderWidth, 0, 0f, 0f, 0f, 0f);
        }

        int dir = ChevronDirection(evt, node, sharedCanvas);
        float slide = ChevronSlide * (float)ShowEventMath.Sin(localMs, ChevronHz) * dir;
        return new SpotlightVisual(false, DimMax * env, 0f, 0f, dir,
            node.Width / 2f + slide, node.Height / 2f, ChevronHeightFraction * node.Height, env);
    }

    /// <summary>
    /// Screen direction toward the target: along the shared canvas (the shorter way around on a Ring), or by
    /// traversal order without a shared canvas; negated on mirrored nodes.
    /// </summary>
    public static int ChevronDirection(ShowEvent evt, NodeLayout node, bool sharedCanvas)
    {
        float delta;
        if (sharedCanvas && node.CanvasWidth > 0)
        {
            delta = evt.TargetCenterX - (node.OffsetX + node.Width / 2f);
            if (node.Wraps)
            {
                float w = node.CanvasWidth;
                delta = NodeMapping.PositiveModulo(delta + w / 2f, w) - w / 2f;
            }
        }
        else
        {
            delta = evt.TargetOrder - node.Order;
        }
        int dir = delta >= 0f ? 1 : -1;
        return node.Mirrored ? -dir : dir;
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~SpotlightMathTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Show/SpotlightMath.cs WaBiBaBuSy.Tests/SpotlightMathTests.cs
git commit -m "feat(events): spotlight math (target border, dim, chevron toward the target)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Celebration particle math

**Files:**
- Create: `WaBiBaBuSy.Models/Show/CelebrationMath.cs`
- Test: `WaBiBaBuSy.Tests/CelebrationMathTests.cs`

**Interfaces:**
- Consumes: `PatternLayout.Hash3(int seed, int i, int j)` (existing), `NodeMapping.ToLocalX/ToLocalY/PositiveModulo`, `ShowEvent`.
- Produces: `readonly record struct CelebrationParticle(int Index, float X, float Y, float Width, float Height, float RotationDeg, int ColorIndex)`; `CelebrationMath.{PerThousandPx, MinCount, MaxCount, Gravity, FallSpeed, MaxDriftSpeed, SwayAmplitude, MaxSize, MaxRiseS, Margin, LaunchSpreadMs, PartyColors}`, `int Count(int canvasWidth)`, `string ColorFor(ShowEvent, int colorIndex)`, `bool Particle(ShowEvent, int i, int count, int canvasW, int canvasH, long localMs, out CelebrationParticle p)` (virtual canvas coordinates), `int Capacity(NodeLayout, int count)`, `int VisibleOnNode(ShowEvent, NodeLayout, long localMs, Span<CelebrationParticle> output)` (node-local coordinates).

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/CelebrationMathTests.cs
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class CelebrationMathTests
{
    private const int CanvasW = 4000, CanvasH = 1000;

    private static ShowEvent Burst(int seed = 1234) => new() { EventId = "c", Type = ShowEventType.Celebration, DurationMs = 5000, Seed = seed };

    private static NodeLayout Node(int offsetX, int width = 1000, bool wraps = false, bool mirrored = false) => new()
    {
        OffsetX = offsetX, Width = width, Height = CanvasH, CanvasWidth = CanvasW, CanvasHeight = CanvasH, Wraps = wraps, Mirrored = mirrored,
    };

    private static List<CelebrationParticle> Visible(ShowEvent e, NodeLayout node, long local)
    {
        var buffer = new CelebrationParticle[CelebrationMath.Capacity(node, CelebrationMath.Count(node.CanvasWidth))];
        int n = CelebrationMath.VisibleOnNode(e, node, local, buffer);
        return buffer.Take(n).ToList();
    }

    [Theory]
    [InlineData(1920, 480)]
    [InlineData(100, 50)]
    [InlineData(1_000_000, 6000)]
    public void Count_ScalesWithCanvasWidth(int width, int expected) => Assert.Equal(expected, CelebrationMath.Count(width));

    [Theory]
    [InlineData(1500L)]
    [InlineData(4000L)]
    public void TwoNodes_SeeIdenticalVirtualPositions(long local)
    {
        var e = Burst();
        NodeLayout a = Node(0), b = Node(1000, mirrored: true);
        var onA = Visible(e, a, local).ToDictionary(p => p.Index);
        var onB = Visible(e, b, local).ToDictionary(p => p.Index);
        var shared = onA.Keys.Intersect(onB.Keys).ToList();
        Assert.NotEmpty(shared);                       // particles straddling the bezel are drawn by both machines
        foreach (int i in shared)
        {
            Assert.Equal(NodeMapping.ToVirtualX(onA[i].X, onA[i].Width, a), NodeMapping.ToVirtualX(onB[i].X, onB[i].Width, b), 2);
            Assert.Equal(onA[i].Y, onB[i].Y, 3);
            Assert.Equal(onA[i].RotationDeg, onB[i].RotationDeg, 3);
            Assert.Equal(onA[i].ColorIndex, onB[i].ColorIndex);
        }
    }

    [Theory]
    [InlineData(500L, false)]
    [InlineData(2500L, false)]
    [InlineData(2500L, true)]
    public void VisibleOnNode_MissesNoParticleThatTouchesTheNode(long local, bool wraps)
    {
        var e = Burst(seed: 77);
        var node = Node(wraps ? 0 : 1500, wraps: wraps);
        int n = CelebrationMath.Count(CanvasW);
        var seen = Visible(e, node, local).Select(p => p.Index).ToHashSet();
        for (int i = 0; i < n; i++)
        {
            if (!CelebrationMath.Particle(e, i, n, CanvasW, CanvasH, local, out var p)) continue;
            foreach (int k in wraps ? new[] { -1, 0, 1 } : new[] { 0 })
            {
                float x = p.X + k * CanvasW;
                bool touches = x + p.Width > node.OffsetX && x < node.OffsetX + node.Width && p.Y + p.Height > 0 && p.Y < CanvasH;
                if (touches) Assert.Contains(i, seen);
            }
        }
    }

    [Fact]
    public void DifferentSeed_DifferentBurst()
    {
        int n = CelebrationMath.Count(CanvasW);
        CelebrationMath.Particle(Burst(1), 10, n, CanvasW, CanvasH, 1500, out var a);
        CelebrationMath.Particle(Burst(2), 10, n, CanvasW, CanvasH, 1500, out var b);
        Assert.NotEqual(a.X, b.X);
    }

    [Fact]
    public void SameSeed_IsRepeatable()
    {
        int n = CelebrationMath.Count(CanvasW);
        CelebrationMath.Particle(Burst(), 10, n, CanvasW, CanvasH, 1500, out var a);
        CelebrationMath.Particle(Burst(), 10, n, CanvasW, CanvasH, 1500, out var b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void BeforeLaunch_ParticleIsNotThere() =>
        Assert.False(CelebrationMath.Particle(Burst(), 0, 100, CanvasW, CanvasH, -1, out _));

    [Fact]
    public void Particles_RiseIntoTheScreenThenFall()
    {
        var e = Burst();
        int n = CelebrationMath.Count(CanvasW);
        int risen = 0;
        for (int i = 0; i < n; i++)
            if (CelebrationMath.Particle(e, i, n, CanvasW, CanvasH, 1800, out var p) && p.Y < CanvasH * 0.6f) risen++;
        Assert.True(risen > n / 2, $"only {risen}/{n} particles are in the upper part of the screen at 1.8 s");
    }

    [Fact]
    public void Capacity_IsBoundedForHugeRooms()
    {
        var node = new NodeLayout { OffsetX = 0, Width = 3840, Height = 2160, CanvasWidth = 20 * 3840, CanvasHeight = 2160 };
        int count = CelebrationMath.Count(node.CanvasWidth);
        Assert.Equal(CelebrationMath.MaxCount, count);
        Assert.InRange(CelebrationMath.Capacity(node, count), 1, 800);
    }

    [Fact]
    public void ColorFor_ZeroIsTheEventColor()
    {
        var e = Burst(); e.ColorHex = "#123456";
        Assert.Equal("#123456", CelebrationMath.ColorFor(e, 0));
        Assert.Equal(CelebrationMath.PartyColors[0], CelebrationMath.ColorFor(e, 1));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~CelebrationMathTests"`
Expected: build error — `CelebrationMath` does not exist.

- [ ] **Step 3: Implement**

```csharp
// WaBiBaBuSy.Models/Show/CelebrationMath.cs
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>One confetti piece: top-left, size and rotation (degrees, about its center), color slot (0 = event color).</summary>
public readonly record struct CelebrationParticle(int Index, float X, float Y, float Width, float Height, float RotationDeg, int ColorIndex);

/// <summary>
/// Celebration (design §3.3): a seeded, closed-form confetti burst over the whole canvas. Particle i is launched from
/// below the bottom edge at x0 = (i + u)/N × CanvasW, rises ballistically to its apex, then drifts down slowly with a
/// sway. Particles are sorted by x0, so a node only evaluates the index range covering its slice.
/// </summary>
public static class CelebrationMath
{
    public const float PerThousandPx = 250f;
    public const int MinCount = 50;
    public const int MaxCount = 6000;
    /// <summary>× canvas height per s².</summary>
    public const float Gravity = 2.0f;
    /// <summary>× canvas height per s after the apex.</summary>
    public const float FallSpeed = 0.15f;
    /// <summary>Horizontal drift during the rise, ± units/s.</summary>
    public const float MaxDriftSpeed = 150f;
    public const float SwayAmplitude = 30f;
    public const double SwayHz = 0.8;
    public const float MaxSize = 18f;
    /// <summary>Longest rise: apex ≤ 0.95 H at g = 2 H/s² → ≤ 0.975 s.</summary>
    public const float MaxRiseS = 1.0f;
    /// <summary>How far a particle can end up from its x0 (drift + sway range + size).</summary>
    public const float Margin = MaxDriftSpeed * MaxRiseS + 2f * SwayAmplitude + MaxSize;
    public const int LaunchSpreadMs = 800;
    public static readonly string[] PartyColors = { "#FF4136", "#2ECC40", "#0074D9", "#FFDC00", "#F012BE" };

    public static int Count(int canvasWidth) =>
        Math.Clamp((int)Math.Ceiling(Math.Max(1, canvasWidth) / 1000.0 * PerThousandPx), MinCount, MaxCount);

    /// <summary>Color of a slot: 0 = the event color, 1..5 = the party colors.</summary>
    public static string ColorFor(ShowEvent evt, int colorIndex) =>
        colorIndex <= 0 ? evt.ColorHex : PartyColors[(colorIndex - 1) % PartyColors.Length];

    private static double U(int seed, int i, int k) => (uint)PatternLayout.Hash3(seed, i, k) / 4294967296.0;   // [0, 1)

    /// <summary>Particle <paramref name="i"/> of <paramref name="count"/> in virtual canvas coordinates; false before its launch.</summary>
    public static bool Particle(ShowEvent evt, int i, int count, int canvasW, int canvasH, long localMs, out CelebrationParticle p)
    {
        p = default;
        int seed = evt.Seed;
        double t = (localMs - U(seed, i, 3) * LaunchSpreadMs) / 1000.0;
        if (t < 0) return false;

        double h = Math.Max(1, canvasH);
        double x0 = (i + U(seed, i, 0)) / Math.Max(1, count) * canvasW;
        double y0 = h + 20.0;
        double g = Gravity * h;
        double apex = (0.4 + 0.55 * U(seed, i, 1)) * h;
        double vy0 = Math.Sqrt(2.0 * g * apex);
        double tUp = vy0 / g;
        double vx = (U(seed, i, 2) - 0.5) * 2.0 * MaxDriftSpeed;

        double x, y;
        if (t <= tUp)
        {
            x = x0 + vx * t;
            y = y0 - vy0 * t + 0.5 * g * t * t;
        }
        else
        {
            double tf = t - tUp;
            double phase = U(seed, i, 7) * 2.0 * Math.PI;
            x = x0 + vx * tUp + SwayAmplitude * (Math.Sin(2.0 * Math.PI * SwayHz * tf + phase) - Math.Sin(phase));
            y = y0 - apex + FallSpeed * h * tf;
        }

        float size = (float)(8.0 + 10.0 * U(seed, i, 5));
        double spinDegPerS = (U(seed, i, 6) - 0.5) * 720.0;
        double rot = (U(seed, i, 6) * 360.0 + spinDegPerS * t) % 360.0;
        if (rot < 0) rot += 360.0;
        int color = Math.Min(PartyColors.Length, (int)(U(seed, i, 4) * (PartyColors.Length + 1)));
        p = new CelebrationParticle(i, (float)x - size / 2f, (float)y - size * 0.3f, size, size * 0.6f, (float)rot, color);
        return true;
    }

    /// <summary>Upper bound of <see cref="VisibleOnNode"/>'s output for this node (size of the caller's buffer).</summary>
    public static int Capacity(NodeLayout node, int count)
    {
        int cw = node.CanvasWidth > 0 ? node.CanvasWidth : Math.Max(1, node.Width);
        return Math.Min(count, (int)Math.Ceiling((node.Width + 2 * Margin) / cw * count) + 3);
    }

    /// <summary>
    /// The particles that can touch <paramref name="node"/>, in node-local coordinates (X mirrored on mirrored nodes);
    /// returns how many were written. Wrapping (Ring) layouts take the copy of each particle nearest the node.
    /// A node (plus margin) wider than the whole ring evaluates every particle once.
    /// </summary>
    public static int VisibleOnNode(ShowEvent evt, NodeLayout node, long localMs, Span<CelebrationParticle> output)
    {
        int cw = node.CanvasWidth > 0 ? node.CanvasWidth : Math.Max(1, node.Width);
        int ch = node.CanvasHeight > 0 ? node.CanvasHeight : Math.Max(1, node.Height);
        int n = Count(cw);
        double lo = node.OffsetX - Margin, hi = node.OffsetX + node.Width + Margin;
        int jLo = (int)Math.Floor(lo / cw * n) - 1;
        int jHi = (int)Math.Ceiling(hi / cw * n) + 1;
        if (!node.Wraps)
        {
            jLo = Math.Max(jLo, 0);
            jHi = Math.Min(jHi, n - 1);
        }
        else if (jHi - jLo + 1 > n)
        {
            jLo = 0;
            jHi = n - 1;
        }

        int written = 0;
        for (int j = jLo; j <= jHi && written < output.Length; j++)
        {
            int i = node.Wraps ? (int)(((long)j % n + n) % n) : j;
            if (!Particle(evt, i, n, cw, ch, localMs, out var p)) continue;
            float vx = p.X;
            if (node.Wraps) vx += (float)Math.Floor((double)j / n) * cw;   // j = i + k·n → the copy shifted by k·cw
            float lx = NodeMapping.ToLocalX(vx, p.Width, node);
            float ly = NodeMapping.ToLocalY(p.Y, node);
            float r = Math.Max(p.Width, p.Height);                          // rotation slack
            if (lx + r < 0f || lx - r > node.Width || ly + r < 0f || ly - r > node.Height) continue;
            output[written++] = p with { X = lx, Y = ly };
        }
        return written;
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~CelebrationMathTests"`
Expected: PASS. If `VisibleOnNode_MissesNoParticleThatTouchesTheNode` fails, widen the index range (`Margin`), never the test.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Show/CelebrationMath.cs WaBiBaBuSy.Tests/CelebrationMathTests.cs
git commit -m "feat(events): closed-form seeded celebration burst, per-node index range

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Traversal math (also used by Announce)

**Files:**
- Create: `WaBiBaBuSy.Models/Show/TraversalMath.cs`
- Test: `WaBiBaBuSy.Tests/TraversalMathTests.cs`

**Interfaces:**
- Consumes: `ShowEventMath.IsActive/ClampDuration/Envelope`.
- Produces: `readonly record struct TraversalPose(float VirtualX, float VirtualY, float Width, float Height, float Opacity)`; `TraversalMath.{MinDurationMs=2000, MaxDurationMs=120_000, DefaultSpeedPxPerSecond=600}`, `(float W, float H) SpriteSize(ShowEvent, int canvasH)`, `int DurationMs(int canvasW, float spriteW, bool wraps, float pxPerSecond)`, `TraversalPose? Pose(ShowEvent, int canvasW, int canvasH, bool wraps, long localMs)` (virtual canvas; the caller wraps with `NodeMapping.WrapCopies`).

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/TraversalMathTests.cs
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class TraversalMathTests
{
    private const int W = 6000, H = 1000;

    private static ShowEvent Lap(bool reverse = false, int duration = 10_000) => new()
    {
        EventId = "t", Type = ShowEventType.Traversal, DurationMs = duration, HeightFraction = 0.5f, SpriteAspect = 1.2f, Reverse = reverse,
    };

    [Fact]
    public void SpriteSize_FromHeightFractionAndAspect()
    {
        var (w, h) = TraversalMath.SpriteSize(Lap(), H);
        Assert.Equal(500f, h, 3);
        Assert.Equal(600f, w, 3);
    }

    [Theory]
    [InlineData(50f, float.NaN, 1000f, 1000f)]
    [InlineData(0f, 0f, 50f, 50f)]
    [InlineData(0.5f, 1000f, 500f * 20f, 500f)]
    public void SpriteSize_ClampsAbsurdValues(float fraction, float aspect, float expectedW, float expectedH)
    {
        var e = Lap(); e.HeightFraction = fraction; e.SpriteAspect = aspect;
        var (w, h) = TraversalMath.SpriteSize(e, H);
        Assert.Equal(expectedH, h, 3);
        Assert.Equal(expectedW, w, 3);
    }

    [Fact]
    public void NonWrapping_StartsAndEndsOffCanvas()
    {
        var start = TraversalMath.Pose(Lap(), W, H, wraps: false, localMs: 0)!.Value;
        var end = TraversalMath.Pose(Lap(), W, H, wraps: false, localMs: 9999)!.Value;
        Assert.True(start.VirtualX + start.Width <= 0.01f);
        Assert.True(end.VirtualX >= W - 2f);
        Assert.Equal((H - start.Height) / 2f, start.VirtualY, 3);
    }

    [Fact]
    public void Ring_RunsExactlyOneLap()
    {
        var start = TraversalMath.Pose(Lap(), W, H, wraps: true, localMs: 0)!.Value;
        var end = TraversalMath.Pose(Lap(), W, H, wraps: true, localMs: 9999)!.Value;
        Assert.InRange(end.VirtualX - start.VirtualX, W - 2f, W);   // one full canvas width further (within the last frame)
    }

    [Fact]
    public void Reverse_MirrorsThePath()
    {
        var fwd = TraversalMath.Pose(Lap(), W, H, false, 3000)!.Value;
        var rev = TraversalMath.Pose(Lap(reverse: true), W, H, false, 3000)!.Value;
        Assert.Equal(W - fwd.Width - fwd.VirtualX, rev.VirtualX, 2);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(10_000L)]
    public void OutsideTheEvent_NoPose(long local) => Assert.Null(TraversalMath.Pose(Lap(), W, H, false, local));

    [Theory]
    [InlineData(6000, 600f, false, 600f, 11_000)]
    [InlineData(6000, 600f, true, 600f, 10_000)]
    [InlineData(100, 10f, false, 600f, 2000)]
    [InlineData(1_000_000, 10f, false, 600f, 120_000)]
    [InlineData(6000, 600f, false, 0f, 11_000)]           // speed 0 → default 600 px/s
    public void DurationMs_OneLap(int canvasW, float spriteW, bool wraps, float speed, int expected) =>
        Assert.Equal(expected, TraversalMath.DurationMs(canvasW, spriteW, wraps, speed));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~TraversalMathTests"`
Expected: build error — `TraversalMath` does not exist.

- [ ] **Step 3: Implement**

```csharp
// WaBiBaBuSy.Models/Show/TraversalMath.cs
namespace WaBiBaBuSy.Models.Show;

/// <summary>Where the extra sprite of a Traversal / Announce is, in virtual canvas units.</summary>
public readonly record struct TraversalPose(float VirtualX, float VirtualY, float Width, float Height, float Opacity);

/// <summary>
/// Traversal and Announce (design §3.4, §3.6): one temporary sprite that runs exactly one lap from T0 and despawns.
/// The size comes from the event (height fraction × canvas height, × the server-measured aspect), never from the
/// decoded bitmap, so every node computes the same box.
/// </summary>
public static class TraversalMath
{
    public const int MinDurationMs = 2000;
    public const int MaxDurationMs = 120_000;
    public const float DefaultSpeedPxPerSecond = 600f;

    public static (float W, float H) SpriteSize(ShowEvent evt, int canvasH)
    {
        float fraction = float.IsFinite(evt.HeightFraction) ? Math.Clamp(evt.HeightFraction, 0.05f, 1f) : 0.5f;
        float aspect = float.IsFinite(evt.SpriteAspect) && evt.SpriteAspect > 0.01f ? Math.Min(evt.SpriteAspect, 20f) : 1f;
        float h = fraction * Math.Max(1, canvasH);
        return (h * aspect, h);
    }

    /// <summary>Duration of one lap at <paramref name="pxPerSecond"/> (canvas units/s; ≤ 0 → 600), clamped to 2 s … 120 s.</summary>
    public static int DurationMs(int canvasW, float spriteW, bool wraps, float pxPerSecond)
    {
        if (!float.IsFinite(pxPerSecond) || pxPerSecond <= 0f) pxPerSecond = DefaultSpeedPxPerSecond;
        double distance = wraps ? canvasW : canvasW + Math.Max(0f, spriteW);
        return (int)Math.Clamp(Math.Round(distance * 1000.0 / pxPerSecond), MinDurationMs, MaxDurationMs);
    }

    /// <summary>
    /// Pose at <paramref name="localMs"/>; null outside the event. Non-wrapping: enters fully off the left edge and
    /// leaves fully off the right. Ring: starts just before the seam and travels one canvas width. Reverse mirrors it.
    /// </summary>
    public static TraversalPose? Pose(ShowEvent evt, int canvasW, int canvasH, bool wraps, long localMs)
    {
        if (!ShowEventMath.IsActive(evt, localMs)) return null;
        var (w, h) = SpriteSize(evt, canvasH);
        double p = localMs / (double)ShowEventMath.ClampDuration(evt.DurationMs);
        double x = wraps ? -w + p * canvasW : -w + p * (canvasW + w);
        if (evt.Reverse) x = canvasW - w - x;
        return new TraversalPose((float)x, (Math.Max(1, canvasH) - h) / 2f, w, h, ShowEventMath.Envelope(localMs, evt.DurationMs));
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~TraversalMathTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Show/TraversalMath.cs WaBiBaBuSy.Tests/TraversalMathTests.cs
git commit -m "feat(events): traversal pose (one lap, ring, reverse) and lap duration

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Event factory

**Files:**
- Create: `WaBiBaBuSy.Models/Show/ShowEventFactory.cs`
- Test: `WaBiBaBuSy.Tests/ShowEventFactoryTests.cs`

**Interfaces:**
- Consumes: `ShowEventSettings`, `SeatMapLayoutResult` (`Nodes`, `Get(id)`, `CanvasWidth/Height`, `Wraps`), `SyncTiming.ComputeStartLeadMs(double, int)`, `TraversalMath`.
- Produces: `sealed record ShowEventRequest(ShowEventType Type, long NowUtcMs, double MaxRttMs, SeatMapLayoutResult? Layout, bool PerMonitor, string? TargetSeatId, int Seed, string EventId, string? ImagePath = null, float SpriteAspect = 1f, string? Text = null)`; `ShowEventFactory.Create(ShowEventRequest, ShowEventSettings, out string? error) : ShowEvent?`.

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/ShowEventFactoryTests.cs
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventFactoryTests
{
    private static readonly ShowEventSettings Settings = new();

    private static SeatMapLayoutResult Layout(bool wraps = false) => new()
    {
        CanvasWidth = 3000, CanvasHeight = 1000, Wraps = wraps,
        Nodes = new[]
        {
            new NodeLayout { Id = "a", Order = 0, OffsetX = 0, Width = 1000, Height = 1000 },
            new NodeLayout { Id = "b", Order = 1, OffsetX = 1000, Width = 1000, Height = 1000 },
            new NodeLayout { Id = "c", Order = 2, OffsetX = 2000, Width = 1400, Height = 900 },
        },
    };

    private static ShowEventRequest Req(ShowEventType type, string? seat = null, double rtt = 0, bool wraps = false, bool perMonitor = false,
        string? image = null, float aspect = 1f, string? text = null) =>
        new(type, NowUtcMs: 100_000, MaxRttMs: rtt, Layout(wraps), perMonitor, seat, Seed: 7, EventId: "id-1", image, aspect, text);

    [Theory]
    [InlineData(0.0, 800)]
    [InlineData(500.0, 1500)]
    [InlineData(5000.0, 4000)]
    public void T0_IsNowPlusTheStartLead(double rtt, int lead)
    {
        var e = ShowEventFactory.Create(Req(ShowEventType.Celebration, rtt: rtt), Settings, out _)!;
        Assert.Equal(100_000 + lead, e.T0Utc);
    }

    [Fact]
    public void CarriesIdAndSeed()
    {
        var e = ShowEventFactory.Create(Req(ShowEventType.Blackout), Settings, out _)!;
        Assert.Equal("id-1", e.EventId);
        Assert.Equal(7, e.Seed);
        Assert.Equal(3000, e.DurationMs);
        Assert.Equal("#000000", e.ColorHex);
    }

    [Fact]
    public void Spotlight_TargetsTheSeatsOrderAndCenter()
    {
        var e = ShowEventFactory.Create(Req(ShowEventType.Spotlight, seat: "b"), Settings, out var error)!;
        Assert.Null(error);
        Assert.Equal(1, e.TargetOrder);
        Assert.Equal(1500f, e.TargetCenterX);
        Assert.Equal(8000, e.DurationMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nope")]
    public void Spotlight_WithoutAKnownSeat_IsRefused(string? seat)
    {
        Assert.Null(ShowEventFactory.Create(Req(ShowEventType.Spotlight, seat: seat), Settings, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Traversal_LapDurationFromTheCanvas()
    {
        // 3000 wide canvas, sprite 500 high × aspect 2 = 1000 wide, 600 px/s: (3000 + 1000) / 600 = 6.667 s
        var e = ShowEventFactory.Create(Req(ShowEventType.Traversal, image: @"C:\logo.png", aspect: 2f), Settings, out _)!;
        Assert.Equal(6667, e.DurationMs);
        Assert.Equal(@"C:\logo.png", e.ImagePath);
        Assert.Equal(2f, e.SpriteAspect);
        Assert.Equal(0.5f, e.HeightFraction);
        var ring = ShowEventFactory.Create(Req(ShowEventType.Traversal, wraps: true, image: @"C:\logo.png", aspect: 2f), Settings, out _)!;
        Assert.Equal(5000, ring.DurationMs);
    }

    [Fact]
    public void Traversal_PerMonitor_UsesTheWidestNodeAndNeverWraps()
    {
        // widest node 1400, tallest 1000 → sprite 500 × 1 = 500 wide: (1400 + 500) / 600 = 3.167 s
        var e = ShowEventFactory.Create(Req(ShowEventType.Traversal, wraps: true, perMonitor: true, image: "x.png"), Settings, out _)!;
        Assert.Equal(3167, e.DurationMs);
    }

    [Fact]
    public void Traversal_WithoutImage_IsRefused()
    {
        Assert.Null(ShowEventFactory.Create(Req(ShowEventType.Traversal, image: " "), Settings, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Announce_WithoutText_IsRefused()
    {
        Assert.Null(ShowEventFactory.Create(Req(ShowEventType.Announce, text: ""), Settings, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Announce_CarriesTextAndAspect()
    {
        var e = ShowEventFactory.Create(Req(ShowEventType.Announce, text: "Round 3", aspect: 6f), Settings, out _)!;
        Assert.Equal("Round 3", e.Text);
        Assert.Equal(6f, e.SpriteAspect);
        Assert.Equal(0.25f, e.HeightFraction);
        Assert.Equal("#FFFFFF", e.ColorHex);
    }

    [Fact]
    public void Duration_IsClamped()
    {
        var s = new ShowEventSettings { CelebrationDurationMs = -1 };
        Assert.Equal(ShowEventMath.MinDurationMs, ShowEventFactory.Create(Req(ShowEventType.Celebration), s, out _)!.DurationMs);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventFactoryTests"`
Expected: build error — `ShowEventFactory` / `ShowEventRequest` do not exist.

- [ ] **Step 3: Implement**

```csharp
// WaBiBaBuSy.Models/Show/ShowEventFactory.cs
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.Models.Show;

/// <summary>Everything the server knows when an event is fired.</summary>
/// <param name="Layout">Layout of the running scene (its seat map).</param>
/// <param name="PerMonitor">Simultaneous distribution: every node's own monitor is its canvas.</param>
/// <param name="Seed">Drawn once by the server (broadcast — determinism holds).</param>
/// <param name="ImagePath">Traversal: the image file (the caller resolves "empty = scene sprite").</param>
/// <param name="SpriteAspect">Traversal: image width ÷ height; Announce: measured text aspect.</param>
public sealed record ShowEventRequest(
    ShowEventType Type, long NowUtcMs, double MaxRttMs, SeatMapLayoutResult? Layout, bool PerMonitor,
    string? TargetSeatId, int Seed, string EventId, string? ImagePath = null, float SpriteAspect = 1f, string? Text = null);

/// <summary>Builds a <see cref="ShowEvent"/> from a request and the trigger defaults (design §3.8). Pure.</summary>
public static class ShowEventFactory
{
    private const int FallbackCanvasW = 1920, FallbackCanvasH = 1080;

    /// <summary>The event, or null with <paramref name="error"/> (a sentence for the status line) when it cannot be built.</summary>
    public static ShowEvent? Create(ShowEventRequest r, ShowEventSettings s, out string? error)
    {
        error = null;
        var evt = new ShowEvent
        {
            EventId = r.EventId,
            Type = r.Type,
            T0Utc = r.NowUtcMs + SyncTiming.ComputeStartLeadMs(r.MaxRttMs, 0),
            Seed = r.Seed,
        };
        var (canvasW, canvasH, wraps) = Canvas(r);

        switch (r.Type)
        {
            case ShowEventType.Spotlight:
                var node = string.IsNullOrEmpty(r.TargetSeatId) ? null : r.Layout?.Get(r.TargetSeatId);
                if (node == null) { error = "Pick a seat for the spotlight."; return null; }
                evt.DurationMs = s.SpotlightDurationMs;
                evt.ColorHex = s.SpotlightColorHex;
                evt.TargetOrder = node.Order;
                evt.TargetCenterX = node.OffsetX + node.Width / 2f;
                break;

            case ShowEventType.Celebration:
                evt.DurationMs = s.CelebrationDurationMs;
                evt.ColorHex = s.CelebrationColorHex;
                break;

            case ShowEventType.Blackout:
                evt.DurationMs = s.BlackoutDurationMs;
                evt.ColorHex = s.BlackoutColorHex;
                break;

            case ShowEventType.Traversal:
                if (string.IsNullOrWhiteSpace(r.ImagePath)) { error = "No image for the traversal (pick one, or play a scene with a sprite)."; return null; }
                evt.ImagePath = r.ImagePath;
                evt.SpriteAspect = r.SpriteAspect;
                evt.HeightFraction = s.TraversalHeightFraction;
                evt.Reverse = s.TraversalReverse;
                evt.DurationMs = TraversalMath.DurationMs(canvasW, TraversalMath.SpriteSize(evt, canvasH).W, wraps, s.TraversalSpeedPxPerSecond);
                break;

            case ShowEventType.Announce:
                if (string.IsNullOrWhiteSpace(r.Text)) { error = "Type the announcement text first."; return null; }
                evt.Text = r.Text.Trim();
                evt.SpriteAspect = r.SpriteAspect;
                evt.HeightFraction = s.AnnounceHeightFraction;
                evt.ColorHex = s.AnnounceColorHex;
                evt.DurationMs = TraversalMath.DurationMs(canvasW, TraversalMath.SpriteSize(evt, canvasH).W, wraps, s.AnnounceSpeedPxPerSecond);
                break;
        }

        evt.DurationMs = ShowEventMath.ClampDuration(evt.DurationMs);
        return evt;
    }

    /// <summary>Canvas a lap runs over: the shared canvas, or (Simultaneous) the widest / tallest node, never wrapping.</summary>
    private static (int W, int H, bool Wraps) Canvas(ShowEventRequest r)
    {
        var layout = r.Layout;
        if (layout == null || layout.Nodes.Count == 0) return (FallbackCanvasW, FallbackCanvasH, false);
        if (r.PerMonitor) return (layout.Nodes.Max(n => n.Width), layout.Nodes.Max(n => n.Height), false);
        return (layout.CanvasWidth, layout.CanvasHeight, layout.Wraps);
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventFactoryTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WaBiBaBuSy.Models/Show/ShowEventFactory.cs WaBiBaBuSy.Tests/ShowEventFactoryTests.cs
git commit -m "feat(events): event factory (T0 = now + lead, seat targeting, lap durations)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Proto change, codec and server broadcast

**Files:**
- Modify: `WaBiBaBuSy.Grpc/Protos/wabibabusy.proto` (message `SyncCommand` ~line 140, enum `CommandType` ~line 149)
- Modify: `WaBiBaBuSy.Models/Content/ContentAssetRef.cs` (class `AssetRole`)
- Create: `WaBiBaBuSy.Core/Services/Show/ShowEventCodec.cs`
- Modify: `WaBiBaBuSy.Core/Services/WallpaperSyncCoordinator.cs` (after `BroadcastPrefetchAsync`, ~line 541)
- Test: `WaBiBaBuSy.Tests/ShowEventCodecTests.cs`

**Interfaces:**
- Consumes: `ShowEvent`, `ContentAssetRef`.
- Produces: `CommandType.Event` (= 12), `SyncCommand.EventJson`; `AssetRole.Event = "event"`; `ShowEventCodec.ToCommand(ShowEvent evt, int sequenceNumber, ContentAssetRef? asset = null) : SyncCommand`, `ShowEventCodec.TryFromCommand(SyncCommand, [NotNullWhen(true)] out ShowEvent? evt) : bool`; `WallpaperSyncCoordinator.BroadcastShowEventAsync(ShowEvent evt, ContentAssetRef? asset) : Task<int>` (number of clients reached).

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/ShowEventCodecTests.cs
using Google.Protobuf;
using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventCodecTests
{
    private static ShowEvent Spot() => new()
    {
        EventId = "abc", Type = ShowEventType.Spotlight, T0Utc = 5_000_000, DurationMs = 8000, Seed = 99,
        TargetOrder = 4, TargetCenterX = 4321.5f, ColorHex = "#FF00FF",
    };

    [Fact]
    public void ProtoBytes_RoundTrip_KeepsTheEvent()
    {
        var bytes = ShowEventCodec.ToCommand(Spot(), sequenceNumber: 17).ToByteArray();
        var cmd = SyncCommand.Parser.ParseFrom(bytes);
        Assert.Equal(CommandType.Event, cmd.Type);
        Assert.Equal(17, cmd.SequenceNumber);
        Assert.Equal(5_000_000, cmd.TimestampUtc);
        Assert.True(ShowEventCodec.TryFromCommand(cmd, out var back));
        Assert.Equal("abc", back.EventId);
        Assert.Equal(ShowEventType.Spotlight, back.Type);
        Assert.Equal(4, back.TargetOrder);
        Assert.Equal(4321.5f, back.TargetCenterX);
        Assert.Equal(99, back.Seed);
        Assert.Equal("#FF00FF", back.ColorHex);
    }

    [Fact]
    public void CommandType_Event_IsTwelve() => Assert.Equal(12, (int)CommandType.Event);

    [Fact]
    public void TryFromCommand_TakesT0FromTheTimestamp_NotTheJson()
    {
        var cmd = ShowEventCodec.ToCommand(Spot(), 1);
        cmd.TimestampUtc -= 2500;            // what the client's clock-offset conversion does
        Assert.True(ShowEventCodec.TryFromCommand(cmd, out var back));
        Assert.Equal(5_000_000 - 2500, back.T0Utc);
    }

    [Fact]
    public void UnknownCommandType_StillParses()  // a newer server's command reaching an older client
    {
        var bytes = new SyncCommand { Type = (CommandType)99, TimestampUtc = 1 }.ToByteArray();
        Assert.Equal(99, (int)SyncCommand.Parser.ParseFrom(bytes).Type);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("""{ "EventId": "", "Type": "Spotlight" }""")]
    [InlineData("""{ "EventId": "x", "Type": "Fireworks" }""")]   // a type this build does not know
    public void TryFromCommand_RejectsBadPayloads(string json) =>
        Assert.False(ShowEventCodec.TryFromCommand(new SyncCommand { Type = CommandType.Event, EventJson = json }, out _));

    [Fact]
    public void TryFromCommand_RejectsOtherCommandTypes()
    {
        var cmd = ShowEventCodec.ToCommand(Spot(), 1);
        cmd.Type = CommandType.Play;
        Assert.False(ShowEventCodec.TryFromCommand(cmd, out _));
    }

    [Fact]
    public void ToCommand_CarriesTheAsset()
    {
        var asset = new ContentAssetRef { ContentId = "logo.png-0123456789abcdef", Role = AssetRole.Event, OriginalPath = @"C:\logo.png" };
        var cmd = ShowEventCodec.ToCommand(Spot(), 1, asset);
        Assert.Equal(asset.ContentId, cmd.ContentId);
        var refd = Assert.Single(cmd.Params.Assets);
        Assert.Equal("event", refd.Role);
        Assert.Equal(@"C:\logo.png", refd.OriginalPath);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventCodecTests"`
Expected: build error — `CommandType.Event`, `EventJson`, `ShowEventCodec` do not exist.

- [ ] **Step 3: Change the proto (new numbers only)**

In `message SyncCommand`, after `SyncParameters params = 5;`:

```proto
  string event_json = 6;   // EVENT: JSON ShowEvent (one-shot show event); its T0 is timestamp_utc
```

In `enum CommandType`, after `TEST_PROBE = 11; ...`:

```proto
  EVENT = 12;             // One-shot show event (Spotlight, Celebration, …): ShowEvent JSON in event_json, T0 = timestamp_utc. Old clients log "unknown" and ignore it.
```

- [ ] **Step 4: Add the asset role and the codec**

In `WaBiBaBuSy.Models/Content/ContentAssetRef.cs`, class `AssetRole`, add after `Background`:

```csharp
    /// <summary>Image of a one-shot show event (Traversal).</summary>
    public const string Event = "event";
```

```csharp
// WaBiBaBuSy.Core/Services/Show/ShowEventCodec.cs
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Core.Services.Show;

/// <summary>
/// <see cref="ShowEvent"/> ↔ <see cref="SyncCommand"/> (type EVENT). The event's T0 travels in
/// <c>timestamp_utc</c> so the client's server→local clock conversion applies to it; the JSON's own T0 is ignored on
/// receipt. A Traversal image travels as a <see cref="ContentRef"/> in <c>params.assets</c>.
/// </summary>
public static class ShowEventCodec
{
    public static SyncCommand ToCommand(ShowEvent evt, int sequenceNumber, ContentAssetRef? asset = null)
    {
        var command = new SyncCommand
        {
            Type = CommandType.Event,
            TimestampUtc = evt.T0Utc,
            SequenceNumber = sequenceNumber,
            ContentId = asset?.ContentId ?? string.Empty,
            EventJson = JsonSerializer.Serialize(evt),
            Params = new SyncParameters(),
        };
        if (asset != null)
            command.Params.Assets.Add(new ContentRef { ContentId = asset.ContentId, Role = asset.Role, OriginalPath = asset.OriginalPath });
        return command;
    }

    /// <summary>The event of an EVENT command with T0 = the command's (converted) timestamp; false for anything else or a bad payload.</summary>
    public static bool TryFromCommand(SyncCommand command, [NotNullWhen(true)] out ShowEvent? evt)
    {
        evt = null;
        if (command.Type != CommandType.Event || string.IsNullOrWhiteSpace(command.EventJson)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<ShowEvent>(command.EventJson);
            if (parsed == null || string.IsNullOrEmpty(parsed.EventId)) return false;
            parsed.T0Utc = command.TimestampUtc;
            evt = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 5: Add the broadcast to `WallpaperSyncCoordinator`** (after `BroadcastPrefetchAsync`; add `using WaBiBaBuSy.Core.Services.Show;` and `using WaBiBaBuSy.Models.Show;`)

```csharp
    /// <summary>
    /// One-shot show event to every connected client (roadmap 2.4). The image of a Traversal is registered for
    /// download like scene assets. Returns how many clients the command reached.
    /// </summary>
    public async Task<int> BroadcastShowEventAsync(ShowEvent evt, ContentAssetRef? asset)
    {
        if (_syncService == null) return 0;
        if (asset != null) _syncService.RegisterContent(asset.ContentId, asset.OriginalPath);

        var command = ShowEventCodec.ToCommand(evt, System.Threading.Interlocked.Increment(ref _sequenceNumber), asset);
        int sent = 0;
        foreach (var client in _syncService.GetConnectedClients().ToList())
        {
            try { if (await _syncService.SendCommandToClientAsync(client.ClientId, command)) sent++; }
            catch (Exception ex) { _logger.LogError(ex, "Show event send failed for {ClientId}", client.ClientId); }
        }
        _logger.LogInformation("Show event {Type} {Id} (T0 {T0}) → {Sent} client(s)", evt.Type, evt.EventId, evt.T0Utc, sent);
        return sent;
    }
```

- [ ] **Step 6: Run to verify they pass, then build everything**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventCodecTests"` → PASS.
Run: `dotnet build WaBiBaBuSy.sln` → 0 errors (the regenerated proto must not break any `switch` over `CommandType`).

- [ ] **Step 7: Commit**

```bash
git add WaBiBaBuSy.Grpc/Protos/wabibabusy.proto WaBiBaBuSy.Models/Content/ContentAssetRef.cs WaBiBaBuSy.Core/Services/Show/ShowEventCodec.cs WaBiBaBuSy.Core/Services/WallpaperSyncCoordinator.cs WaBiBaBuSy.Tests/ShowEventCodecTests.cs
git commit -m "feat(events): EVENT sync command (proto 12 / event_json 6), codec, broadcast

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Client path — EVENT before the timing wait, asset fetch, delegate

**Files:**
- Modify: `WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs` (delegates ~line 52, `OnSyncCommandReceived` ~line 157)
- Modify: `WaBiBaBuSy.Core/Services/ClientPlaybackDelegates.cs`
- Modify: `WaBiBaBuSy.Core/Services/WaBiBaBuSyService.cs` (after `SetD2DCrossScreenStopDelegate`, ~line 99)
- Test: `WaBiBaBuSy.Tests/ShowEventClientTests.cs`

**Interfaces:**
- Consumes: `ShowEventCodec.TryFromCommand`, existing private `EnsureCachedAsync(string contentId)`.
- Produces: `WallpaperPlaybackService.ShowEventDelegate : Func<ShowEvent, Task>?`; `internal Task HandleShowEventAsync(SyncCommand)`; `ClientPlaybackDelegates.ShowEvent`; `WaBiBaBuSyService.SetShowEventDelegate(Func<ShowEvent, Task>?)`.

- [ ] **Step 1: Write the failing tests** (harness as in `SkewedSchedulingTests`)

```csharp
// WaBiBaBuSy.Tests/ShowEventClientTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>EVENT on the client: forwarded at once (players hold it until T0), the image fetched into the cache first.</summary>
public class ShowEventClientTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"wbbs-evt-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _time = new(Start);
    private readonly WallpaperSyncClient _client;
    private readonly WallpaperPlaybackService _playback;
    private readonly List<ShowEvent> _received = new();

    public ShowEventClientTests()
    {
        _client = new WallpaperSyncClient(NullLogger<WallpaperSyncClient>.Instance, new ClientConfiguration()) { TimeProvider = _time };
        _playback = new WallpaperPlaybackService(NullLogger<WallpaperPlaybackService>.Instance, _client, cacheDirectory: _cacheDir)
        {
            ShowEventDelegate = e => { lock (_received) _received.Add(e); return Task.CompletedTask; },
        };
    }

    public void Dispose()
    {
        _playback.Dispose();
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    private static ShowEvent Evt(ShowEventType type = ShowEventType.Celebration) =>
        new() { EventId = "e1", Type = type, DurationMs = 5000, ImagePath = @"C:\server\logo.png" };

    [Fact]
    public async Task FutureT0_ForwardedImmediately_WithT0FromTheTimestamp()
    {
        var cmd = ShowEventCodec.ToCommand(Evt(), 1);
        cmd.TimestampUtc = Start.ToUnixTimeMilliseconds() + 60_000;   // a minute ahead: must not wait for it
        await _playback.HandleShowEventAsync(cmd).WaitAsync(TimeSpan.FromSeconds(5));
        var e = Assert.Single(_received);
        Assert.Equal(cmd.TimestampUtc, e.T0Utc);
    }

    [Fact]
    public async Task CachedAsset_RewritesTheImagePath()
    {
        Directory.CreateDirectory(_cacheDir);
        var local = Path.Combine(_cacheDir, "logo.png");
        File.WriteAllBytes(local, new byte[] { 1 });
        _playback.RegisterContent("logo.png-0123456789abcdef", local);
        var asset = new ContentAssetRef { ContentId = "logo.png-0123456789abcdef", Role = AssetRole.Event, OriginalPath = @"C:\server\logo.png" };

        await _playback.HandleShowEventAsync(ShowEventCodec.ToCommand(Evt(ShowEventType.Traversal), 1, asset));

        Assert.Equal(local, Assert.Single(_received).ImagePath);
    }

    [Fact]
    public async Task AssetNotDownloadable_ForwardsWithoutImage()
    {
        var asset = new ContentAssetRef { ContentId = "missing.png-ffffffffffffffff", Role = AssetRole.Event, OriginalPath = @"C:\server\missing.png" };
        await _playback.HandleShowEventAsync(ShowEventCodec.ToCommand(Evt(ShowEventType.Traversal), 1, asset));
        Assert.Null(Assert.Single(_received).ImagePath);
    }

    [Fact]
    public async Task NoAsset_DropsTheServerPath()
    {
        await _playback.HandleShowEventAsync(ShowEventCodec.ToCommand(Evt(ShowEventType.Traversal), 1));
        Assert.Null(Assert.Single(_received).ImagePath);     // a server path means nothing on this machine
    }

    [Fact]
    public async Task MalformedEvent_IsIgnored()
    {
        await _playback.HandleShowEventAsync(new SyncCommand { Type = CommandType.Event, EventJson = "{" });
        Assert.Empty(_received);
    }

    [Fact]
    public async Task ThrowingDelegate_DoesNotEscape()
    {
        _playback.ShowEventDelegate = _ => throw new InvalidOperationException("player gone");
        await _playback.HandleShowEventAsync(ShowEventCodec.ToCommand(Evt(), 1));   // must not throw
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventClientTests"`
Expected: build error — `ShowEventDelegate` / `HandleShowEventAsync` do not exist.

- [ ] **Step 3: Implement in `WallpaperPlaybackService`** (add `using WaBiBaBuSy.Core.Services.Show;` and `using WaBiBaBuSy.Models.Show;`)

After `D2DCrossScreenStopDelegate`:

```csharp
    /// <summary>
    /// One-shot show event (roadmap 2.4): T0 already in this machine's clock terms, image path rewritten to the cache
    /// (null when it could not be fetched). Set by the UI layer to forward the event to the local players.
    /// </summary>
    public Func<ShowEvent, Task>? ShowEventDelegate { get; set; }
```

In `OnSyncCommandReceived`, before `// Schedule command execution based on timestamp`:

```csharp
        // Show events skip the timestamp wait: the players hold them until T0 (fetching the image takes time too).
        if (command.Type == CommandType.Event)
        {
            _ = Task.Run(() => HandleShowEventAsync(command));
            return;
        }
```

New method (next to `HandlePrefetchAsync`):

```csharp
    /// <summary>EVENT: parse, fetch the image into the cache, rewrite its path, forward. Never throws.</summary>
    internal async Task HandleShowEventAsync(SyncCommand command)
    {
        try
        {
            if (!ShowEventCodec.TryFromCommand(command, out var evt))
            {
                _logger.LogWarning("[Playback:EVENT] Ignoring an unreadable show event (seq {Seq})", command.SequenceNumber);
                return;
            }

            var asset = command.Params?.Assets.FirstOrDefault();
            if (asset != null)
            {
                evt.ImagePath = await EnsureCachedAsync(asset.ContentId);
                if (evt.ImagePath == null)
                    _logger.LogWarning("[Playback:EVENT] Could not fetch {ContentId}; {Type} is drawn without its image", asset.ContentId, evt.Type);
            }
            else
            {
                evt.ImagePath = null;   // a path on the server means nothing here
            }

            _logger.LogInformation("[Playback:EVENT] {Type} {Id} T0={T0} duration={Duration}ms", evt.Type, evt.EventId, evt.T0Utc, evt.DurationMs);
            var forward = ShowEventDelegate;
            if (forward != null) await forward(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Playback:EVENT] Show event failed");
        }
    }
```

- [ ] **Step 4: Plumb the delegate**

`ClientPlaybackDelegates`: add the property and copy it in `ApplyTo` (add `using WaBiBaBuSy.Models.Show;`):

```csharp
    /// <summary>One-shot show event for the local players (see <see cref="WallpaperPlaybackService.ShowEventDelegate"/>).</summary>
    public Func<ShowEvent, Task>? ShowEvent { get; set; }
```

```csharp
        playback.ShowEventDelegate = ShowEvent;
```

`WaBiBaBuSyService`, after `SetD2DCrossScreenStopDelegate`:

```csharp
    /// <summary>Set the show-event delegate (forwards EVENTs to this machine's players). Kept for later connections.</summary>
    public void SetShowEventDelegate(Func<Models.Show.ShowEvent, Task>? showEvent)
    {
        PlaybackDelegates.ShowEvent = showEvent;
        if (_playbackService != null)
            _playbackService.ShowEventDelegate = showEvent;
    }
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventClientTests"` → PASS.
If `AssetNotDownloadable_ForwardsWithoutImage` throws inside `DownloadContentAsync` (no connection), that is caught by `HandleShowEventAsync`'s catch and nothing is forwarded — then wrap only the `EnsureCachedAsync` call in its own `try { … } catch (Exception ex) { _logger.LogWarning(ex, …); evt.ImagePath = null; }` so the event is still forwarded.

- [ ] **Step 6: Full test run, commit**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → green.

```bash
git add WaBiBaBuSy.Core/Services/WallpaperPlaybackService.cs WaBiBaBuSy.Core/Services/ClientPlaybackDelegates.cs WaBiBaBuSy.Core/Services/WaBiBaBuSyService.cs WaBiBaBuSy.Tests/ShowEventClientTests.cs
git commit -m "feat(events): client handles EVENT before the timing wait, fetches the image, forwards

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Player IPC message and host send

**Files:**
- Create: `WaBiBaBuSy.Player.Common/Messages/PlayerCommandShowEvent.cs`
- Modify: `WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs` (after `SendTestModeAsync`, ~line 633)
- Modify: `WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs` (after `SetTestModeAsync`, ~line 64)

**Interfaces:**
- Produces: `PlayerCommandShowEvent { MessageType = "cmd_show_event"; ShowEvent Event }`; `D2DPlayerHost.SendShowEventAsync(ShowEvent) : Task`; `D2DCompositionService.SendShowEventAsync(ShowEvent) : Task`.

No unit test (the test project does not reference the player assemblies; the JSON round trip of `ShowEvent` with Newtonsoft is covered in Task 1). Verification is the build.

- [ ] **Step 1: Message**

```csharp
// WaBiBaBuSy.Player.Common/Messages/PlayerCommandShowEvent.cs
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// One-shot show event (roadmap 2.4). The player keeps it and draws it above the scene from T0 for its duration;
/// a second copy with the same EventId (replay) is ignored. No stdout reply.
/// </summary>
public class PlayerCommandShowEvent : PlayerMessageBase
{
    public PlayerCommandShowEvent()
    {
        MessageType = "cmd_show_event";
    }

    public ShowEvent Event { get; set; } = new();
}
```

- [ ] **Step 2: Host send** — `D2DPlayerHost` (add `using WaBiBaBuSy.Models.Show;`):

```csharp
    /// <summary>One-shot show event. Fire-and-forget: the player sends no stdout reply.</summary>
    public async Task SendShowEventAsync(ShowEvent evt)
    {
        if (!IsRunning) return;
        await SendCommandAsync(JsonConvert.SerializeObject(new PlayerCommandShowEvent { Event = evt }));
    }
```

`D2DCompositionService` (add `using WaBiBaBuSy.Models.Show;`):

```csharp
    /// <summary>Send a one-shot show event to every running player of this service.</summary>
    public async Task SendShowEventAsync(ShowEvent evt)
    {
        foreach (var host in _playerHosts.Values)
        {
            if (!host.IsRunning) continue;
            try { await host.SendShowEventAsync(evt); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to send show event {Id} to a player host", evt.EventId); }
        }
    }
```

- [ ] **Step 3: Build, commit**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors.

```bash
git add WaBiBaBuSy.Player.Common/Messages/PlayerCommandShowEvent.cs WaBiBaBuSy.WallpaperEngine/Direct2D/D2DPlayerHost.cs WaBiBaBuSy.WallpaperEngine/Composition/D2DCompositionService.cs
git commit -m "feat(events): cmd_show_event player message and host send

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Player — keep, decode and draw events

**Files:**
- Create: `WaBiBaBuSy.Player.D2D/Program.ShowEvents.cs`
- Modify: `WaBiBaBuSy.Player.D2D/Program.cs`: `HandleJsonCommand` switch (~line 3155, next to `cmd_test_mode`) and its catch (~line 3197); frame tail (~line 1356, before `RecordMovementTrailSample();`); `RenderDueExactFrame` (~line 2045, after `DrawNativeGifScene(elapsed);`); `DisposeNativeD2DResources` (~line 4163)

**Interfaces:**
- Consumes: `ShowEventLog`, `ShowEventMath`, `SpotlightMath`, `CelebrationMath`, `TraversalMath` (Models); `PlayerCommandShowEvent`; existing player members `_d2dContext`, `_layout`, `Scale`, `_perMonitorMode`, `_renderLoopStart`, `NowUtcMs`, `_lastFrameElapsedMs`, `_lastFramePhaseMs`, `UploadBitmapToD2D(Bitmap)`, `SyncTiming.PositiveModulo(long, long)`, `_logger`.
- Produces: `HandleShowEventCommand(PlayerCommandShowEvent)`, `DrawShowEvents(long sceneRawElapsedMs)`, `DisposeShowEventResources()`, `DecodeEventSprite(string eventId, Func<EventSpriteFrames> decode)`, `EventSpriteFrames` (Task 16 adds the Announce decode).

No unit test can reach the player; the math is covered by Tasks 2–5. Verification: build, plus the showcase exact frames in Task 15.

- [ ] **Step 1: Create `Program.ShowEvents.cs`**

```csharp
// WaBiBaBuSy.Player.D2D/Program.ShowEvents.cs
using System.Drawing;
using System.Numerics;
using ImageMagick;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>
/// One-shot show events (roadmap 2.4, design 2026-10-03-show-events-design.md): kept from cmd_show_event, drawn above
/// the scene from T0 for their duration. Math in WaBiBaBuSy.Models/Show. The stdin thread adds events and decodes
/// sprite images on a worker task; the render thread uploads and draws.
/// </summary>
partial class Program
{
    private sealed class EventSpriteFrames : IDisposable
    {
        public required List<Bitmap> Frames { get; init; }
        public required List<int> DelaysMs { get; init; }
        public void Dispose() { foreach (var f in Frames) f.Dispose(); }
    }

    private sealed class EventSpriteGpu : IDisposable
    {
        public required ID2D1Bitmap[] Frames { get; init; }
        public required int[] DelaysMs { get; init; }
        public required long TotalMs { get; init; }
        public void Dispose() { foreach (var f in Frames) f.Dispose(); }
    }

    private const int MaxEventSpriteHeight = 1024;

    private static readonly object _showEventLock = new();
    private static readonly ShowEventLog _showEvents = new();                                         // own lock
    private static readonly Dictionary<string, EventSpriteFrames> _decodedEventSprites = new();      // guarded by _showEventLock
    private static readonly HashSet<string> _decodingEventSprites = new();                           // guarded by _showEventLock
    private static readonly Dictionary<string, EventSpriteGpu> _eventSprites = new();                // render thread only
    private static readonly ShowEvent[] _showEventScratch = new ShowEvent[ShowEventLog.Capacity];   // render thread only
    private static CelebrationParticle[] _particleScratch = new CelebrationParticle[512];           // render thread only
    private static readonly List<string> _staleSpriteScratch = new();                                // render thread only
    private static ID2D1SolidColorBrush? _eventBrush;
    private static long _lastEventPruneUtcMs;

    /// <summary>stdin thread: keep the event (a replay with a known id is ignored) and start decoding its sprite.</summary>
    private static void HandleShowEventCommand(PlayerCommandShowEvent cmd)
    {
        var evt = cmd.Event;
        if (evt == null || !_showEvents.Add(evt)) return;
        _logger?.LogInformation("[Event] {Type} {Id} T0={T0} ({InMs} ms from now) duration={Duration}ms",
            evt.Type, evt.EventId, evt.T0Utc, evt.T0Utc - NowUtcMs, evt.DurationMs);
        StartEventSpriteDecode(evt);
    }

    /// <summary>Decode the event's sprite on a worker task unless it is decoded, decoding or uploaded already.</summary>
    private static void StartEventSpriteDecode(ShowEvent evt)
    {
        Func<EventSpriteFrames>? decode = evt.Type switch
        {
            ShowEventType.Traversal when !string.IsNullOrEmpty(evt.ImagePath) => () => DecodeImageFrames(evt.ImagePath!),
            _ => null,
        };
        if (decode == null)
        {
            if (evt.Type == ShowEventType.Traversal) _logger?.LogWarning("[Event] Traversal {Id} has no image on this machine; skipped", evt.EventId);
            return;
        }
        lock (_showEventLock)
        {
            if (_decodingEventSprites.Contains(evt.EventId) || _decodedEventSprites.ContainsKey(evt.EventId)) return;
            _decodingEventSprites.Add(evt.EventId);
        }
        _ = Task.Run(() => DecodeEventSprite(evt.EventId, decode));
    }

    private static void DecodeEventSprite(string eventId, Func<EventSpriteFrames> decode)
    {
        EventSpriteFrames? frames = null;
        try
        {
            frames = decode();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Event] Could not decode the sprite of {Id}; skipped", eventId);
        }
        lock (_showEventLock)
        {
            _decodingEventSprites.Remove(eventId);
            if (frames != null && _showEvents.Contains(eventId)) { _decodedEventSprites[eventId] = frames; frames = null; }
        }
        frames?.Dispose();   // the event was pruned meanwhile
    }

    /// <summary>All frames of an image / GIF (coalesced), at most 1024 px high, as GDI+ bitmaps (uploaded later on the render thread).</summary>
    private static EventSpriteFrames DecodeImageFrames(string path)
    {
        using var collection = new MagickImageCollection(path);
        collection.Coalesce();
        var frames = new List<Bitmap>(collection.Count);
        var delays = new List<int>(collection.Count);
        foreach (var frame in collection)
        {
            if (frame.Height > MaxEventSpriteHeight)
            {
                uint w = (uint)Math.Max(1L, (long)frame.Width * MaxEventSpriteHeight / frame.Height);
                frame.Resize(new MagickGeometry(w, (uint)MaxEventSpriteHeight) { IgnoreAspectRatio = true });
            }
            frames.Add(frame.ToBitmap());
            int delay = (int)(frame.AnimationDelay * 10);
            delays.Add(delay <= 0 ? 100 : Math.Min(delay, 1000));
        }
        return new EventSpriteFrames { Frames = frames, DelaysMs = delays };
    }

    /// <summary>Render thread: decoded sprites → GPU; GPU sprites of events that left the log are released.</summary>
    private static void SyncEventSprites(int eventCount)
    {
        List<KeyValuePair<string, EventSpriteFrames>>? ready = null;
        lock (_showEventLock)
        {
            if (_decodedEventSprites.Count > 0)
            {
                ready = _decodedEventSprites.ToList();   // rare (once per event), not per frame
                _decodedEventSprites.Clear();
            }
        }
        if (ready != null)
        {
            foreach (var (id, frames) in ready)
            {
                using (frames)
                {
                    try
                    {
                        var gpu = frames.Frames.Select(UploadBitmapToD2D).ToArray();
                        _eventSprites[id] = new EventSpriteGpu { Frames = gpu, DelaysMs = frames.DelaysMs.ToArray(), TotalMs = Math.Max(1, frames.DelaysMs.Sum()) };
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[Event] Could not upload the sprite of {Id}", id);
                    }
                }
            }
        }

        if (_eventSprites.Count == 0) return;
        _staleSpriteScratch.Clear();
        foreach (var id in _eventSprites.Keys)
        {
            bool known = false;
            for (int i = 0; i < eventCount && !known; i++) known = _showEventScratch[i].EventId == id;
            if (!known) _staleSpriteScratch.Add(id);
        }
        foreach (var id in _staleSpriteScratch)
        {
            _eventSprites[id].Dispose();
            _eventSprites.Remove(id);
        }
    }

    /// <summary>Draw order: sprites and confetti, then the spotlight (dims them too), then blackout on top.</summary>
    private static int EventLayer(ShowEventType type) => type switch
    {
        ShowEventType.Spotlight => 1,
        ShowEventType.Blackout => 2,
        _ => 0,
    };

    /// <summary>
    /// Render thread: draw the active events above the scene. <paramref name="sceneRawElapsedMs"/> is the un-phased scene
    /// elapsed (live: now − shared start; exact frame: the probe's elapsed), so exact frames show events exactly as live.
    /// </summary>
    private static void DrawShowEvents(long sceneRawElapsedMs)
    {
        if (_d2dContext == null || (_showEvents.Count == 0 && _eventSprites.Count == 0)) return;

        long now = NowUtcMs;
        if (now - _lastEventPruneUtcMs > 1000)
        {
            _lastEventPruneUtcMs = now;
            _showEvents.Prune(now);
        }
        int n = _showEvents.CopyTo(_showEventScratch);
        SyncEventSprites(n);
        if (n == 0) return;

        long sceneStartUtc = new DateTimeOffset(_renderLoopStart).ToUnixTimeMilliseconds();
        _eventBrush ??= _d2dContext.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        var prior = _d2dContext.Transform;
        var canvas = Matrix3x2.CreateScale(Scale) * prior;   // canvas units → device px
        _d2dContext.Transform = canvas;
        try
        {
            for (int layer = 0; layer <= 2; layer++)
            {
                for (int i = 0; i < n; i++)
                {
                    var evt = _showEventScratch[i];
                    if (EventLayer(evt.Type) != layer) continue;
                    long local = ShowEventMath.LocalMs(evt, sceneStartUtc, sceneRawElapsedMs);
                    if (!ShowEventMath.IsActive(evt, local)) continue;
                    switch (evt.Type)
                    {
                        case ShowEventType.Blackout: DrawBlackout(evt, local); break;
                        case ShowEventType.Spotlight: DrawSpotlight(evt, local); break;
                        case ShowEventType.Celebration: DrawCelebration(evt, local, canvas); break;
                        case ShowEventType.Traversal:
                        case ShowEventType.Announce: DrawEventSprite(evt, local); break;
                    }
                }
            }
        }
        finally
        {
            _d2dContext.Transform = prior;
        }
    }

    private static void SetEventBrush(string hex, float opacity)
    {
        var (r, g, b) = ShowEventMath.ParseColor(hex);
        _eventBrush!.Color = new Color4(r / 255f, g / 255f, b / 255f, 1f);
        _eventBrush.Opacity = Math.Clamp(opacity, 0f, 1f);
    }

    private static void SetEventBrushBlack(float opacity)
    {
        _eventBrush!.Color = new Color4(0f, 0f, 0f, 1f);
        _eventBrush.Opacity = Math.Clamp(opacity, 0f, 1f);
    }

    private static void DrawBlackout(ShowEvent evt, long local)
    {
        SetEventBrush(evt.ColorHex, ShowEventMath.BlackoutOpacity(evt, local));
        _d2dContext!.FillRectangle(new System.Drawing.RectangleF(0, 0, _layout.Width, _layout.Height), _eventBrush!);
    }

    private static void DrawSpotlight(ShowEvent evt, long local)
    {
        var v = SpotlightMath.Visual(evt, _layout, sharedCanvas: !_perMonitorMode, local);
        if (v.IsTarget)
        {
            if (v.BorderOpacity <= 0.005f) return;
            SetEventBrush(evt.ColorHex, v.BorderOpacity);
            float half = v.BorderWidth / 2f;
            _d2dContext!.DrawRectangle(new System.Drawing.RectangleF(half, half, _layout.Width - v.BorderWidth, _layout.Height - v.BorderWidth), _eventBrush!, v.BorderWidth);
            return;
        }
        if (v.DimOpacity > 0.005f)
        {
            SetEventBrushBlack(v.DimOpacity);
            _d2dContext!.FillRectangle(new System.Drawing.RectangleF(0, 0, _layout.Width, _layout.Height), _eventBrush!);
        }
        if (v.ChevronDirection != 0 && v.ChevronOpacity > 0.005f)
        {
            SetEventBrush(evt.ColorHex, v.ChevronOpacity);
            float half = v.ChevronSize / 2f, d = v.ChevronDirection;
            var tip = new Vector2(v.ChevronCenterX + d * half, v.ChevronCenterY);
            var top = new Vector2(v.ChevronCenterX - d * half, v.ChevronCenterY - half);
            var bottom = new Vector2(v.ChevronCenterX - d * half, v.ChevronCenterY + half);
            float stroke = v.ChevronSize * 0.18f;
            _d2dContext!.DrawLine(top, tip, _eventBrush!, stroke);
            _d2dContext.DrawLine(bottom, tip, _eventBrush!, stroke);
        }
    }

    private static void DrawCelebration(ShowEvent evt, long local, Matrix3x2 canvas)
    {
        int count = CelebrationMath.Count(_layout.CanvasWidth > 0 ? _layout.CanvasWidth : _layout.Width);
        int capacity = CelebrationMath.Capacity(_layout, count);
        if (_particleScratch.Length < capacity) _particleScratch = new CelebrationParticle[capacity];   // grows once per layout
        int visible = CelebrationMath.VisibleOnNode(evt, _layout, local, _particleScratch);
        float env = ShowEventMath.Envelope(local, evt.DurationMs);
        if (env <= 0.005f) return;
        try
        {
            for (int i = 0; i < visible; i++)
            {
                var p = _particleScratch[i];
                SetEventBrush(CelebrationMath.ColorFor(evt, p.ColorIndex), env);
                var center = new Vector2(p.X + p.Width / 2f, p.Y + p.Height / 2f);
                _d2dContext!.Transform = Matrix3x2.CreateRotation(p.RotationDeg * MathF.PI / 180f, center) * canvas;
                _d2dContext.FillRectangle(new System.Drawing.RectangleF(p.X, p.Y, p.Width, p.Height), _eventBrush!);
            }
        }
        finally
        {
            _d2dContext!.Transform = canvas;
        }
    }

    /// <summary>Traversal image or Announce text bitmap, one lap (TraversalMath), wrap copies on a Ring. Skipped until decoded.</summary>
    private static void DrawEventSprite(ShowEvent evt, long local)
    {
        if (!_eventSprites.TryGetValue(evt.EventId, out var sprite))
        {
            bool known;
            lock (_showEventLock) known = _decodingEventSprites.Contains(evt.EventId) || _decodedEventSprites.ContainsKey(evt.EventId);
            if (!known) StartEventSpriteDecode(evt);   // e.g. after DisposeNativeD2DResources released the GPU copy
            return;
        }
        int cw = _layout.CanvasWidth > 0 ? _layout.CanvasWidth : _layout.Width;
        int ch = _layout.CanvasHeight > 0 ? _layout.CanvasHeight : _layout.Height;
        var pose = TraversalMath.Pose(evt, cw, ch, _layout.Wraps, local);
        if (pose is not { } p || p.Opacity <= 0.005f) return;

        var frame = sprite.Frames[0];
        if (sprite.Frames.Length > 1)
        {
            long t = SyncTiming.PositiveModulo(local, sprite.TotalMs);
            for (int f = 0; f < sprite.DelaysMs.Length; f++)
            {
                if (t < sprite.DelaysMs[f]) { frame = sprite.Frames[f]; break; }
                t -= sprite.DelaysMs[f];
            }
        }

        Span<float> copies = stackalloc float[2];
        int nCopies = NodeMapping.WrapCopies(p.VirtualX, p.Width, _layout, copies);
        float ly = NodeMapping.ToLocalY(p.VirtualY, _layout);
        for (int c = 0; c < nCopies; c++)
        {
            float lx = NodeMapping.ToLocalX(copies[c], p.Width, _layout);
            if (!NodeMapping.IsVisible(lx, p.Width, _layout)) continue;
            _d2dContext!.DrawBitmap(frame, new System.Drawing.RectangleF(lx, ly, p.Width, p.Height), p.Opacity, BitmapInterpolationMode.Linear, null);
        }
    }

    /// <summary>Release the event brush and GPU sprites (reload / device loss / exit). Events stay; sprites are re-decoded on demand.</summary>
    private static void DisposeShowEventResources()
    {
        _eventBrush?.Dispose();
        _eventBrush = null;
        foreach (var s in _eventSprites.Values) s.Dispose();
        _eventSprites.Clear();
    }
}
```

- [ ] **Step 2: Wire it into `Program.cs`**

In `HandleJsonCommand`'s switch, after the `cmd_probe` case:

```csharp
                case "cmd_show_event":
                    // No stdout reply (like cmd_test_mode): the host does not read one.
                    var eventCmd = JsonConvert.DeserializeObject<PlayerCommandShowEvent>(json);
                    if (eventCmd != null) HandleShowEventCommand(eventCmd);
                    break;
```

In its catch, extend the no-reply list:

```csharp
            if (messageType is "cmd_test_mode" or "cmd_probe" or "cmd_show_event") return;
```

Frame tail — replace

```csharp
                    RecordMovementTrailSample();
                    DrawDebugOverlay();
```

with

```csharp
                    RecordMovementTrailSample();
                    // Show events: above the scene, below the debug overlay and the timecode strip. Raw = un-phased elapsed.
                    if (_lastFrameElapsedMs != long.MinValue) DrawShowEvents(_lastFrameElapsedMs + _lastFramePhaseMs);
                    DrawDebugOverlay();
```

In `RenderDueExactFrame`, right after `DrawNativeGifScene(elapsed);`:

```csharp
            DrawShowEvents(elapsed);   // exact frames have no phase: the raw elapsed is the requested one
```

In `DisposeNativeD2DResources`, after `DisposeEffectBrushes();`:

```csharp
        DisposeShowEventResources();
```

- [ ] **Step 3: Build**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors. If `MagickGeometry(uint, uint)` or `frame.Width` types differ in Magick.NET 14.17, convert with explicit casts — do not change the downscale rule.

- [ ] **Step 4: Full tests, commit**

Run: `dotnet test WaBiBaBuSy.Tests` → green.

```bash
git add WaBiBaBuSy.Player.D2D/Program.ShowEvents.cs WaBiBaBuSy.Player.D2D/Program.cs
git commit -m "feat(player): draw show events above the scene (spotlight, celebration, traversal, blackout)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Playback hosts — fire, log, replay, prefetch

**Files:**
- Create: `WaBiBaBuSy.UI/Services/EventImages.cs`
- Modify: `WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs` (fields near `_activeLayout` ~line 91; `ClearActiveScene` ~line 173; `ApplyCrossScreenConfigAsync` after the Phase 2 start loop ~line 1117 and after Phase 3 ~line 1167; RTT capture at ~line 1102)
- Modify: `WaBiBaBuSy.UI/Services/RemotePlaybackHost.cs` (`Register`, `ApplyCrossScreenAsync`, `ApplyPlainAsync`, `StopAllAsync`)

**Interfaces:**
- Consumes: `ShowEventFactory`, `ShowEventRequest`, `ShowEventLog`, `ShowEventSettingsStore`, `WallpaperSyncCoordinator.BroadcastShowEventAsync`, `D2DCompositionService.SendShowEventAsync`, `WaBiBaBuSyService.SetShowEventDelegate`, `ContentIdentity.ComputeId(string)`, `AssetRole.Event`.
- Produces on `ServerPlaybackHost`: `ShowEventSettings EventSettings`, `void SaveEventSettings(ShowEventSettings)`, `IReadOnlyList<ShowEvent> ActiveShowEvents` (observable), `string? EventStatusText` (observable), `bool CanFireEvents`, `event Action<ShowEvent>? EventFired`, `Task<bool> FireEventAsync(ShowEventType type, string? targetSeatId = null)`, `Task FireEventForTestAsync(ShowEvent evt)`, `Task PrefetchEventImageAsync()`. On `RemotePlaybackHost`: `Task OnShowEventAsync(ShowEvent evt)`. `EventImages.Aspect(string? path) : float`.

UI layer: no unit tests (the test project does not reference the UI); the logic it calls is tested in Tasks 2–8. Verification: build.

- [ ] **Step 1: `EventImages`**

```csharp
// WaBiBaBuSy.UI/Services/EventImages.cs
using System;
using System.IO;
using Avalonia.Media.Imaging;

namespace WaBiBaBuSy.UI.Services;

/// <summary>Image facts the server needs for show events.</summary>
public static class EventImages
{
    /// <summary>Width ÷ height of an image file; 1 when it cannot be read.</summary>
    public static float Aspect(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 1f;
            using var bmp = new Bitmap(path);
            return bmp.PixelSize.Height > 0 ? bmp.PixelSize.Width / (float)bmp.PixelSize.Height : 1f;
        }
        catch (Exception)
        {
            return 1f;
        }
    }
}
```

- [ ] **Step 2: `ServerPlaybackHost` state** (add `using WaBiBaBuSy.Models.Show;`, `using WaBiBaBuSy.Models.Configuration;` if missing, `using WaBiBaBuSy.Models.Content;` if missing)

Next to the other live-state properties:

```csharp
    // ── One-shot show events (roadmap 2.4) ──
    private readonly ShowEventLog _showEvents = new();
    private double _activeMaxRttMs;

    /// <summary>Events of the running scene that have not ended (the room preview draws them).</summary>
    [ObservableProperty] private IReadOnlyList<ShowEvent> _activeShowEvents = Array.Empty<ShowEvent>();

    /// <summary>Last event result or refusal, for the Events strip.</summary>
    [ObservableProperty] private string? _eventStatusText;

    /// <summary>Defaults of the Events strip and hotkeys.</summary>
    public ShowEventSettings EventSettings { get; private set; } = ShowEventSettingsStore.Load();

    /// <summary>An event was fired (after it went to the players and clients).</summary>
    public event Action<ShowEvent>? EventFired;

    /// <summary>True while a scene plays, so events have a canvas and a clock.</summary>
    public bool CanFireEvents => ActiveScene != null && ActiveLayout != null;

    public void SaveEventSettings(ShowEventSettings settings)
    {
        EventSettings = settings;
        ShowEventSettingsStore.Save(settings);
    }
```

Raise `CanFireEvents` when the scene changes: add `partial void OnActiveSceneChanged(CrossScreenConfig? value) => OnPropertyChanged(nameof(CanFireEvents));` (if a partial `OnActiveSceneChanged` already exists, add the line to it).

In `ClearActiveScene()` append:

```csharp
        _showEvents.Clear();
        ActiveShowEvents = Array.Empty<ShowEvent>();
```

In `ApplyCrossScreenConfigAsync`, right after `double maxRttMs = plan.MaxRemoteRttMs;`:

```csharp
        _activeMaxRttMs = maxRttMs;   // events fired during this scene use the same lead rule
```

After the Phase 2 loop that does `_players[monitorIndex] = d2dService;`:

```csharp
        // An event still running (e.g. across a playlist item switch) continues on the new players at its position.
        await ReplayEventsAsync(newServices.Select(s => s.Item2));
```

After the Phase 3 block (remote commands), before `StartFullscreenDetectionIfNeeded();`:

```csharp
        _ = PrefetchEventImageAsync();
```

- [ ] **Step 3: `ServerPlaybackHost` fire / dispatch / replay / prefetch**

```csharp
    /// <summary>
    /// Fire a one-shot event on every seat: this machine's players and every connected client. Refused (false, with
    /// <see cref="EventStatusText"/>) when nothing plays or the event is incomplete. Call on the UI thread.
    /// </summary>
    public async Task<bool> FireEventAsync(ShowEventType type, string? targetSeatId = null)
    {
        var scene = ActiveScene;
        var layout = ActiveLayout;
        if (scene == null || layout == null)
        {
            EventStatusText = "Events need a running scene.";
            return false;
        }

        var settings = EventSettings;
        string? image = null;
        string? text = null;
        float aspect = 1f;
        if (type == ShowEventType.Traversal)
        {
            image = string.IsNullOrWhiteSpace(settings.TraversalImagePath) ? scene.Animation.AnimationPath : settings.TraversalImagePath;
            aspect = EventImages.Aspect(image);
        }

        var request = new ShowEventRequest(type, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _activeMaxRttMs, layout,
            PerMonitor: scene.DistributionMode == AnimationDistributionMode.Simultaneous, targetSeatId,
            Seed: Random.Shared.Next(), EventId: Guid.NewGuid().ToString("N"), image, aspect, text);
        var evt = ShowEventFactory.Create(request, settings, out var error);
        if (evt == null)
        {
            EventStatusText = error;
            return false;
        }
        await DispatchEventAsync(evt);
        return true;
    }

    /// <summary>Test runs: send a prepared event (T0 already set by the runner). Call on the UI thread.</summary>
    public Task FireEventForTestAsync(ShowEvent evt) => DispatchEventAsync(evt);

    private async Task DispatchEventAsync(ShowEvent evt)
    {
        _showEvents.Add(evt);
        ActiveShowEvents = _showEvents.Active(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        foreach (var player in _players.Values)
        {
            try { await player.SendShowEventAsync(evt); }
            catch (Exception ex) { Debug.WriteLine($"[Events] Local player send failed: {ex.Message}"); }
        }

        int sent = 0;
        if (_service.SyncCoordinator != null)
            sent = await _service.SyncCoordinator.BroadcastShowEventAsync(evt, EventAsset(evt.ImagePath));

        EventStatusText = sent > 0 ? $"{evt.Type} → this machine + {sent} client(s)" : $"{evt.Type} → this machine";
        EventFired?.Invoke(evt);
    }

    private async Task ReplayEventsAsync(IEnumerable<D2DCompositionService> players)
    {
        var active = _showEvents.Active(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (active.Count == 0) return;
        foreach (var player in players)
            foreach (var evt in active)
            {
                try { await player.SendShowEventAsync(evt); }
                catch (Exception ex) { Debug.WriteLine($"[Events] Replay failed: {ex.Message}"); }
            }
    }

    private static ContentAssetRef? EventAsset(string? path) =>
        string.IsNullOrEmpty(path) || !File.Exists(path)
            ? null
            : new ContentAssetRef { ContentId = ContentIdentity.ComputeId(path), Role = AssetRole.Event, OriginalPath = path };

    /// <summary>Cache the Traversal image on every client ahead of time (when it is picked and on every scene start).</summary>
    public async Task PrefetchEventImageAsync()
    {
        var asset = EventAsset(EventSettings.TraversalImagePath);
        if (asset == null || _service.SyncCoordinator == null) return;
        try { await _service.SyncCoordinator.BroadcastPrefetchAsync(new[] { asset }); }
        catch (Exception ex) { Debug.WriteLine($"[Events] Prefetch of the traversal image failed: {ex.Message}"); }
    }
```

- [ ] **Step 4: `RemotePlaybackHost`** (add `using WaBiBaBuSy.Models.Show;`)

Field and registration:

```csharp
    private readonly ShowEventLog _showEvents = new();
```

In `Register()` append:

```csharp
        _service.SetShowEventDelegate(OnShowEventAsync);
```

Methods:

```csharp
    /// <summary>EVENT from the server (T0 in this machine's clock, image in the cache): keep it and send it to every player.</summary>
    public async Task OnShowEventAsync(ShowEvent evt)
    {
        if (!_showEvents.Add(evt)) return;
        foreach (var player in _players.Values)
        {
            try { await player.SendShowEventAsync(evt); }
            catch (Exception ex) { Debug.WriteLine($"[Events] Player send failed: {ex.Message}"); }
        }
    }

    /// <summary>A new player continues the events that have not ended (playlist item switch, late start).</summary>
    private async Task ReplayEventsAsync(D2DCompositionService player)
    {
        long now = _service.Client?.NowUtcMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var evt in _showEvents.Active(now))
        {
            try { await player.SendShowEventAsync(evt); }
            catch (Exception ex) { Debug.WriteLine($"[Events] Replay failed: {ex.Message}"); }
        }
    }
```

In `ApplyCrossScreenAsync` and `ApplyPlainAsync`, after `_players[monitorIndex] = d2dService;`:

```csharp
        await ReplayEventsAsync(d2dService);
```

In `StopAllAsync()`, after `_players.Clear();` (the server sends Stop only for an explicit stop, never between playlist items):

```csharp
        _showEvents.Clear();
```

- [ ] **Step 5: Build, full tests, commit**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → green.

```bash
git add WaBiBaBuSy.UI/Services/EventImages.cs WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs WaBiBaBuSy.UI/Services/RemotePlaybackHost.cs
git commit -m "feat(events): playback hosts fire, log and replay show events; prefetch the traversal image

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Room preview draws live events

**Files:**
- Create: `WaBiBaBuSy.UI/Controls/ScenePainter.Events.cs` (and mark `ScenePainter` `partial`)
- Modify: `WaBiBaBuSy.UI/Controls/ScenePainter.cs` (`public sealed class ScenePainter` → `public sealed partial class ScenePainter`)
- Modify: `WaBiBaBuSy.UI/Controls/RoomView.cs` (styled properties ~line 40, `AffectsRender` ~line 129, render loop ~line 300, `DrawTile` ~line 343)
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (`OnServerPropertyChanged`), `WaBiBaBuSy.UI/Views/MainWindow.axaml` (RoomView element ~line 253)

**Interfaces:**
- Consumes: `ShowEventMath`, `SpotlightMath`, `CelebrationMath`, `TraversalMath`, `ScenePainter.ParseHex(string?)`, `ServerPlaybackHost.ActiveShowEvents`.
- Produces: `ScenePainter.PaintShowEvents(DrawingContext ctx, Rect rect, double s, NodeLayout node, IReadOnlyList<ShowEvent> events, long sceneStartUtcMs, long sceneRawElapsedMs, bool sharedCanvas)`; `RoomView.ShowEvents` styled property; `MainWindowViewModel.RoomShowEvents`.

- [ ] **Step 1: Painter**

```csharp
// WaBiBaBuSy.UI/Controls/ScenePainter.Events.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;

namespace WaBiBaBuSy.UI.Controls;

/// <summary>Live show events over a node tile, with the players' own math (roadmap 2.4).</summary>
public sealed partial class ScenePainter
{
    /// <summary>Confetti pieces drawn per tile at most (the preview is small; the players draw all).</summary>
    private const int MaxPreviewParticles = 150;

    private CelebrationParticle[] _particles = new CelebrationParticle[256];
    private readonly Dictionary<string, Bitmap?> _eventImages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Draws the active events of <paramref name="events"/> on one node (clipped to <paramref name="rect"/>), in the players' layer order.</summary>
    public void PaintShowEvents(DrawingContext ctx, Rect rect, double s, NodeLayout node, IReadOnlyList<ShowEvent> events,
        long sceneStartUtcMs, long sceneRawElapsedMs, bool sharedCanvas)
    {
        using var clip = ctx.PushClip(rect);
        for (int layer = 0; layer <= 2; layer++)
        {
            foreach (var evt in events)
            {
                int l = evt.Type == ShowEventType.Spotlight ? 1 : evt.Type == ShowEventType.Blackout ? 2 : 0;
                if (l != layer) continue;
                long local = ShowEventMath.LocalMs(evt, sceneStartUtcMs, sceneRawElapsedMs);
                if (!ShowEventMath.IsActive(evt, local)) continue;
                switch (evt.Type)
                {
                    case ShowEventType.Blackout:
                        ctx.FillRectangle(EventBrush(evt.ColorHex, ShowEventMath.BlackoutOpacity(evt, local)), rect);
                        break;
                    case ShowEventType.Spotlight:
                        PaintSpotlight(ctx, rect, s, node, evt, local, sharedCanvas);
                        break;
                    case ShowEventType.Celebration:
                        PaintCelebration(ctx, rect, s, node, evt, local);
                        break;
                    case ShowEventType.Traversal:
                    case ShowEventType.Announce:
                        PaintEventSprite(ctx, rect, s, node, evt, local);
                        break;
                }
            }
        }
    }

    private static IBrush EventBrush(string? hex, double opacity) => new SolidColorBrush(ParseHex(hex), Math.Clamp(opacity, 0, 1));

    private static void PaintSpotlight(DrawingContext ctx, Rect rect, double s, NodeLayout node, ShowEvent evt, long local, bool sharedCanvas)
    {
        var v = SpotlightMath.Visual(evt, node, sharedCanvas, local);
        if (v.IsTarget)
        {
            double w = v.BorderWidth * s;
            ctx.DrawRectangle(null, new Pen(EventBrush(evt.ColorHex, v.BorderOpacity), w), rect.Deflate(w / 2));
            return;
        }
        if (v.DimOpacity > 0) ctx.FillRectangle(new SolidColorBrush(Colors.Black, v.DimOpacity), rect);
        if (v.ChevronDirection == 0) return;
        double half = v.ChevronSize / 2 * s, d = v.ChevronDirection;
        double cx = rect.X + v.ChevronCenterX * s, cy = rect.Y + v.ChevronCenterY * s;
        var pen = new Pen(EventBrush(evt.ColorHex, v.ChevronOpacity), v.ChevronSize * 0.18 * s);
        ctx.DrawLine(pen, new Point(cx - d * half, cy - half), new Point(cx + d * half, cy));
        ctx.DrawLine(pen, new Point(cx - d * half, cy + half), new Point(cx + d * half, cy));
    }

    private void PaintCelebration(DrawingContext ctx, Rect rect, double s, NodeLayout node, ShowEvent evt, long local)
    {
        int count = CelebrationMath.Count(node.CanvasWidth > 0 ? node.CanvasWidth : node.Width);
        int capacity = CelebrationMath.Capacity(node, count);
        if (_particles.Length < capacity) _particles = new CelebrationParticle[capacity];
        int n = CelebrationMath.VisibleOnNode(evt, node, local, _particles);
        double env = ShowEventMath.Envelope(local, evt.DurationMs);
        int step = Math.Max(1, n / MaxPreviewParticles);
        for (int i = 0; i < n; i += step)
        {
            var p = _particles[i];
            var box = new Rect(rect.X + p.X * s, rect.Y + p.Y * s, Math.Max(1, p.Width * s), Math.Max(1, p.Height * s));
            var c = box.Center;
            var m = Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateRotation(p.RotationDeg * Math.PI / 180) * Matrix.CreateTranslation(c.X, c.Y);
            using (ctx.PushTransform(m))
                ctx.FillRectangle(EventBrush(CelebrationMath.ColorFor(evt, p.ColorIndex), env), box);
        }
    }

    private void PaintEventSprite(DrawingContext ctx, Rect rect, double s, NodeLayout node, ShowEvent evt, long local)
    {
        int cw = node.CanvasWidth > 0 ? node.CanvasWidth : node.Width;
        int ch = node.CanvasHeight > 0 ? node.CanvasHeight : node.Height;
        if (TraversalMath.Pose(evt, cw, ch, node.Wraps, local) is not { } p) return;
        Span<float> copies = stackalloc float[2];
        int n = NodeMapping.WrapCopies(p.VirtualX, p.Width, node, copies);
        double y = rect.Y + NodeMapping.ToLocalY(p.VirtualY, node) * s;
        using var opacity = ctx.PushOpacity(p.Opacity);
        for (int c = 0; c < n; c++)
        {
            float lx = NodeMapping.ToLocalX(copies[c], p.Width, node);
            if (!NodeMapping.IsVisible(lx, p.Width, node)) continue;
            var dest = new Rect(rect.X + lx * s, y, p.Width * s, p.Height * s);
            if (evt.Type == ShowEventType.Announce) PaintAnnounceText(ctx, dest, evt);
            else if (EventImage(evt.ImagePath) is { } bmp) ctx.DrawImage(bmp, new Rect(bmp.Size), dest);
            else ctx.DrawRectangle(EventBrush(evt.ColorHex, 0.5), null, dest);   // image not readable here: a placeholder box
        }
    }

    /// <summary>Announce text scaled into its box (Task 16 fills this in; until then a placeholder box).</summary>
    private static void PaintAnnounceText(DrawingContext ctx, Rect dest, ShowEvent evt) =>
        ctx.DrawRectangle(null, new Pen(EventBrush(evt.ColorHex, 0.8), 1), dest);

    private Bitmap? EventImage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_eventImages.TryGetValue(path, out var cached)) return cached;
        Bitmap? bmp = null;
        try { if (File.Exists(path)) bmp = new Bitmap(path); }
        catch (Exception) { bmp = null; }
        if (_eventImages.Count > 8) _eventImages.Clear();
        _eventImages[path] = bmp;
        return bmp;
    }
}
```

Note for the implementer: `PaintAnnounceText` draws the box outline now and is replaced in Task 16 — it is drawn only for Announce events, which cannot be fired before Task 16 adds the button.

- [ ] **Step 2: `RoomView`** (add `using System.Collections.Generic;` and `using WaBiBaBuSy.Models.Show;` if missing)

Styled property next to `ActiveLayoutProperty`:

```csharp
    /// <summary>Defines the <see cref="ShowEvents"/> property.</summary>
    public static readonly StyledProperty<IReadOnlyList<ShowEvent>?> ShowEventsProperty =
        AvaloniaProperty.Register<RoomView, IReadOnlyList<ShowEvent>?>(nameof(ShowEvents));

    /// <summary>Live one-shot events drawn over the animating tiles (Live mode only).</summary>
    public IReadOnlyList<ShowEvent>? ShowEvents { get => GetValue(ShowEventsProperty); set => SetValue(ShowEventsProperty, value); }
```

Add `ShowEventsProperty` to the `AffectsRender<RoomView>(...)` list. In the render loop, pass `live` into `DrawTile`:

```csharp
            DrawTile(ctx, tile, client, node, scene, elapsed, live);
```

Change the `DrawTile` signature to `private void DrawTile(DrawingContext ctx, TileBox t, ClientNodeViewModel? c, NodeLayout? node, CrossScreenConfig? scene, long elapsed, bool live)` and after `_painter.PaintSprite(ctx, screen, s, node, scene!, elapsed);`:

```csharp
            if (live && ShowEvents is { Count: > 0 } events)
                _painter.PaintShowEvents(ctx, screen, s, node, events, SharedStartUtcMs, elapsed,
                    sharedCanvas: scene!.DistributionMode == AnimationDistributionMode.Sequential);
```

- [ ] **Step 3: View model + binding**

`MainWindowViewModel` (add `using WaBiBaBuSy.Models.Show;`):

```csharp
    /// <summary>Live one-shot events for the room preview.</summary>
    public IReadOnlyList<ShowEvent> RoomShowEvents => _server.ActiveShowEvents;
```

In `OnServerPropertyChanged` add a case:

```csharp
            case nameof(ServerPlaybackHost.ActiveShowEvents):
                OnPropertyChanged(nameof(RoomShowEvents));
                break;
```

`MainWindow.axaml`, on the `controls:RoomView` element add:

```xml
                                               ShowEvents="{Binding RoomShowEvents}"
```

- [ ] **Step 4: Build, commit**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors.

```bash
git add WaBiBaBuSy.UI/Controls/ScenePainter.cs WaBiBaBuSy.UI/Controls/ScenePainter.Events.cs WaBiBaBuSy.UI/Controls/RoomView.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs WaBiBaBuSy.UI/Views/MainWindow.axaml
git commit -m "feat(preview): room view paints live show events with the players' math

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Events strip in the main window

**Files:**
- Create: `WaBiBaBuSy.UI/ViewModels/ShowEventsViewModel.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs` (property, ctor, `SetStorageProvider` ~line 622, `Detach` ~line 663, `OnServerPropertyChanged`, `OnSelectedClientChanged`)
- Modify: `WaBiBaBuSy.UI/Views/MainWindow.axaml` (left column grid `RowDefinitions="Auto,Auto,*,Auto,Auto,Auto"` ~line 187 → one more `Auto` row; the strip goes between the preview bar (row 3) and the selection bar)

**Interfaces:**
- Consumes: `ServerPlaybackHost.{FireEventAsync, CanFireEvents, EventStatusText, EventSettings, SaveEventSettings, PrefetchEventImageAsync, ActiveLayout}`, `MainWindowViewModel.Clients` (`ClientId`, `DisplayName`).
- Produces: `ShowEventsViewModel { ObservableCollection<SeatChoice> Seats; SeatChoice? SelectedSeat; string TraversalImageName; string AnnounceText; bool CanFire; string? StatusText; IAsyncRelayCommand SpotlightCommand, CelebrationCommand, TraversalCommand, BlackoutCommand, PickTraversalImageCommand; IRelayCommand ClearTraversalImageCommand; void RefreshSeats(IReadOnlyList<SeatChoice>); void SelectSeat(string? id); void SetStorageProvider(IStorageProvider); void Detach() }`; `sealed record SeatChoice(string Id, string Label)`; `MainWindowViewModel.Events`.

- [ ] **Step 1: View model**

```csharp
// WaBiBaBuSy.UI/ViewModels/ShowEventsViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.UI.Services;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>One seat in the Spotlight picker.</summary>
public sealed record SeatChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The Events strip under the room view: one-shot events on every seat (roadmap 2.4).</summary>
public partial class ShowEventsViewModel : ObservableObject
{
    private readonly ServerPlaybackHost _server;
    private IStorageProvider? _storageProvider;

    public ShowEventsViewModel(ServerPlaybackHost server)
    {
        _server = server;
        _server.PropertyChanged += OnServerPropertyChanged;
        _announceText = server.EventSettings.AnnounceText;
    }

    public ObservableCollection<SeatChoice> Seats { get; } = new();

    [ObservableProperty] private SeatChoice? _selectedSeat;
    [ObservableProperty] private string _announceText;

    public bool CanFire => _server.CanFireEvents;
    public string? StatusText => _server.EventStatusText;

    /// <summary>File name of the Traversal image, or the fallback.</summary>
    public string TraversalImageName => string.IsNullOrWhiteSpace(_server.EventSettings.TraversalImagePath)
        ? "scene sprite"
        : Path.GetFileName(_server.EventSettings.TraversalImagePath);

    public void SetStorageProvider(IStorageProvider storageProvider) => _storageProvider = storageProvider;

    /// <summary>Seats of the running layout in traversal order; keeps the selection when it still exists.</summary>
    public void RefreshSeats(IReadOnlyList<SeatChoice> seats)
    {
        var keep = SelectedSeat?.Id ?? _server.EventSettings.LastTargetSeatId;
        Seats.Clear();
        foreach (var seat in seats) Seats.Add(seat);
        SelectedSeat = Seats.FirstOrDefault(s => s.Id == keep) ?? Seats.FirstOrDefault();
    }

    /// <summary>The room view's selected node becomes the Spotlight target (when it is a seat of the running layout).</summary>
    public void SelectSeat(string? id)
    {
        var seat = Seats.FirstOrDefault(s => s.Id == id);
        if (seat != null) SelectedSeat = seat;
    }

    partial void OnSelectedSeatChanged(SeatChoice? value)
    {
        if (value == null || value.Id == _server.EventSettings.LastTargetSeatId) return;
        _server.EventSettings.LastTargetSeatId = value.Id;   // the Spotlight hotkey uses it
        _server.SaveEventSettings(_server.EventSettings);
    }

    partial void OnAnnounceTextChanged(string value)
    {
        _server.EventSettings.AnnounceText = value;
        _server.SaveEventSettings(_server.EventSettings);
    }

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Spotlight() => _server.FireEventAsync(ShowEventType.Spotlight, SelectedSeat?.Id);

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Celebration() => _server.FireEventAsync(ShowEventType.Celebration);

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Traversal() => _server.FireEventAsync(ShowEventType.Traversal);

    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Blackout() => _server.FireEventAsync(ShowEventType.Blackout);

    [RelayCommand]
    private async Task PickTraversalImage()
    {
        if (_storageProvider == null) return;
        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Traversal image",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.gif", "*.jpg", "*.jpeg", "*.bmp" } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        _server.EventSettings.TraversalImagePath = path;
        _server.SaveEventSettings(_server.EventSettings);
        OnPropertyChanged(nameof(TraversalImageName));
        _ = _server.PrefetchEventImageAsync();
    }

    [RelayCommand]
    private void ClearTraversalImage()
    {
        _server.EventSettings.TraversalImagePath = string.Empty;
        _server.SaveEventSettings(_server.EventSettings);
        OnPropertyChanged(nameof(TraversalImageName));
    }

    private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ServerPlaybackHost.CanFireEvents):
            case nameof(ServerPlaybackHost.ActiveScene):
            case nameof(ServerPlaybackHost.ActiveLayout):
                OnPropertyChanged(nameof(CanFire));
                SpotlightCommand.NotifyCanExecuteChanged();
                CelebrationCommand.NotifyCanExecuteChanged();
                TraversalCommand.NotifyCanExecuteChanged();
                BlackoutCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ServerPlaybackHost.EventStatusText):
                OnPropertyChanged(nameof(StatusText));
                break;
        }
    }

    /// <summary>The window closed: stop listening to the app-lifetime host.</summary>
    public void Detach() => _server.PropertyChanged -= OnServerPropertyChanged;
}
```

- [ ] **Step 2: Wire into `MainWindowViewModel`**

```csharp
    /// <summary>The Events strip (one-shot show events).</summary>
    public ShowEventsViewModel Events { get; }
```

In the constructor, right after `_server = server;`: `Events = new ShowEventsViewModel(server);` and after the existing subscriptions: `RefreshEventSeats();`.

In `SetStorageProvider` add `Events.SetStorageProvider(storageProvider);`. In `Detach()` add `Events.Detach();`.

In `OnServerPropertyChanged`, in the existing `case nameof(ServerPlaybackHost.ActiveLayout):` add `RefreshEventSeats();`.

Selected node → Spotlight target:

```csharp
    partial void OnSelectedClientChanged(ClientNodeViewModel? value) => Events.SelectSeat(value?.ClientId);
```

(If a partial `OnSelectedClientChanged` already exists, add the call to it.)

```csharp
    /// <summary>Spotlight picker: the running layout's seats in traversal order, labeled with the node names.</summary>
    private void RefreshEventSeats()
    {
        var layout = _server.ActiveLayout;
        var names = Clients.GroupBy(c => c.ClientId).ToDictionary(g => g.Key, g => g.First().DisplayName);
        var seats = layout == null
            ? new List<SeatChoice>()
            : layout.Nodes.OrderBy(n => n.Order)
                .Select(n => new SeatChoice(n.Id, $"{n.Order + 1}. {names.GetValueOrDefault(n.Id, n.Id)}"))
                .ToList();
        Events.RefreshSeats(seats);
    }
```

- [ ] **Step 3: The strip in `MainWindow.axaml`**

Change the left column grid's `RowDefinitions="Auto,Auto,*,Auto,Auto,Auto"` to `RowDefinitions="Auto,Auto,*,Auto,Auto,Auto,Auto"`, bump `Grid.Row` of the selection bar (4→5) and the client log viewer (5→6), and insert at `Grid.Row="4"`:

```xml
                    <!-- Events strip: one-shot moments on every seat (also Ctrl+Alt+1..5) -->
                    <Border Grid.Row="4" Background="#252526" CornerRadius="5" Padding="10,6" Margin="0,8,0,0"
                            IsVisible="{Binding IsServerMode}">
                        <StackPanel Spacing="4">
                            <WrapPanel>
                                <TextBlock Text="Events" FontWeight="SemiBold" FontSize="12" VerticalAlignment="Center" Margin="0,0,10,0"/>
                                <Button Content="★ Spotlight" Command="{Binding Events.SpotlightCommand}" Padding="8,3" FontSize="11" Margin="0,0,4,0"
                                        ToolTip.Tip="Spotlight the picked seat: its border pulses, every other seat dims and points to it (Ctrl+Alt+1)"/>
                                <ComboBox ItemsSource="{Binding Events.Seats}" SelectedItem="{Binding Events.SelectedSeat}"
                                          MinWidth="140" FontSize="11" Margin="0,0,10,0" ToolTip.Tip="Spotlight target (the selected node in the room)"/>
                                <Button Content="✦ Celebration" Command="{Binding Events.CelebrationCommand}" Padding="8,3" FontSize="11" Margin="0,0,4,0"
                                        ToolTip.Tip="Confetti burst over the whole room (Ctrl+Alt+2)"/>
                                <Button Content="↻ Traversal" Command="{Binding Events.TraversalCommand}" Padding="8,3" FontSize="11" Margin="0,0,2,0"
                                        ToolTip.Tip="The image runs around the room exactly once (Ctrl+Alt+3)"/>
                                <Button Content="{Binding Events.TraversalImageName}" Command="{Binding Events.PickTraversalImageCommand}"
                                        Padding="6,3" FontSize="10" Margin="0,0,2,0" ToolTip.Tip="Pick the traversal image (default: the scene's sprite)"/>
                                <Button Content="✕" Command="{Binding Events.ClearTraversalImageCommand}" Padding="4,3" FontSize="10" Margin="0,0,10,0"
                                        ToolTip.Tip="Use the scene's sprite"/>
                                <Button Content="■ Blackout" Command="{Binding Events.BlackoutCommand}" Padding="8,3" FontSize="11"
                                        ToolTip.Tip="Every seat fades to black (Ctrl+Alt+4)"/>
                            </WrapPanel>
                            <TextBlock Text="{Binding Events.StatusText}" Foreground="#AAAAAA" FontSize="10"
                                       IsVisible="{Binding Events.StatusText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
                        </StackPanel>
                    </Border>
```

- [ ] **Step 4: Build, commit**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → green.

```bash
git add WaBiBaBuSy.UI/ViewModels/ShowEventsViewModel.cs WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs WaBiBaBuSy.UI/Views/MainWindow.axaml
git commit -m "feat(ui): Events strip (spotlight + seat picker, celebration, traversal + image, blackout)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Global hotkeys

**Files:**
- Create: `WaBiBaBuSy.Models/Show/ShowEventHotkeys.cs`, `WaBiBaBuSy.UI/Services/GlobalHotkeyService.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs` (constructor after `_serverPlayback = …` ~line 92; `ExitAsync` ~line 213)
- Test: `WaBiBaBuSy.Tests/ShowEventHotkeysTests.cs`

**Interfaces:**
- Produces: `ShowEventHotkeys.Slots : IReadOnlyList<int>` (1..5), `ShowEventType? TypeForSlot(int slot)`, `string Gesture(int slot)` ("Ctrl+Alt+N"); `GlobalHotkeyService { event Action<int>? Pressed; IReadOnlyList<int> FailedSlots; bool IsRunning; void Start(); void Stop(); void Dispose() }`.

- [ ] **Step 1: Write the failing test**

```csharp
// WaBiBaBuSy.Tests/ShowEventHotkeysTests.cs
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventHotkeysTests
{
    [Theory]
    [InlineData(1, ShowEventType.Spotlight)]
    [InlineData(2, ShowEventType.Celebration)]
    [InlineData(3, ShowEventType.Traversal)]
    [InlineData(4, ShowEventType.Blackout)]
    [InlineData(5, ShowEventType.Announce)]
    public void Slot_MapsToType(int slot, ShowEventType type) => Assert.Equal(type, ShowEventHotkeys.TypeForSlot(slot));

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void UnknownSlot_IsNull(int slot) => Assert.Null(ShowEventHotkeys.TypeForSlot(slot));

    [Fact]
    public void Gesture_Text() => Assert.Equal("Ctrl+Alt+3", ShowEventHotkeys.Gesture(3));

    [Fact]
    public void Slots_AreOneToFive() => Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ShowEventHotkeys.Slots);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventHotkeysTests"` → build error.

- [ ] **Step 3: Implement the mapping**

```csharp
// WaBiBaBuSy.Models/Show/ShowEventHotkeys.cs
namespace WaBiBaBuSy.Models.Show;

/// <summary>Global hotkeys of the server (Ctrl+Alt+1..5) and the event each fires.</summary>
public static class ShowEventHotkeys
{
    private static readonly ShowEventType[] Types =
        { ShowEventType.Spotlight, ShowEventType.Celebration, ShowEventType.Traversal, ShowEventType.Blackout, ShowEventType.Announce };

    public static IReadOnlyList<int> Slots { get; } = Enumerable.Range(1, Types.Length).ToArray();

    public static ShowEventType? TypeForSlot(int slot) => slot >= 1 && slot <= Types.Length ? Types[slot - 1] : null;

    public static string Gesture(int slot) => $"Ctrl+Alt+{slot}";
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ShowEventHotkeysTests"` → PASS.

- [ ] **Step 5: Win32 service**

```csharp
// WaBiBaBuSy.UI/Services/GlobalHotkeyService.cs
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.UI.Services;

/// <summary>
/// System-wide Ctrl+Alt+1..5 for show events. Runs a dedicated thread with its own message queue:
/// <c>RegisterHotKey(NULL, …)</c> posts WM_HOTKEY to the registering thread, so no window is needed and the keys
/// work while the main window is closed. Started only while the server runs (clients never grab the keys).
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012, PM_NOREMOVE = 0x0000;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;

    private readonly object _gate = new();
    private Thread? _thread;
    private uint _threadId;

    /// <summary>A hotkey slot (1..5) was pressed. Raised on the hotkey thread — marshal to the UI thread.</summary>
    public event Action<int>? Pressed;

    /// <summary>Slots another application already holds (not registered).</summary>
    public IReadOnlyList<int> FailedSlots { get; private set; } = Array.Empty<int>();

    public bool IsRunning { get { lock (_gate) return _thread != null; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread != null) return;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "GlobalHotkeys" };
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));
        }
    }

    private void Loop(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);   // create this thread's queue before Stop can post to it
        var failed = new List<int>();
        foreach (int slot in ShowEventHotkeys.Slots)
            if (!RegisterHotKey(IntPtr.Zero, slot, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)('0' + slot)))
                failed.Add(slot);
        FailedSlots = failed;
        ready.Set();
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message != WM_HOTKEY) continue;
                try { Pressed?.Invoke((int)msg.wParam); }
                catch (Exception ex) { Console.Error.WriteLine($"[Hotkeys] Handler failed: {ex.Message}"); }
            }
        }
        finally
        {
            foreach (int slot in ShowEventHotkeys.Slots) UnregisterHotKey(IntPtr.Zero, slot);
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
        }
        if (thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
        FailedSlots = Array.Empty<int>();
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
```

- [ ] **Step 6: Tray ownership** (`TrayViewModel`; add `using WaBiBaBuSy.Models.Show;`)

Field: `private readonly GlobalHotkeyService _hotkeys = new();`

In the constructor after `_serverPlayback = new ServerPlaybackHost(...)`:

```csharp
        // Ctrl+Alt+1..5 fire show events — registered only while this machine runs the server.
        _hotkeys.Pressed += slot => Dispatcher.UIThread.Post(() => _ = OnEventHotkeyAsync(slot));
        _service.ServerStatusChanged += (_, e) => Dispatcher.UIThread.Post(() => UpdateHotkeys(e.IsRunning));
        UpdateHotkeys(_service.IsServerRunning);
```

Methods:

```csharp
    private void UpdateHotkeys(bool serverRunning)
    {
        if (serverRunning && _serverPlayback.EventSettings.HotkeysEnabled)
        {
            _hotkeys.Start();
            if (_hotkeys.FailedSlots.Count > 0)
                _serverPlayback.EventStatusText = $"Hotkey(s) taken by another app: {string.Join(", ", _hotkeys.FailedSlots.Select(ShowEventHotkeys.Gesture))}";
        }
        else
        {
            _hotkeys.Stop();
        }
    }

    private async Task OnEventHotkeyAsync(int slot)
    {
        if (ShowEventHotkeys.TypeForSlot(slot) is not { } type) return;
        await _serverPlayback.FireEventAsync(type, _serverPlayback.EventSettings.LastTargetSeatId);
    }
```

In `ExitAsync`, before stopping the players: `_hotkeys.Dispose();`

- [ ] **Step 7: Build, tests, commit**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → green.

```bash
git add WaBiBaBuSy.Models/Show/ShowEventHotkeys.cs WaBiBaBuSy.UI/Services/GlobalHotkeyService.cs WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs WaBiBaBuSy.Tests/ShowEventHotkeysTests.cs
git commit -m "feat(events): global hotkeys Ctrl+Alt+1..5 while the server runs

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 15: `showEvent` scenario step and the showcase

**Files:**
- Modify: `WaBiBaBuSy.Models/Testing/TestScenario.cs` (derived-type list ~line 44, new class after `ExactFrameStep`)
- Modify: `WaBiBaBuSy.Models/Testing/ScenarioLoader.cs` (step validation switch ~line 109)
- Modify: `WaBiBaBuSy.Core/Services/Testing/ITestHost.cs`, `WaBiBaBuSy.Core/Services/Testing/TestRunner.cs` (`RunStepAsync` ~line 373), `WaBiBaBuSy.UI/Services/Testing/TestHostAdapter.cs`
- Modify: `WaBiBaBuSy.Tests/TestRunnerTests.cs` (`FakeHost`), `WaBiBaBuSy.Tests/TestRunCoordinatorTests.cs` (`EmptyHost`), `WaBiBaBuSy.Tests/ScenarioLoaderTests.cs`
- Create: `TestScenarios/showcase/events.json`

**Interfaces:**
- Consumes: `ShowEvent`, `ShowEventMath.ClampDuration`, `ServerPlaybackHost.FireEventForTestAsync`, `SceneStartInfo.SharedStartServerUtcMs`, `SceneStartInfo.Layout`.
- Produces: `ShowEventStep : TestStep { ShowEvent Event; long AtElapsedMs; Kind = "showEvent" }`; `ITestHost.FireShowEventAsync(ShowEvent evt, CancellationToken ct) : Task`.

- [ ] **Step 1: Write the failing tests**

Append to `ScenarioLoaderTests`:

```csharp
    [Fact]
    public void Parse_ShowEventStep()
    {
        var json = """
        { "name": "x", "steps": [
          { "type": "playScene", "scene": "scenes/linear.json" },
          { "type": "showEvent", "atElapsedMs": 2000, "event": { "type": "Celebration", "durationMs": 4000, "seed": 5 } } ] }
        """;
        var step = Assert.IsType<ShowEventStep>(ScenarioLoader.Parse(json, _dir).Steps[1]);
        Assert.Equal(2000, step.AtElapsedMs);
        Assert.Equal(WaBiBaBuSy.Models.Show.ShowEventType.Celebration, step.Event.Type);
        Assert.Equal(5, step.Event.Seed);
    }

    [Theory]
    [InlineData("""{ "type": "showEvent", "atElapsedMs": -1, "event": { "type": "Blackout", "durationMs": 1000 } }""", "atElapsedMs must be >= 0")]
    [InlineData("""{ "type": "showEvent", "event": { "type": "Spotlight", "durationMs": 1000 } }""", "targetOrder")]
    [InlineData("""{ "type": "showEvent", "event": { "type": "Traversal", "durationMs": 1000, "imagePath": "nope.png" } }""", "image not found")]
    [InlineData("""{ "type": "showEvent", "event": { "type": "Announce", "durationMs": 1000 } }""", "text")]
    public void Parse_Invalid_ShowEvent(string step, string expected)
    {
        var json = $$"""{ "name": "x", "steps": [ { "type": "playScene", "scene": "scenes/linear.json" }, {{step}} ] }""";
        Assert.Contains(expected, Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir)).Message);
    }

    [Fact]
    public void Parse_Invalid_ShowEventBeforeAnyScene()
    {
        var json = """{ "name": "x", "steps": [ { "type": "showEvent", "event": { "type": "Blackout", "durationMs": 1000 } } ] }""";
        Assert.Contains("needs a playScene step before it", Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, _dir)).Message);
    }
```

In `TestRunnerTests.FakeHost` add:

```csharp
        public List<WaBiBaBuSy.Models.Show.ShowEvent> FiredEvents { get; } = new();
        public Task FireShowEventAsync(WaBiBaBuSy.Models.Show.ShowEvent evt, CancellationToken ct) { FiredEvents.Add(evt); return Task.CompletedTask; }
```

and the test:

```csharp
    [Fact]
    public async Task ShowEventStep_FiresAtSharedStartPlusOffset_WithAFreshId()
    {
        var report = await Runner().RunAsync(Scenario($$"""
            {{PlayMarker}},
            { "type": "showEvent", "atElapsedMs": 1500, "event": { "type": "Celebration", "durationMs": 99999999, "seed": 3, "eventId": "fixed" } }
            """, minRemotes: 0), "unit.json", CancellationToken.None);

        Assert.False(report.Aborted, report.AbortReason);
        var fired = Assert.Single(_host.FiredEvents);
        Assert.Equal(FakeTransport.SharedStart + 1500, fired.T0Utc);
        Assert.NotEqual("fixed", fired.EventId);                                       // reruns must not be deduped by the players
        Assert.Equal(WaBiBaBuSy.Models.Show.ShowEventMath.MaxDurationMs, fired.DurationMs);   // clamped
        Assert.Equal(Verdict.Pass, report.Steps[1].Verdict);
    }
```

In `TestRunCoordinatorTests.EmptyHost` add:

```csharp
        public Task FireShowEventAsync(WaBiBaBuSy.Models.Show.ShowEvent evt, CancellationToken ct) => Task.CompletedTask;
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ScenarioLoaderTests|FullyQualifiedName~TestRunnerTests|FullyQualifiedName~TestRunCoordinatorTests"`
Expected: build error — `ShowEventStep`, `ITestHost.FireShowEventAsync` do not exist.

- [ ] **Step 3: Model + validation**

`TestScenario.cs` — add `[JsonDerivedType(typeof(ShowEventStep), "showEvent")]` to the list on `TestStep`, and (with `using WaBiBaBuSy.Models.Show;`):

```csharp
/// <summary>Fire a one-shot show event at <see cref="AtElapsedMs"/> of the running scene (T0 = shared start + offset).</summary>
public sealed class ShowEventStep : TestStep
{
    /// <summary>The event; <c>eventId</c> and <c>t0Utc</c> are replaced by the runner, <c>imagePath</c> is relative to the scenario.</summary>
    public ShowEvent Event { get; set; } = new();
    public long AtElapsedMs { get; set; }
    public override string Kind => "showEvent";
}
```

`ScenarioLoader` validation switch, before `case WaitStep wait:`:

```csharp
                case ShowEventStep ev:
                    if (ev.Event == null) { errors.Add($"{at}: event is missing"); break; }
                    if (ev.AtElapsedMs < 0) errors.Add($"{at}: atElapsedMs must be >= 0");
                    if (ev.Event.Type == Show.ShowEventType.Spotlight && ev.Event.TargetOrder < 0)
                        errors.Add($"{at}: a Spotlight needs targetOrder (the seat's traversal index, 0 = first)");
                    if (ev.Event.Type == Show.ShowEventType.Announce && string.IsNullOrWhiteSpace(ev.Event.Text))
                        errors.Add($"{at}: an Announce needs text");
                    if (ev.Event.Type == Show.ShowEventType.Traversal)
                    {
                        if (string.IsNullOrWhiteSpace(ev.Event.ImagePath)) errors.Add($"{at}: a Traversal needs imagePath");
                        else if (IsRemoteOrDevicePath(ev.Event.ImagePath)) errors.Add($"{at}: imagePath {LocalOnly}");
                        else if (!File.Exists(ResolvePath(scenario, ev.Event.ImagePath))) errors.Add($"{at}: image not found: {ResolvePath(scenario, ev.Event.ImagePath)}");
                    }
                    RequireScene(scenePlaying, at, errors);
                    break;
```

(`ScenarioLoader` lives in `WaBiBaBuSy.Models.Testing`, so `Show.ShowEventType` resolves to `WaBiBaBuSy.Models.Show.ShowEventType`; alternatively add `using WaBiBaBuSy.Models.Show;` and drop the prefix.)

- [ ] **Step 4: Host interface, runner, adapter**

`ITestHost` (add `using WaBiBaBuSy.Models.Show;`):

```csharp
    /// <summary>Fire a prepared show event (T0 set) on every node, like the Events strip.</summary>
    Task FireShowEventAsync(ShowEvent evt, CancellationToken ct);
```

`TestRunner.RunStepAsync`, before `case WaitStep wait:` (add `using WaBiBaBuSy.Models.Show;`):

```csharp
            case ShowEventStep ev:
                if (ctx.Active == null)
                {
                    sr.Verdict = Verdict.Fail;
                    sr.Message = "no scene is playing";
                    break;
                }
                var evt = ev.Event.Clone();
                evt.EventId = $"test-{Guid.NewGuid():N}";   // players dedupe by id: a rerun must not be ignored
                evt.T0Utc = ctx.Active.SharedStartServerUtcMs + ev.AtElapsedMs;
                evt.DurationMs = ShowEventMath.ClampDuration(evt.DurationMs);
                if (!string.IsNullOrEmpty(evt.ImagePath)) evt.ImagePath = ScenarioLoader.ResolvePath(ctx.Scenario, evt.ImagePath);
                if (evt.Type == ShowEventType.Spotlight && ctx.Active.Layout?.Nodes.FirstOrDefault(n => n.Order == evt.TargetOrder) is { } target)
                    evt.TargetCenterX = target.OffsetX + target.Width / 2f;
                await _host.FireShowEventAsync(evt, ct);
                sr.Verdict = Verdict.Pass;
                sr.Message = $"{evt.Type} at elapsed {ev.AtElapsedMs} ms for {evt.DurationMs} ms";
                break;
```

`TestHostAdapter` (add `using Avalonia.Threading;` and `using WaBiBaBuSy.Models.Show;`):

```csharp
    public Task FireShowEventAsync(ShowEvent evt, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() => _host.FireEventForTestAsync(evt));
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~ScenarioLoaderTests|FullyQualifiedName~TestRunnerTests|FullyQualifiedName~TestRunCoordinatorTests"` → PASS.

- [ ] **Step 6: Showcase scenario**

```json
// TestScenarios/showcase/events.json
{
  // One-shot show events (roadmap 2.4): each event is fired at a fixed scene time; the exact frames fall inside
  // its window, so they show the event exactly as live (the frames are pure functions of the elapsed time).
  "name": "events",
  "requires": { "minRemoteNodes": 0 },
  "steps": [
    { "type": "testMode", "timecode": false },
    { "type": "playScene", "label": "02-dvd-bounce", "scene": "scenes/02-dvd-bounce.json" },

    { "type": "showEvent", "label": "celebration", "atElapsedMs": 1000,
      "event": { "type": "Celebration", "durationMs": 5000, "seed": 2026, "colorHex": "#FFD700" } },
    { "type": "exactFrame", "label": "celebration t=+0.5s (rising)", "elapsedMs": 1500 },
    { "type": "exactFrame", "label": "celebration t=+2.5s (falling)", "elapsedMs": 3500 },

    { "type": "showEvent", "label": "spotlight seat 1", "atElapsedMs": 7000,
      "event": { "type": "Spotlight", "durationMs": 4000, "targetOrder": 0, "colorHex": "#FFD700" } },
    { "type": "exactFrame", "label": "spotlight t=+1s", "elapsedMs": 8000 },

    { "type": "showEvent", "label": "traversal cat", "atElapsedMs": 12000,
      "event": { "type": "Traversal", "durationMs": 6000, "imagePath": "assets/cat.gif", "spriteAspect": 1.0, "heightFraction": 0.4 } },
    { "type": "exactFrame", "label": "traversal t=+1.5s", "elapsedMs": 13500 },
    { "type": "exactFrame", "label": "traversal t=+4.5s", "elapsedMs": 16500 },

    { "type": "showEvent", "label": "blackout", "atElapsedMs": 19000,
      "event": { "type": "Blackout", "durationMs": 2000, "colorHex": "#000000" } },
    { "type": "exactFrame", "label": "blackout t=+0.125s (fading in)", "elapsedMs": 19125 },
    { "type": "exactFrame", "label": "blackout t=+1s (full)", "elapsedMs": 20000 },

    { "type": "stop" }
  ]
}
```

(`cat.gif` is 320 × 320, so `spriteAspect` is 1.0.)

- [ ] **Step 7: Full build + tests, commit**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → green. Do not run the scenario (it needs the app) — the user runs it (checklist, Task 18).

```bash
git add WaBiBaBuSy.Models/Testing WaBiBaBuSy.Core/Services/Testing WaBiBaBuSy.UI/Services/Testing/TestHostAdapter.cs WaBiBaBuSy.Tests/ScenarioLoaderTests.cs WaBiBaBuSy.Tests/TestRunnerTests.cs WaBiBaBuSy.Tests/TestRunCoordinatorTests.cs TestScenarios/showcase/events.json
git commit -m "feat(test-mode): showEvent scenario step; showcase scenario with exact frames per event

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 16: Announce (text lap)

**Files:**
- Create: `WaBiBaBuSy.WallpaperEngine/Show/TextSprite.cs`
- Modify: `WaBiBaBuSy.Player.D2D/Program.ShowEvents.cs` (`StartEventSpriteDecode`)
- Modify: `WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs` (`FireEventAsync`)
- Modify: `WaBiBaBuSy.UI/Controls/ScenePainter.Events.cs` (`PaintAnnounceText`)
- Modify: `WaBiBaBuSy.UI/ViewModels/ShowEventsViewModel.cs`, `WaBiBaBuSy.UI/Views/MainWindow.axaml` (Announce button + text box)

**Interfaces:**
- Produces: `TextSprite.FontPx`, `TextSprite.MeasureAspect(string text) : float`, `TextSprite.Render(string text, string colorHex) : System.Drawing.Bitmap`; `ShowEventsViewModel.AnnounceCommand`.

Verification: build; manual check (Task 18). The pose math is Task 5's (tested).

- [ ] **Step 1: Text sprite (GDI+, shared by server and player)**

```csharp
// WaBiBaBuSy.WallpaperEngine/Show/TextSprite.cs
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.WallpaperEngine.Show;

/// <summary>
/// Announce text as a bitmap (the player has no DirectWrite). The server measures the aspect with the same routine
/// and sends it in the event; every node draws its bitmap into the box aspect × height, so positions agree even if a
/// node's font metrics differ slightly.
/// </summary>
public static class TextSprite
{
    /// <summary>Rendered font size; the bitmap is scaled into the event's box when drawn.</summary>
    public const float FontPx = 160f;
    private const int MaxWidthPx = 8192;
    private const string FontFamilyName = "Segoe UI";

    private static Font CreateFont() => new(FontFamilyName, FontPx, FontStyle.Bold, GraphicsUnit.Pixel);

    /// <summary>Width ÷ height of the rendered text (0.2 … 20); 1 for empty text.</summary>
    public static float MeasureAspect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1f;
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        using var font = CreateFont();
        var size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
        return size.Height > 0 ? Math.Clamp(size.Width / size.Height, 0.2f, 20f) : 1f;
    }

    /// <summary>The text in <paramref name="colorHex"/> on transparent, anti-aliased. Caller disposes.</summary>
    public static Bitmap Render(string text, string colorHex)
    {
        using var font = CreateFont();
        SizeF size;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
            size = pg.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);

        int w = Math.Clamp((int)Math.Ceiling(size.Width), 1, MaxWidthPx);
        int h = Math.Max(1, (int)Math.Ceiling(size.Height));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var (r, gr, b) = ShowEventMath.ParseColor(colorHex);
        using var brush = new SolidBrush(Color.FromArgb(255, r, gr, b));
        // Wider than MaxWidthPx: squeeze horizontally so the whole text fits (drawn into the event box anyway).
        if (size.Width > w) g.ScaleTransform(w / size.Width, 1f);
        g.DrawString(text, font, brush, PointF.Empty, StringFormat.GenericTypographic);
        return bmp;
    }
}
```

- [ ] **Step 2: Player decode branch** — in `StartEventSpriteDecode`, extend the switch (add `using WaBiBaBuSy.WallpaperEngine.Show;`):

```csharp
            ShowEventType.Announce when !string.IsNullOrWhiteSpace(evt.Text) => () => new EventSpriteFrames
            {
                Frames = new List<Bitmap> { TextSprite.Render(evt.Text!, evt.ColorHex) },
                DelaysMs = new List<int> { 100 },
            },
```

- [ ] **Step 3: Server measures the text** — in `ServerPlaybackHost.FireEventAsync`, after the Traversal `if` (add `using WaBiBaBuSy.WallpaperEngine.Show;`):

```csharp
        if (type == ShowEventType.Announce)
        {
            text = settings.AnnounceText;
            aspect = TextSprite.MeasureAspect(text);
        }
```

- [ ] **Step 4: Preview text** — replace `PaintAnnounceText` in `ScenePainter.Events.cs`:

```csharp
    /// <summary>Announce text scaled into its box (same box the players draw their text bitmap into).</summary>
    private static void PaintAnnounceText(DrawingContext ctx, Rect dest, ShowEvent evt)
    {
        var ft = new FormattedText(evt.Text ?? string.Empty, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 100, EventBrush(evt.ColorHex, 1));
        if (ft.Width <= 0 || ft.Height <= 0) return;
        var m = Matrix.CreateScale(dest.Width / ft.Width, dest.Height / ft.Height) * Matrix.CreateTranslation(dest.X, dest.Y);
        using (ctx.PushTransform(m))
            ctx.DrawText(ft, new Point(0, 0));
    }
```

- [ ] **Step 5: UI** — `ShowEventsViewModel`:

```csharp
    [RelayCommand(CanExecute = nameof(CanFire))]
    private Task Announce() => _server.FireEventAsync(ShowEventType.Announce);
```

and add `AnnounceCommand.NotifyCanExecuteChanged();` to the CanFire block in `OnServerPropertyChanged`. In the Events strip `WrapPanel` (after Blackout):

```xml
                                <TextBox Text="{Binding Events.AnnounceText}" Watermark="Announcement…" Width="180" FontSize="11" Margin="10,0,4,0"/>
                                <Button Content="✎ Announce" Command="{Binding Events.AnnounceCommand}" Padding="8,3" FontSize="11"
                                        ToolTip.Tip="The text runs around the room once (Ctrl+Alt+5)"/>
```

- [ ] **Step 6: Showcase** — append to `events.json` before `stop` (server measures the aspect only when fired from the UI; in the scenario set it explicitly):

```json
    { "type": "showEvent", "label": "announce", "atElapsedMs": 22000,
      "event": { "type": "Announce", "durationMs": 6000, "text": "Round 3 starts in 5 min", "spriteAspect": 9.5, "heightFraction": 0.25, "colorHex": "#FFFFFF" } },
    { "type": "exactFrame", "label": "announce t=+3s", "elapsedMs": 25000 },
```

- [ ] **Step 7: Build, tests, commit**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → green.

```bash
git add WaBiBaBuSy.WallpaperEngine/Show/TextSprite.cs WaBiBaBuSy.Player.D2D/Program.ShowEvents.cs WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs WaBiBaBuSy.UI/Controls/ScenePainter.Events.cs WaBiBaBuSy.UI/ViewModels/ShowEventsViewModel.cs WaBiBaBuSy.UI/Views/MainWindow.axaml TestScenarios/showcase/events.json
git commit -m "feat(events): Announce — text lap around the room (GDI+ text sprite, server-measured box)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 17 (optional, only if time allows): Scheduled playlist interrupts

**Files:**
- Modify: `WaBiBaBuSy.Models/Wallpaper/Playlist.cs` (class `Playlist`, new class `PlaylistInterrupt`)
- Modify: `WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs` (constructor, `StartCore`, `StopAsync`)
- Modify: `WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs` (`new PlaylistOrchestrator(…)` ~line 476)
- Modify: `WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs` (`BuildPlaylist`, `LoadFrom`), `WaBiBaBuSy.UI/Views/PlaylistPanel.axaml` (header `WrapPanel`)
- Test: `WaBiBaBuSy.Tests/PlaylistInterruptTests.cs`

**Interfaces:**
- Produces: `PlaylistInterrupt { bool Enabled = true; int EveryMs = 1_800_000; ShowEventType Type = Traversal }`; `Playlist.Interrupts : List<PlaylistInterrupt>`; `PlaylistOrchestrator(…, Func<ShowEventType, Task>? fireEvent = null)`, `PlaylistOrchestrator.MinInterruptEveryMs { get; set; } = 10_000`.

- [ ] **Step 1: Write the failing tests**

```csharp
// WaBiBaBuSy.Tests/PlaylistInterruptTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class PlaylistInterruptTests
{
    private readonly List<ShowEventType> _fired = new();

    private PlaylistOrchestrator Orchestrator() => new(
        NullLogger<PlaylistOrchestrator>.Instance,
        apply: _ => Task.FromResult(new ApplyMetrics(1000, 0)),
        seedProvider: () => 1,
        fireEvent: t => { lock (_fired) _fired.Add(t); return Task.CompletedTask; })
    { MinInterruptEveryMs = 0 };

    private static Playlist Show(params PlaylistInterrupt[] interrupts) => new()
    {
        DefaultItemDurationMs = 60_000,
        Items = { new PlaylistItem { Name = "a", Config = new CrossScreenConfig() } },
        Interrupts = interrupts.ToList(),
    };

    private int Count(ShowEventType t) { lock (_fired) return _fired.Count(x => x == t); }

    [Fact]
    public async Task Interrupt_FiresRepeatedly_WithoutAdvancingTheItem()
    {
        var o = Orchestrator();
        o.Start(Show(new PlaylistInterrupt { EveryMs = 40, Type = ShowEventType.Celebration }));
        await Task.Delay(300);
        Assert.True(Count(ShowEventType.Celebration) >= 3, $"fired {Count(ShowEventType.Celebration)}×");
        Assert.Equal(0, o.CurrentItemIndex);
        await o.StopAsync();
    }

    [Fact]
    public async Task Stop_EndsTheInterrupts()
    {
        var o = Orchestrator();
        o.Start(Show(new PlaylistInterrupt { EveryMs = 30, Type = ShowEventType.Blackout }));
        await Task.Delay(120);
        await o.StopAsync();
        int after = Count(ShowEventType.Blackout);
        await Task.Delay(150);
        Assert.Equal(after, Count(ShowEventType.Blackout));
    }

    [Fact]
    public async Task DisabledOrTooFrequent_NeverFires()
    {
        var o = Orchestrator();
        o.MinInterruptEveryMs = 10_000;
        o.Start(Show(new PlaylistInterrupt { EveryMs = 20, Type = ShowEventType.Traversal },
                     new PlaylistInterrupt { EveryMs = 20, Type = ShowEventType.Celebration, Enabled = false }));
        await Task.Delay(150);
        Assert.Empty(_fired);
        await o.StopAsync();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~PlaylistInterruptTests"` → build error.

- [ ] **Step 3: Model** — in `Playlist.cs` (add `using WaBiBaBuSy.Models.Show;`), in class `Playlist`:

```csharp
    /// <summary>Show events fired on a timer while the show runs (roadmap 2.4); they never advance the item.</summary>
    public List<PlaylistInterrupt> Interrupts { get; set; } = new();
```

and

```csharp
/// <summary>A show event the running show fires every <see cref="EveryMs"/> (e.g. the logo swims the ring every 30 min).</summary>
public class PlaylistInterrupt
{
    public bool Enabled { get; set; } = true;
    public int EveryMs { get; set; } = 1_800_000;
    public ShowEventType Type { get; set; } = ShowEventType.Traversal;
}
```

- [ ] **Step 4: Orchestrator** — constructor gets a last optional parameter `Func<ShowEventType, Task>? fireEvent = null` stored in `_fireEvent`; add:

```csharp
    private readonly Func<ShowEventType, Task>? _fireEvent;
    private Task? _interruptTask;

    /// <summary>Interrupts more frequent than this are ignored (a typo must not fire an event every few ms).</summary>
    public int MinInterruptEveryMs { get; set; } = 10_000;
```

In `StartCore`, after `_loopTask = RunLoopAsync(...)`:

```csharp
        _interruptTask = RunInterruptsAsync(playlist.Interrupts, _cts.Token);
```

In `StopAsync`, after awaiting `_loopTask`:

```csharp
        if (_interruptTask != null)
        {
            try { await _interruptTask; } catch (OperationCanceledException) { }
            _interruptTask = null;
        }
```

New methods (add `using WaBiBaBuSy.Models.Show;`):

```csharp
    /// <summary>One timer per enabled interrupt, independent of item switches; cancelled with the show.</summary>
    private Task RunInterruptsAsync(IReadOnlyList<PlaylistInterrupt>? interrupts, CancellationToken ct)
    {
        var active = (interrupts ?? Array.Empty<PlaylistInterrupt>())
            .Where(i => i.Enabled && i.EveryMs > 0 && i.EveryMs >= MinInterruptEveryMs).ToList();
        if (_fireEvent == null || active.Count == 0) return Task.CompletedTask;
        return Task.WhenAll(active.Select(i => RepeatInterruptAsync(i, ct)));
    }

    private async Task RepeatInterruptAsync(PlaylistInterrupt interrupt, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interrupt.EveryMs, ct);
                try { await _fireEvent!(interrupt.Type); }
                catch (Exception ex) { _logger.LogWarning(ex, "Playlist interrupt {Type} failed", interrupt.Type); }
            }
        }
        catch (OperationCanceledException) { /* show stopped */ }
    }
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter "FullyQualifiedName~PlaylistInterruptTests|FullyQualifiedName~PlaylistOrchestrator"` → PASS.

- [ ] **Step 6: Wire the host and the playlist tab**

`ServerPlaybackHost`, in `new PlaylistOrchestrator(…)` add the argument:

```csharp
                waitUntilNodesReady: WaitUntilRemotesPrefetchedAsync,
                fireEvent: type => Dispatcher.UIThread.InvokeAsync(() => FireEventAsync(type, EventSettings.LastTargetSeatId)));
```

`PlaylistViewModel` (add `using WaBiBaBuSy.Models.Show;`): one interrupt row (the model keeps a list for later):

```csharp
    [ObservableProperty] private bool _interruptEnabled;
    [ObservableProperty] private int _interruptEveryMinutes = 30;
    [ObservableProperty] private ShowEventType _interruptType = ShowEventType.Traversal;
    public ShowEventType[] InterruptTypes { get; } = { ShowEventType.Traversal, ShowEventType.Celebration, ShowEventType.Blackout };
```

In `BuildPlaylist()`'s initializer add:

```csharp
            Interrupts = InterruptEnabled
                ? new List<PlaylistInterrupt> { new() { EveryMs = Math.Clamp(InterruptEveryMinutes, 1, 24 * 60) * 60_000, Type = InterruptType } }
                : new List<PlaylistInterrupt>(),
```

In `LoadFrom(...)` add:

```csharp
        var interrupt = playlist.Interrupts.FirstOrDefault();
        InterruptEnabled = interrupt?.Enabled == true;
        InterruptEveryMinutes = Math.Max(1, (interrupt?.EveryMs ?? 1_800_000) / 60_000);
        InterruptType = interrupt?.Type ?? ShowEventType.Traversal;
```

`PlaylistPanel.axaml`, inside the header `WrapPanel` after the Default duration:

```xml
                <CheckBox Content="Every" IsChecked="{Binding InterruptEnabled}" Margin="12,0,6,0"
                          ToolTip.Tip="Fire a show event on a timer while the show runs (does not change the item)"/>
                <NumericUpDown Value="{Binding InterruptEveryMinutes}" Minimum="1" Maximum="1440" Width="90" IsEnabled="{Binding InterruptEnabled}"/>
                <TextBlock Text="min" VerticalAlignment="Center" Margin="4,0,6,0"/>
                <ComboBox ItemsSource="{Binding InterruptTypes}" SelectedItem="{Binding InterruptType}" IsEnabled="{Binding InterruptEnabled}"/>
```

- [ ] **Step 7: Build, tests, commit**

Run: `dotnet build WaBiBaBuSy.sln` → 0 errors; `dotnet test WaBiBaBuSy.Tests` → green.

```bash
git add WaBiBaBuSy.Models/Wallpaper/Playlist.cs WaBiBaBuSy.Core/Services/Animation/PlaylistOrchestrator.cs WaBiBaBuSy.UI/Services/ServerPlaybackHost.cs WaBiBaBuSy.UI/ViewModels/PlaylistViewModel.cs WaBiBaBuSy.UI/Views/PlaylistPanel.axaml WaBiBaBuSy.Tests/PlaylistInterruptTests.cs
git commit -m "feat(playlist): scheduled show-event interrupts (every N min, item unchanged)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 18: Docs, roadmap status, checklist

**Files:**
- Modify: `.docs/RECENT_UPDATES.md` (new top entry), `.docs/plans/2026-10-03-show-events-design.md` (status), `.docs/plans/2026-09-17-crossing-effects-and-events-design.md` (status line), `.docs/plans/2026-09-17-lan-party-roadmap.md` (Tier 2.4 status), `.docs/2026.07_OPEN_ITEMS.md` (validation items), `.docs/2026.07_FEATURE_OVERVIEW.md` (inventory row), `CLAUDE.md` (capabilities line + Current Work), `.docs/plans/2026-10-04-manual-test-checklist.md` (new section), `TestScenarios/README.md` (`showEvent` step + `events.json`)

- [ ] **Step 1: Changelog** — `.docs/RECENT_UPDATES.md`, top entry `## 2026-10-03 — One-shot show events (roadmap 2.4 slice 2)`: what shipped (Spotlight, Celebration, Traversal, Blackout, Announce; Events strip; Ctrl+Alt+1..5; preview; `showEvent` step + `showcase/events.json`; interrupts if Task 17 landed), the proto change (`EVENT = 12`, `event_json = 6`; old clients ignore it), the replay rule, and the build/test result with the test count from the last `dotnet test` run.

- [ ] **Step 2: Design + roadmap status** — show-events design: `**Status:** implemented 2026-10-03 (Tasks 1–16[, 17]); multi-machine validation pending`. Parent design header: one-shot events (§5) implemented — link the new design. Roadmap Tier 2.4: "events done; Portal + transitions open".

- [ ] **Step 3: CLAUDE.md** — under Key Capabilities, after the sprite-effects line, add:

```markdown
- One-shot show events: Spotlight a seat, Celebration burst, Traversal (an image laps the ring once), Blackout, Announce text lap — shared T0 + duration, drawn above the scene by every player and the room preview (`WaBiBaBuSy.Models/Show`), fired from the Events strip, Ctrl+Alt+1..5 (server only) or playlist interrupts; `SyncCommand` EVENT
```

and under Current Work item 3 change "2.4 crossing effects + events (rest)" to "2.4 Portal + transitions (events done)".

- [ ] **Step 4: Manual checklist** — append to `.docs/plans/2026-10-04-manual-test-checklist.md` a section `## 8. Show events (roadmap 2.4 slice 2, build-verified only)`:

```markdown
- [ ] **Showcase exact frames:** Developer tools → Run test suite… → `TestScenarios/showcase/events.json` → report shows confetti rising/falling, the spotlight border on seat 1 (others dimmed with a chevron), the cat mid-lap, blackout half / full, the announce text.
- [ ] **Celebration on 2+ machines:** confetti crosses the bezel continuously (a piece leaving one screen appears on the next at the same height).
- [ ] **Spotlight:** pick a seat in the strip (or select its tile) → its border pulses gold for 8 s; every other seat dims and its chevron points toward it (mirrored row: still toward it; Ring: the shorter way).
- [ ] **Traversal:** default (scene sprite) and a picked image (club logo) each lap the room exactly once and vanish; on a client that never had the image it still appears (download), at most late — never at the wrong place.
- [ ] **Blackout / Announce:** blackout fades in and out over 3 s on all seats; the announce text laps once, readable, same position on all seats.
- [ ] **Playlist item switch during an event:** fire a Celebration 1 s before an item switch → the burst continues across the switch without restarting.
- [ ] **Late join:** fire a 10 s Spotlight, then connect a client 3 s later and start a scene → no stale event after it ended; during it, Resync shows it at the right position.
- [ ] **Hotkeys:** with the server running, Ctrl+Alt+1..5 fire the events with the main window closed (tray only); on a client machine the keys do nothing (not registered); a key held by another app shows "Hotkey(s) taken by another app" in the strip.
- [ ] **Old client:** a client on the previous build stays on its scene when an event is fired (its log shows "Unknown command type: 12").
- [ ] **Room preview (Live):** the tiles show the same events at the same moments.
- [ ] **Performance:** Celebration on a 4K seat stays at the monitor's refresh rate; player CPU stays under 15 %.
- [ ] **Interrupts** (if Task 17 shipped): a playlist with "Every 1 min Traversal" laps the logo once a minute without changing the item.
```

- [ ] **Step 5: Open items + feature overview + scenario README** — `2026.07_OPEN_ITEMS.md`: add the multi-machine validation of events under validation items, point at the checklist section. `2026.07_FEATURE_OVERVIEW.md`: add a "Show events" row (types, triggers, status: implemented, validation pending). `TestScenarios/README.md`: document `showEvent` (`event`, `atElapsedMs`, image path relative to the scenario, runner replaces `eventId`/`t0Utc`) and list `showcase/events.json`.

- [ ] **Step 6: Graph + verification + commit**

Run: `./tools/graphify-update.ps1` (AST-only; do not commit `graphify-out/` changes other than `GRAPH_REPORT.md` if the hook does not already). Run `dotnet build WaBiBaBuSy.sln` → 0 errors and `dotnet test WaBiBaBuSy.Tests` → green.

```bash
git add .docs CLAUDE.md TestScenarios/README.md
git commit -m "docs: show events (roadmap 2.4 slice 2) — changelog, design/roadmap status, checklist

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
