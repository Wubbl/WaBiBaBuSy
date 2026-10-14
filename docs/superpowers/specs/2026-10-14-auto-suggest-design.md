# Auto-Suggest Scenes from the Picked Media — Design Spec
**Date:** 2026-10-14  
**Status:** Draft — awaiting review  

## Goal

Configuring a scene by hand is too slow to try out content. After picking media, the user presses
**✨ Suggest** and gets four candidate scenes built for *that* media (a small mono logo, several logos,
a full-screen GIF, …). Clicking a card makes it the draft, the room view previews it live, and the
normal tabs fine-tune it. **Shuffle** offers four more.

Out of scope for this spec (later steps on the same engine): folder → party playlist batch, links to
AI content tools, animated mini-previews inside the cards, learning from what the user picked.

## Constraints

- **Determinism holds.** Recipes are pure functions of `(media profiles, seed, base scene)`. The applied
  scene is an ordinary `CrossScreenConfig`; every machine renders it identically. No `Random` without the
  seed, no clock, no per-machine state.
- **A suggestion replaces the look, never the targets.** `SelectedMonitorIds` and `DistributionMode`
  are copied from the current draft. Everything else (`Background`, `Animation`, `Movement`) is the recipe's.
- **Nothing is sent to machines** until the user presses Play, same as any draft edit.
- Analysis is pure and lives in `WaBiBaBuSy.Models` (no imaging dependency); decoding stays in UI.

## 1. Media analysis

### 1.1 Per file — `MediaProfile` (Models, record)

| Field | Source | Rule |
|---|---|---|
| `Path`, `Kind` (Image / Gif / Video) | extension (`SceneChecks.IsVideo`, `.gif`) | |
| `PixelWidth`, `PixelHeight` | existing media-details pass (`Image.FromFile`, `GifMetadataReader`, `ProbeVideo`) | |
| `SizeClass` (Small / Medium / FullScreen) | vs the server's primary monitor height H and width W | Small: height < 0.25·H. FullScreen: width ≥ 0.7·W **and** height ≥ 0.7·H. Else Medium |
| `AspectClass` (Wide / Square / Tall) | w / h | Wide > 2.5, Tall < 0.6, else Square |
| `HasAlpha` | thumbnail pixels | > 5 % of pixels with alpha < 128 |
| `IsMonochrome` | visible pixels (alpha ≥ 128) | ≥ 90 % of them either low-saturation (S < 0.15) or within ±20° of the dominant hue |
| `DominantColors` (≤ 3, `#RRGGBB`) | visible pixels | 4-bit-per-channel buckets, most frequent first; buckets under 5 % dropped |
| `MeanLuminance` (0–1) | visible pixels | Rec. 709 luma mean |
| `LoopMs` (GIF), `DurationMs` (video) | existing `GifMetadataReader` / `ProbeVideo` | null for stills |

The pixel facts come from `MediaAnalyzer.Analyze(ReadOnlySpan<byte> bgra, int w, int h)` (pure). The UI
decodes the file's first frame with Magick.NET, scaled to fit 64 × 64, and passes the BGRA bytes. For
videos the existing thumbnail is decoded instead. Results are cached on `WallpaperGalleryItem` inside
`MainWindowViewModel.ScheduleMediaDetails`, so Suggest never blocks on decoding. A file that fails to
decode gets a profile with pixel facts unknown (`HasAlpha = false`, `IsMonochrome = false`,
`DominantColors` empty); recipes must cope.

### 1.2 Whole selection — `SelectionProfile`

Built from the picked files (`AnimationPath` + `AdditionalAnimationPaths`):

- `Files` (the `MediaProfile`s, hero first), `Count`
- `Mix`: **Single**, **Uniform** (all within a factor 2 in height), **BigPlusSmall** (exactly one FullScreen,
  the rest Small/Medium), **Mixed** (anything else)
- `AllMonochrome`, `SharedPalette` (the union of dominant colours, ≤ 5, most frequent first)
- `ShortestLoopMs` over the GIFs (null if none)

Deliberately not measured: seamless-loop detection, content recognition.

## 2. Recipes

```csharp
public interface ISceneRecipe   // Models/Wallpaper/Suggest/
{
    string Name { get; }
    double Score(SelectionProfile s);                          // 0 = not offered, else 0..1
    Suggestion Build(SelectionProfile s, CrossScreenConfig baseScene, int seed);
}
public sealed record Suggestion(string RecipeName, string Why, CrossScreenConfig Scene);
```

`Why` is one line naming the facts used ("mono logo → rainbow tint", "loop 1.2 s → pulse every loop").
`Build` starts from `SceneFile.Clone(baseScene)` and overwrites the look. Seeded choices use the existing
deterministic hash (`PatternLayout.Hash3`) over the seed, never `System.Random`.

### 2.1 Shared rules (all recipes)

- **Tint:** mono → a colour mode (Rainbow / Gradient between two palette colours / CycleColorList);
  colourful → `ColorGrading.Mode = None`.
- **Background:** `SolidColor`. Light media (`MeanLuminance` > 0.6) → dark background; dark media → light
  one. The hue is the first dominant colour, darkened to luma ≈ 0.08 (or lightened to ≈ 0.85).
- **Timing:** with a GIF in the selection, cycle-based values (pulse period, spin turn, colour
  `CyclesPerSecond`) are snapped to whole multiples of `ShortestLoopMs`.
