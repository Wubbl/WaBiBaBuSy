"""Render the BiBaBu logo parts (Bier, Bart, Busen) in many styles as transparent sprites, through a running ComfyUI.

FLUX.2 Klein 9B gets the white-on-black line drawing (ComfyUI/input/bibabu_<part>.png) as reference image and a style
prompt; the object is drawn on a flat background, which is keyed out here (flood fill from the border, so enclosed
areas such as the beard's mouth stay). Each sprite is trimmed to its content and saved as <out>/<part>_<style>.png.
Existing sprites are skipped; delete one to re-key it from its raw_ render, delete both to re-render it, or change --seed for a whole new take.

    python logo_styles.py                                   # every part x every style
    python logo_styles.py --parts bier --styles gold,lego   # a few
"""
import argparse
import os
import sys
import time
from collections import deque

import numpy as np
from PIL import Image, ImageFilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import comfy_api  # noqa: E402
from comfy_api import Graph, klein, reference, run  # noqa: E402

PARTS = {
    "bier": "the foaming beer mug",
    "bart": "the beard (a full beard with a moustache around an open mouth)",
    "busen": "the bra outline (two round cups)",
}
# style -> (description, background). The background is the flat colour the object stands on, chosen so the
# object never contains it (a white sticker border on white would be keyed out with the background).
STYLES = {
    "gold":        ("a shiny polished gold 3D emblem with bevelled edges and soft reflections", "white"),
    "chrome":      ("a mirror-polished chrome 3D object with sharp reflections", "white"),
    "pixel":       ("a 16-bit pixel art video game sprite with a dark outline and limited colour palette, crisp square pixels", "white"),
    "voxel":       ("a 3D voxel object built from small cubes, like a block-building game", "white"),
    "lego":        ("a model built from colourful plastic toy bricks with visible studs", "white"),
    "stainedglass":("a stained glass window piece with thick black lead lines and glowing coloured glass", "white"),
    "patch":       ("an embroidered fabric patch with a thick stitched border and visible thread texture", "white"),
    "sticker":     ("a glossy cartoon sticker with bold black outlines, bright flat colours and a thick white die-cut border", "black"),
    "graffiti":    ("wildstyle graffiti spray paint with drips, bright colours and a black outline", "white"),
    "lowpoly":     ("a low-poly 3D model with flat-shaded triangular facets in bright colours", "white"),
    "plush":       ("a cute soft plush toy made of fuzzy felt fabric with visible seams", "white"),
    "wood":        ("a hand-carved wooden sculpture with visible wood grain", "white"),
    "ice":         ("a translucent blue ice sculpture with frosty edges", "black"),
    "origami":     ("folded colourful origami paper craft with crisp folds", "white"),
    "gingerbread": ("an Oktoberfest gingerbread heart cookie decorated with colourful sugar icing", "white"),
    "gummy":       ("a translucent glossy gummy candy in bright red and orange", "white"),
    "balloon":     ("a shiny inflated metallic foil party balloon", "white"),
    "tattoo":      ("an old-school tattoo flash design with bold outlines, red, yellow and teal shading", "white"),
    "popart":      ("a comic book pop art illustration with halftone dots, bold outlines and primary colours", "white"),
    "neon":        ("a glowing pink and cyan neon tube sign", "black"),
}
NEG = "text, letters, watermark, frame, border around the image, multiple objects, cropped, cut off, shadow on the background"

ap = argparse.ArgumentParser()
ap.add_argument("--parts", default=",".join(PARTS))
ap.add_argument("--styles", default=",".join(STYLES))
ap.add_argument("--out", default="F:/ComfyUI/ComfyUI/output/bibabu_styles")
ap.add_argument("--size", type=int, default=1024)
ap.add_argument("--max-side", type=int, default=512, help="longest side of the saved sprite")
ap.add_argument("--seed", type=int, default=2026)
ap.add_argument("--server", default="http://127.0.0.1:8188")
args = ap.parse_args()
comfy_api.SERVER = args.server
os.makedirs(args.out, exist_ok=True)


def render(part, style, seed):
    desc, bg = STYLES[style]
    text = (f"Redraw {PARTS[part]} from the reference line drawing as {desc}. Keep exactly the same silhouette, outline "
            f"and proportions as the reference. One single object, centred, filling about 70 percent of the image, "
            f"isolated on a plain flat solid pure {bg} background, nothing else in the image.")
    g = Graph()
    unet, clip, vae = klein(g)
    pos, neg = g("CLIPTextEncode", clip=clip, text=text), g("CLIPTextEncode", clip=clip, text=NEG)
    pos, neg = reference(g, vae, pos, neg, f"bibabu_{part}.png")
    s = args.size
    sm = comfy_api.sample(g, unet, pos, neg, g("EmptyFlux2LatentImage", width=s, height=s, batch_size=1), seed, s, s)
    g("SaveImage", images=g("VAEDecode", samples=sm, vae=vae), filename_prefix=f"bibabu_styles/raw_{part}_{style}")
    return run(g)


def key_out(img, glow=False, tol=40.0):
    """Alpha = 0 for background pixels connected to the image border (colour within `tol` of the border's median
    colour), soft 1 px edge. glow (neon on black): alpha also follows the distance from black, so the glow fades out."""
    a = np.asarray(img, np.float32)
    h, w, _ = a.shape
    border = np.concatenate([a[0], a[-1], a[:, 0], a[:, -1]])
    bg = np.median(border, axis=0)
    dist = np.sqrt(((a - bg) ** 2).sum(axis=2))
    near = dist < tol
    seen = np.zeros((h, w), bool)
    q = deque((y, x) for y in range(h) for x in (0, w - 1) if near[y, x])
    q.extend((y, x) for x in range(w) for y in (0, h - 1) if near[y, x])
    for y, x in q:
        seen[y, x] = True
    while q:
        y, x = q.popleft()
        for ny, nx in ((y - 1, x), (y + 1, x), (y, x - 1), (y, x + 1)):
            if 0 <= ny < h and 0 <= nx < w and near[ny, nx] and not seen[ny, nx]:
                seen[ny, nx] = True
                q.append((ny, nx))
    alpha = Image.fromarray(np.where(seen, 0, 255).astype(np.uint8)).filter(ImageFilter.MinFilter(3)).filter(ImageFilter.GaussianBlur(0.8))
    al = np.asarray(alpha, np.float32) / 255.0
    rgb = a
    if glow:
        lum = np.clip(dist / 255.0 * 2.0, 0, 1)
        al = np.maximum(al * lum, np.where(seen, lum, 0))
        rgb = np.clip((a - bg * (1 - al[..., None])) / np.maximum(al[..., None], 1e-3), 0, 255)
    out = Image.fromarray(np.dstack([rgb, al * 255]).clip(0, 255).astype(np.uint8), "RGBA")
    box = out.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
    if box:
        out = out.crop(box)
    out.thumbnail((args.max_side, args.max_side), Image.Resampling.LANCZOS)
    return out


for part in args.parts.split(","):
    for i, style in enumerate(args.styles.split(",")):
        path = os.path.join(args.out, f"{part}_{style}.png")
        if os.path.exists(path):
            continue
        t0 = time.time()
        raw_path = os.path.join(args.out, f"raw_{part}_{style}.png")   # kept, so re-keying needs no new render
        raw = Image.open(raw_path).convert("RGB") if os.path.exists(raw_path) else render(part, style, args.seed + i)
        raw.save(raw_path)
        key_out(raw, glow=style == "neon").save(path)
        print(f"{part}_{style} {time.time() - t0:.0f}s", flush=True)
