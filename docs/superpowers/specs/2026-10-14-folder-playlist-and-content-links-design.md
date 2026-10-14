# Folder → Party Playlist, and "Get content" Links — Design Spec
**Date:** 2026-10-14  
**Status:** Implemented 2026-10-14 (unit tests + real-decode console check; GUI click-through pending)  
**Builds on:** `2026-10-14-auto-suggest-design.md` (media profiles, recipes, `SceneSuggester`)

## Goal

LAN-party prep in bulk: point at a folder (e.g. 40 cat GIFs, 10 logos, 2 clips) and get a ready, varied party
playlist — one click instead of configuring 50 scenes. And a place in the app that says *where to get* small,
animation-friendly content (free GIF / sprite sites, AI generators, clean-up tools).

## Constraints

- Everything the auto-suggest spec fixes still holds: pure, deterministic Models code; recipes untouched; a built
  item is an ordinary `CrossScreenConfig`; nothing reaches the machines until the show is started.
- Same folder + same files → same playlist (seeds from file names, never the clock).
- The playlist format (`Playlist` / `PlaylistItem`) and playback are unchanged.
- No new package dependencies. No network calls from the app: links open in the user's browser.

## 1. Files → items (approved)

### 1.1 Buckets

| Bucket | Which files | Becomes |
|---|---|---|
| **Showcase** | `SizeClass.FullScreen` files and every video | one item each, recipe Showcase |
| **Swarmable** | Small / Medium **with** `HasAlpha` | groups of 3–5 (§1.2) |
| **Solo** | everything else (opaque Medium images, `AspectClass.Wide` wordmarks) and swarmable leftovers when fewer than 3 exist | one item each, its best single-file recipe |

Wide wordmarks go to Solo even with alpha (Marquee is the recipe that suits them).

### 1.2 Groups

Swarmable files are ordered by (mono before colourful, then `PixelHeight`, then file name) so neighbours look
alike, then cut into groups of 4. The remainder decides the tail: 0 → nothing; 1 → the last group becomes 5;
2 → the last group of 4 and the 2 become 3 + 3; 3 → a group of 3. (So 5 → 5, 6 → 3+3, 9 → 4+5, 10 → 4+3+3.)
Fewer than 3 swarmable files → all Solo.

A group's scene is the best-scoring recipe for that group (`SceneSuggester`'s ranking), which for 3–5 alpha
sprites is Pulse Grid / Fireflies / Parade / Logo Conveyor.

### 1.3 Order and variety

- Items interleave by bucket in a fixed rhythm: group, solo, group, showcase, … (any bucket that runs out is
  skipped), so the show does not play ten grids in a row.
- No recipe twice in a row: if an item's best recipe equals the previous item's, it takes the next best one that
  scores > 0 (if none, it repeats).
- Each item's seed = `PatternLayout.Hash3` over a stable hash of its file names (ordinal, case-insensitive),
  so rebuilding the same folder gives the same playlist.

### 1.4 Durations

- Default dwell: the playlist's `DefaultItemDurationMs` (30 s).
- An item with GIFs: rounded to the nearest whole multiple of its shortest loop (at least one loop).
- A Showcase video: its own length, capped at 120 s; unknown length → the default.
- `DurationMs` is stored only when it differs from the default (null otherwise, as the editor does).

### 1.5 Names

Item: `"<Recipe>: <first file name>"` plus `" + N"` for groups (e.g. `Pulse Grid: cat01.gif + 3`,
`Showcase: intro.mp4`). Playlist: the folder name.

Not done: near-duplicate detection, subfolders (only the folder's own files are read).

## 2. UI flow

- **Entry point:** the Playlist tab's **＋ Add ▾** menu gains **"Auto from folder…"** (next to "Current scene" and
  "From preset"). It opens the folder picker.
- **Reading:** the media files in that folder (the gallery's extensions: images, GIFs, videos), sorted by name,
  at most **200** (more → the first 200 by name and a status note). Each is added to the gallery
  (`AddWallpaperFromPath`, which already skips duplicates) so items get thumbnails and can be opened in the
  Scene editor / re-suggested later. Each file is profiled with the existing `MediaProbe` off the UI thread; the
  Playlist tab shows a status line *"Analysing 12 / 52…"*. A file that fails to decode is profiled as Unknown
  (it lands in Solo or Showcase), never aborts the run.
- **Result:** the built items are **appended** to the open playlist (`PlaylistViewModel.AddItem`), so nothing is
  ever replaced or lost. If the open playlist was empty, it is also renamed to the folder name. The status line
  says *"Added 14 items (≈ 7 min) from Cats"*. The playlist is not saved automatically — the normal Save does that.
- Physical-unit rooms get the cm twins (same rule as Suggest). Monitor targets / distribution mode come from the
  Scene editor's current draft, as with Suggest.
- **Empty folder / no media:** status *"No pictures or videos in <folder>"*, nothing added.

## 3. "Get content" links

A **"Get content ▾"** button next to the gallery's **+ Add**, opening a menu of links in the default browser
(`Process.Start` with `UseShellExecute`, the pattern the app already uses):

| Group | Links |
|---|---|
| **Free GIFs & sprites** | GIPHY, Tenor, itch.io free game assets, OpenGameArt, LottieFiles, Pixabay, Pexels |
| **Make with AI** | ChatGPT (images), Ideogram, Adobe Firefly, Kling, Luma Dream Machine, Runway, ComfyUI (local, free) |
| **Clean up** | remove.bg (transparent background), ezgif (crop / resize / convert to GIF), Upscayl (local upscaling) |

Each item has a one-line tooltip (what it is good for; "check the licence for public shows" on the free-content
sites). The last menu entry is a non-clickable tip: *"Best results: small transparent PNG / GIF, plain
background → remove.bg, seamless loop."* The link list is plain data in Models (`ContentLinks`), so a test can
check every URL is `https://` and every group is non-empty; root-domain URLs only (no deep links that rot).

Not done: calling any AI service from the app (API keys, cost, errors), downloading into the gallery.

## 4. Testing

- `PlaylistBuilderTests` (pure): bucket assignment; grouping sizes (3, 4, 5, 6, 7, 9 swarmable files → 3/4/5/3+3/4+3/4+5);
  < 3 swarmable → Solo; interleave order; no recipe twice in a row when an alternative exists; GIF dwell is a
  whole number of loops; video dwell capped at 120 s; same input → same playlist (JSON equal); every item passes
  `SceneChecks.Validate(scene, null)`; names.
- `ContentLinksTests`: https only, unique, non-empty groups.
- Manual: Auto from folder on `TestScenarios/showcase/assets` and a folder of ~40 GIFs; the status line; items
  play; Get content opens the browser.

## 5. Files

| New | Project |
|---|---|
| `Wallpaper/Suggest/PlaylistBuilder.cs`, `Content/ContentLinks.cs` | Models |
| tests above | Tests |

Changed: `SceneSuggester` (expose the ranked recipe list for one selection — `Ranked(selection)` — so the builder can
take "next best"), `PlaylistViewModel` + `PlaylistPanel.axaml` (menu entry, status line, a `BuildFromFolder` hook set
by the host), `MainWindowViewModel.Playlist.cs` (folder pick, gallery add, profiling — reusing the profile code from
`BuildSelectionProfileAsync`, extracted to one helper), `SceneEditorPanel.axaml` (Get content menu).