- **Size:** `FitMode = TargetHeight`, height chosen per recipe as a fraction of screen height; FullScreen
  media in a sprite recipe is scaled down to that height.
- **Speed:** a per-recipe range; the seed picks within it.

### 2.2 The recipe set

| Recipe | Score high when | Builds |
|---|---|---|
| **DVD Bounce** | Single, Small, HasAlpha | `Bounce`; mono → `CycleColorList` over the palette (colour change on wall hits) |
| **Logo Conveyor** | any sprite; bonus in Sequential mode | `Linear`, `Endless`; mono → Rainbow |
| **Marquee** | AspectClass Wide | `SineWave`, low amplitude, ~15 % screen height |
| **Orbit** | Square, Small/Medium | `Circular` + spin; one turn = one loop when a GIF |
| **Pulse Grid** | Small sprites; Count ≥ 2 | `Pattern` (Fill), files spread over cells; mono → `TravelingRainbow`; pulse timed to the loop |
| **Fireflies** | Small, Count ≥ 2 or tiny | `RandomWalk`, jittered pattern, dark palette background |
| **Comet** | Single, Small, HasAlpha | fast `Linear` with `Trail` in the first dominant colour |
| **Parade** | Uniform, Count ≥ 2 | `Linear`, files in single file, `NodePhaseDelayMs` wave |
| **Showcase** | a FullScreen file | `Static`, `FitMode = Fill`, no tint, the FullScreen file as the sprite |

Opaque media (`HasAlpha = false`) scores at most 0.3 for swarm recipes (Pulse Grid, Fireflies).

### 2.3 BigPlusSmall (full-screen GIF + small GIFs)

A scene has one animation layer and the background is a still bitmap (the player loads one image), so
"animated backdrop + sprites" is not buildable until scene layers (roadmap Tier 2.3). Until then:
Showcase offers the big file alone; the other recipes use only the small files as sprites, with the
background colour taken from the big file's dominant colour. The suggestion strip shows
*"big + small together needs scene layers"*. A **Backdrop + Sprites** recipe is added once layers exist.

Not included: ThreeZone / IconZone backgrounds (they depend on the desktop, not the media).

### 2.4 Picking — `SceneSuggester` (pure)

- `Suggest(selection, baseScene, seed)` → the 4 highest-scoring recipes (ties by name), each built with a
  seed derived from `(seed, recipe index)`.
- `Shuffle(selection, baseScene, seed, shownRecipeNames)` → 4 recipes drawn weighted by score from
  the recipes with score > 0, preferring ones not in `shownRecipeNames` (if fewer than 4 remain, shown ones
  fill up). The new seed varies a repeated recipe's parameters.
- Fewer than 4 recipes with score > 0 → fewer cards. Empty selection → Suggest disabled.

## 3. UI

- **Content tab:** a **✨ Suggest** button next to the media picked from the gallery strip, enabled when
  `AnimationPath` is set.
- **Suggestion strip** above the tabs while open: title *"Suggestions for: …"*, 4 cards (recipe name +
  `Why`), **🎲 Shuffle**, **Keep**, **✕**. An info line for the BigPlusSmall note.
- **Click a card** → `SceneEditor.LoadFromConfig(suggestion.Scene)` (the preset-load path), so the room
  view previews it live and every tab shows its values. The clicked card is highlighted.
- **Keep** → close the strip, the clicked card stays the draft. **✕ / Revert** → close the strip and reload
  the draft saved when it opened. Before any card is clicked, Keep is disabled.
- **Shuffle** → new seed, new cards; the current draft is untouched until a card is clicked.
- Picking different media while the strip is open closes it (the saved draft is discarded).
- New `SuggestionsViewModel` (cards, saved draft, seed, shown names), owned by the Scene editor host.
  The first seed comes from the clock in the UI only; it is part of the built scene's own seeds, so
  the applied scene stays deterministic.

## 4. Testing

- `MediaAnalyzerTests`: hand-built BGRA arrays — opaque vs transparent, mono black logo, mono red logo,
  multi-colour, greyscale photo; dominant colour order; empty / fully transparent input.
- `SelectionProfileTests`: Mix classification (Single, Uniform, BigPlusSmall, Mixed), shared palette.
- `SceneRecipeTests` (theory over every recipe): `Build` is deterministic per seed; it keeps
  `SelectedMonitorIds` / `DistributionMode`; `SceneChecks.Validate` reports no errors; with an unknown-pixel
  profile it still builds.
- `SceneSuggesterTests`: the single small mono logo puts DVD Bounce in the top 4; three logos put Pulse
  Grid in the top 4; a FullScreen GIF puts Showcase first; Shuffle avoids shown recipes when it can; same
  seed → same cards.
- Manual: Suggest, click through the cards (room preview follows), Revert, Shuffle, Keep → Play.

## 5. Files

| New | Project |
|---|---|
| `Wallpaper/Suggest/MediaProfile.cs`, `MediaAnalyzer.cs`, `SelectionProfile.cs`, `ISceneRecipe.cs`, `SceneRecipes.cs` (all nine), `SceneSuggester.cs` | Models |
| `ViewModels/SuggestionsViewModel.cs`, strip markup in `SceneEditorPanel.axaml` | UI |
| the tests above | Tests |

Changed: `MainWindowViewModel.ScheduleMediaDetails` (cache the profile), `WallpaperGalleryItem` (profile
field), `CrossScreenConfigViewModel` / `SceneEditorPanel.axaml` (button).
