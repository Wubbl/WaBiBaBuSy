"""Turn the logo_styles.py sprites into the "BiBaBu Swarm" scene preset: every style of Bier, Bart and Busen at once.

Copies the sprites to %APPDATA%/WaBiBaBuSy/presets/assets/bibabu-styles/ and writes presets/BiBaBu Swarm.json:
  1. Logo drift   - all sprites in a pattern grid drifting slowly across the room (Sequential), dimmed
  2. Bier parade  - the Bier styles as one group on a sine wave (Sequential)
  3. Bart bounce  - the Bart styles bouncing around the room (Sequential)
  4. Busen orbit  - the Busen styles circling on every screen (Simultaneous)
  5. Neon comet   - the neon Bier alone with a trail, opening a Portal at every bezel (effects need a single image)

Also one preset per finished panorama.py wallpaper ("Panorama - Lan Party", ...): the room image as Panorama
background and a parade of matching logo styles riding across the middle band.

    python swarm_preset.py [--sprites F:/ComfyUI/ComfyUI/output/bibabu_styles] [--panoramas F:/ComfyUI/ComfyUI/output/panorama2]
"""
import argparse
import glob
import json
import os
import shutil

ap = argparse.ArgumentParser()
ap.add_argument("--sprites", default="F:/ComfyUI/ComfyUI/output/bibabu_styles")
ap.add_argument("--presets", default=os.path.join(os.environ["APPDATA"], "WaBiBaBuSy", "presets"))
ap.add_argument("--name", default="BiBaBu Swarm")
ap.add_argument("--panoramas", default="F:/ComfyUI/ComfyUI/output/panorama2")
args = ap.parse_args()

dst = os.path.join(args.presets, "assets", "bibabu-styles")
os.makedirs(dst, exist_ok=True)
parts = {}
for f in sorted(glob.glob(os.path.join(args.sprites, "*.png"))):
    name = os.path.basename(f)
    if name.startswith("raw_"):
        continue
    shutil.copy2(f, os.path.join(dst, name))
    parts.setdefault(name.split("_")[0], []).append("assets/bibabu-styles/" + name)
everything = [p for ps in parts.values() for p in ps]
if not everything:
    raise SystemExit(f"no sprites in {args.sprites}")


def images(paths, spread=0, jitter=0, **anim):
    return dict(AnimationPath=paths[0], AdditionalAnimationPaths=paths[1:], FitMode="TargetHeight",
                MultiImageSpread=spread, MultiImagePhaseJitterMs=jitter, **anim)


layers = [dict(
    Name="Logo drift", Opacity=0.55,
    Animation=images(everything, TargetHeight=110, Pattern=dict(Sizing="Fill", SpacingX=110, SpacingY=80, Margin=160,
                                                                  RandomOffsetMaxPx=55, RandomRotationMaxDeg=25, Seed=7)),
    Movement=dict(Type="Linear", SpeedPixelsPerSecond=45, DirectionAngleDegrees=12),
    Distribution="Sequential")]
if parts.get("bier"):
    layers.append(dict(
        Name="Bier parade",
        Animation=images(parts["bier"], spread=2400, jitter=3000, TargetHeight=240),
        Movement=dict(Type="SineWave", SpeedPixelsPerSecond=260, WaveAmplitudePixels=220, WaveFrequencyHz=0.12),
        Distribution="Sequential"))
if parts.get("bart"):
    layers.append(dict(
        Name="Bart bounce", PhaseOffsetMs=5000,
        Animation=images(parts["bart"], spread=1800, jitter=2500, TargetHeight=280),
        Movement=dict(Type="Bounce", SpeedPixelsPerSecond=170, DirectionAngleDegrees=52),
        Distribution="Sequential"))
if parts.get("busen"):
    layers.append(dict(
        Name="Busen orbit", Opacity=0.9,
        Animation=images(parts["busen"], spread=500, jitter=1200, TargetHeight=170),
        Movement=dict(Type="Circular", SpeedPixelsPerSecond=140, OrbitRadiusPixels=330),
        Distribution="Simultaneous"))

neon = "assets/bibabu-styles/bier_neon.png"
if neon in everything:
    layers.append(dict(
        Name="Neon comet",
        Animation=images([neon], TargetHeight=200, Effects=dict(
            Trail=dict(Count=6, SpacingMs=70, StartOpacity=0.5, ScaleFalloff=0.9, UseGradingColor=False),
            Entry="Portal", Exit="Portal", BezelColorHex="#FF3CC8")),
        Movement=dict(Type="Linear", SpeedPixelsPerSecond=600, DirectionAngleDegrees=0),
        Distribution="Sequential"))

scene = {"Background": {"Mode": "SolidColor", "ColorHex": "#0B0B14"}, "Layers": layers}
path = os.path.join(args.presets, args.name + ".json")
json.dump(scene, open(path, "w", encoding="utf-8"), indent=2)
print(path, {k: len(v) for k, v in parts.items()})

# Panorama presets: the styles that suit each wallpaper ride across it (missing styles are skipped).
RIDERS = {
    "lan_party": ["pixel", "voxel", "lego"], "pixel_quest": ["pixel", "voxel", "lego"], "cat_world": ["pixel", "plush", "sticker"],
    "neon_harbour": ["neon", "chrome", "stainedglass"], "mountains": ["wood", "gingerbread", "patch"],
    "fantasy_realm": ["gold", "stainedglass", "wood"], "deep_space": ["neon", "chrome", "ice"],
}
pano_dst = os.path.join(args.presets, "assets", "panoramas")
for room in sorted(glob.glob(os.path.join(args.panoramas, "*", "*_room.*"))):
    concept = os.path.basename(os.path.dirname(room))
    if room.endswith("_preview.jpg") or concept not in RIDERS:
        continue
    os.makedirs(pano_dst, exist_ok=True)
    shutil.copy2(room, os.path.join(pano_dst, os.path.basename(room)))
    riders = [f"assets/bibabu-styles/{part}_{st}.png" for st in RIDERS[concept] for part in ("bier", "bart", "busen")]
    riders = [r for r in riders if r in everything]
    pano = {"Background": {"Mode": "Panorama", "ImagePath": "assets/panoramas/" + os.path.basename(room)}, "Layers": []}
    if riders:
        pano["Layers"].append(dict(Name="Logo parade", Animation=images(riders, spread=3000, jitter=2000, TargetHeight=150),
                                   Movement=dict(Type="SineWave", SpeedPixelsPerSecond=220, WaveAmplitudePixels=60,
                                                 WaveFrequencyHz=0.25),
                                   Distribution="Sequential"))
    title = "Panorama - " + concept.replace("_", " ").title()
    json.dump(pano, open(os.path.join(args.presets, title + ".json"), "w", encoding="utf-8"), indent=2)
    print(title, len(riders), "riders")
