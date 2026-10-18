"""Render a room-sized Panorama wallpaper through a running ComfyUI (FLUX.2 Klein 9B) at native screen resolution.

Successor of gen_panorama_workflow.py's one-click workflow (see .docs/2026.10_PANORAMA_CONTENT_GUIDE.md section 9):
- one independent panel per screen at 1920 x 1088 (no 16,384 px limit, no upscale: every screen pixel is generated),
- every join (including the ring join last -> first) repainted as a band with both neighbours as context,
- the band blended in here with a per-row colour correction (the band's tone is pulled to the panels' tone at both
  band edges, interpolated across), which removes the hard vertical lines the workflow left at the band edges.

Files go to <out>/<concept>/: panel_XX.png and seam_XX.png are cached, so deleting one file and re-running renders
only that piece (plus nothing else). The room image is <out>/<concept>/<concept>_room.(jpg|png).

    python panorama.py --concept lan_party
    python panorama.py --concept deep_space --screens 10 --out F:/ComfyUI/ComfyUI/output/panorama2
"""
import argparse
import os
import sys
import time

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import comfy_api  # noqa: E402
from comfy_api import Graph, klein, reference, run, upload  # noqa: E402
from panorama_concepts import CONCEPTS  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--concept", required=True, choices=sorted(CONCEPTS))
ap.add_argument("--screens", type=int, default=10)
ap.add_argument("--out", default="F:/ComfyUI/ComfyUI/output/panorama2")
ap.add_argument("--server", default="http://127.0.0.1:8188")
ap.add_argument("--steps", type=int, default=20)
ap.add_argument("--seed", type=int, default=20261018)
ap.add_argument("--band", type=int, default=768, help="repainted band centred on each join (px)")
ap.add_argument("--ramp", type=int, default=96, help="soft edge inside the band (px)")
ap.add_argument("--pixel-size", type=int, help="snap to a pixel grid (2, 3, 4, 5, 6, 8; 0 = off; default: the concept's)")
ap.add_argument("--colors", type=int, default=64)
ap.add_argument("--only", help="comma-separated pieces to render, e.g. p3,s3 (others must exist or are skipped)")
ap.add_argument("--reseed", default="", help="comma-separated panel numbers that get a new seed (delete their "
                "panel_XX.png and the two neighbouring seam files first)")
ap.add_argument("--match", choices=["edge", "inner"], default="edge", help="seam colour match, see blend()")
ap.add_argument("--coarse", type=int, default=0, help="coarse-to-fine: one ~1.8 MP panel per N screens, lanczos up to "
                "room size, then a tiled img2img detail pass (0 = off, one native panel per screen)")
ap.add_argument("--detail", type=float, default=0.4, help="--coarse: denoise of the detail pass (0.3 soft .. 0.55 inventive)")
args = ap.parse_args()

SCREEN_W, H_FULL, ROOM_H = 1920, 1088, 1080   # Klein latents are 16 px cells: 1088 = 68 x 16, cropped to 1080 at the end
C = CONCEPTS[args.concept]
N = max(args.coarse, 1)                        # screens per panel
if args.screens % N:
    ap.error("--screens must be a multiple of --coarse")
P = args.screens // N
SCALE = 1.0 if not args.coarse else (1.84e6 / (N * SCREEN_W * ROOM_H)) ** 0.5
r16 = lambda v: int(round(v / 16)) * 16
W, H = (SCREEN_W, H_FULL) if not args.coarse else (r16(N * SCREEN_W * SCALE), r16(ROOM_H * SCALE))
HALF = W // 2
BAND, RAMP = r16(args.band * SCALE), r16(args.ramp * SCALE)
DIR = os.path.join(args.out, args.concept + (f"_coarse{N}" if args.coarse else ""))
os.makedirs(DIR, exist_ok=True)
EDGES = "Evenly lit to the very edges, no vignette, no frames, no borders, no text unless described."
LAYOUT = "The described subject is in the middle of the image; nothing important near the left and right edges."
SEAM_FILL = ("Fill the empty grey band in the middle so the left and right parts join seamlessly with the same colors, "
             "brightness, perspective and level of detail: ")
ONLY = set(args.only.split(",")) if args.only else None


comfy_api.SERVER = args.server


# ---------------------------------------------------------------- pieces
def beat(screen):
    return C["beats"][screen % len(C["beats"])]


def panel(i):
    g = Graph()
    unet, clip, vae = klein(g)
    screens = range(i * N, i * N + N)
    if N == 1:
        text = beat(i)
    else:   # a coarse panel spans N screens: say what goes where, left to right
        parts = ["left", "middle", "right"] if N == 3 else ["left half", "right half"] if N == 2 else [f"part {k + 1}" for k in range(N)]
        text = "From left to right: " + " ".join(f"In the {parts[k]}: {beat(s)}" for k, s in enumerate(screens))
    pos = g("CLIPTextEncode", clip=clip, text=" ".join([C["style"], EDGES, LAYOUT, text]))
    neg = g("CLIPTextEncode", clip=clip, text=C.get("neg", ""))
    for s_ in screens:
        refs = C.get("logos", {}).get(s_ % len(C["beats"]), [])
        for name in [refs] if isinstance(refs, str) else refs:     # one logo image or a list (first = "the reference")
            pos, neg = reference(g, vae, pos, neg, name)
    sm = comfy_api.sample(g, unet, pos, neg, g("EmptyFlux2LatentImage", width=W, height=H, batch_size=1), args.seed + i + (1000 if str(i + 1) in args.reseed.split(",") else 0), W, H, args.steps)
    g("SaveImage", images=g("VAEDecode", samples=sm, vae=vae), filename_prefix=f"panorama2/{args.concept}_panel")
    return run(g)


def band_mask():
    """1 in the band core, linear ramp to 0 at the band edges, 0 outside; window-wide (W)."""
    x = np.arange(W) + 0.5
    d = np.abs(x - HALF)
    return np.clip((BAND / 2 - d) / RAMP, 0.0, 1.0)


def seam(k, left, right):
    """Window = right half of `left` + left half of `right`; the band in the middle is repainted."""
    win = Image.new("RGB", (W, H))
    win.paste(left.crop((HALF, 0, W, H)), (0, 0))
    win.paste(right.crop((0, 0, HALF, H)), (HALF, 0))
    ref = win.copy()
    ref.paste((127, 127, 127), (HALF - BAND // 2, 0, HALF + BAND // 2, H))
    m = (band_mask()[None, :].repeat(H, 0) * 255).astype(np.uint8)
    names = (upload(win, f"{args.concept}_win{k}.png", "panorama2"), upload(ref, f"{args.concept}_ref{k}.png", "panorama2"),
             upload(Image.fromarray(m).convert("RGB"), f"{args.concept}_mask.png", "panorama2"))
    g = Graph()
    unet, clip, vae = klein(g)
    lat = g("VAEEncode", pixels=g("LoadImage", image=names[0]), vae=vae)
    lat = g("SetLatentNoiseMask", samples=lat, mask=g("LoadImageMask", image=names[2], channel="red"))
    pos = g("CLIPTextEncode", clip=clip, text=SEAM_FILL + " ".join([C["style"], EDGES, C["seam"]]))
    neg = g("CLIPTextEncode", clip=clip, text=C["seam_neg"])
    ref_lat = g("VAEEncode", pixels=g("LoadImage", image=names[1]), vae=vae)
    pos, neg = g("ReferenceLatent", conditioning=pos, latent=ref_lat), g("ReferenceLatent", conditioning=neg, latent=ref_lat)
    sm = comfy_api.sample(g, unet, pos, neg, lat, args.seed + 100 + k, W, H, args.steps)
    g("SaveImage", images=g("VAEDecode", samples=sm, vae=vae), filename_prefix=f"panorama2/{args.concept}_seam")
    return run(g), win


def smooth_rows(v, sigma=6.0):
    """Gaussian along the rows (axis 0) of an (H, 3) array, edge-padded."""
    r = int(3 * sigma)
    k = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma) ** 2)
    k /= k.sum()
    p = np.pad(v, ((r, r), (0, 0)), mode="edge")
    return np.stack([np.convolve(p[:, c], k, mode="valid") for c in range(v.shape[1])], axis=1)


def blend(win, gen):
    """The band from `gen`, everything else from `win`. The band's colour is corrected per row so it meets the panels,
    interpolated across the band. --match edge: difference between original and generated pixels just outside the
    band (same content, only the VAE's tone shift). --match inner: original just outside vs generated just inside the
    full-strength core - also fixes a sky the model painted in another colour inside the soft ramp, but tints the band
    where the two sides show different things in the same rows (sky next to a hill)."""
    o, g = np.asarray(win, np.float32), np.asarray(gen, np.float32)
    a, b = HALF - BAND // 2, HALF + BAND // 2
    if args.match == "inner":
        probe = 48
        d_l = smooth_rows(o[:, a - probe:a].mean(axis=1) - g[:, a + RAMP:a + RAMP + probe].mean(axis=1), 20.0)
        d_r = smooth_rows(o[:, b:b + probe].mean(axis=1) - g[:, b - RAMP - probe:b - RAMP].mean(axis=1), 20.0)
    else:
        probe = 24
        d_l = smooth_rows((o[:, a - probe:a] - g[:, a - probe:a]).mean(axis=1))
        d_r = smooth_rows((o[:, b:b + probe] - g[:, b:b + probe]).mean(axis=1))
    t = np.clip((np.arange(W) + 0.5 - a) / (b - a), 0.0, 1.0)[None, :, None]
    fixed = g + d_l[:, None, :] * (1 - t) + d_r[:, None, :] * t
    m = band_mask()[None, :, None]
    return o * (1 - m) + fixed * m


def detail_tile(j, tile, screen):
    """img2img of one room-size tile at --detail denoise: the composition stays, the model adds fine detail."""
    name = upload(tile, f"{args.concept}_tile{j}.png", "panorama2")
    g = Graph()
    unet, clip, vae = klein(g)
    lat = g("VAEEncode", pixels=g("LoadImage", image=name), vae=vae)
    pos = g("CLIPTextEncode", clip=clip, text=" ".join([C["style"], EDGES, beat(screen), "Sharp, highly detailed."]))
    neg = g("CLIPTextEncode", clip=clip, text=C.get("neg", "") + ", blurry, soft, upscaled")
    guider = g("CFGGuider", model=unet, positive=pos, negative=neg, cfg=5.0)
    sig = g("Flux2Scheduler", steps=args.steps, width=SCREEN_W, height=H_FULL)
    low = (g("SplitSigmasDenoise", sigmas=sig, denoise=args.detail)[0], 1)      # output 1: the low (late) sigmas
    sm = g("SamplerCustomAdvanced", noise=g("RandomNoise", noise_seed=args.seed + 200 + j), guider=guider,
           sampler=g("KSamplerSelect", sampler_name="euler"), sigmas=low, latent_image=lat)
    g("SaveImage", images=g("VAEDecode", samples=sm, vae=vae), filename_prefix=f"panorama2/{args.concept}_detail")
    return run(g)


def detail_pass(room):
    """Overlapping screen-size tiles around the ring, each img2img'd, cross-faded over the overlap."""
    total = room.shape[1]
    t = -(-total // (SCREEN_W * 3 // 4))                      # tiles: at most 3/4 of a screen apart
    xs = [round(j * total / t) for j in range(t)]
    ov = SCREEN_W - (total / t)
    ramp = np.minimum(1.0, np.minimum(np.arange(SCREEN_W) + 0.5, SCREEN_W - np.arange(SCREEN_W) - 0.5) / ov)
    acc, wsum = np.zeros_like(room), np.zeros(total, np.float32)
    for j, x in enumerate(xs):
        cols = (x + np.arange(SCREEN_W)) % total
        tile = Image.fromarray(np.clip(room[:, cols] + 0.5, 0, 255).astype(np.uint8))
        out = piece(os.path.join(DIR, f"detail_{j + 1:02d}.png"), f"d{j + 1}",
                    lambda: detail_tile(j, tile, int((x + SCREEN_W // 2) % total) // SCREEN_W))
        if out is None:
            raise SystemExit("detail tiles missing (--only)")
        acc[:, cols] += np.asarray(out, np.float32) * ramp[None, :, None]
        wsum[cols] += ramp
    return acc / wsum[None, :, None]


# ---------------------------------------------------------------- main
def piece(path, tag, make):
    if os.path.exists(path):
        return Image.open(path).convert("RGB")
    if ONLY is not None and tag not in ONLY:
        return None
    t0 = time.time()
    img = make()
    img.save(path)
    print(f"{tag} {time.time() - t0:.0f}s", flush=True)
    return img


panels = [piece(os.path.join(DIR, f"panel_{i + 1:02d}.png"), f"p{i + 1}", lambda i=i: panel(i)) for i in range(P)]
if any(p is None for p in panels):
    raise SystemExit("panels missing (--only)")

room = np.concatenate([np.asarray(p, np.float32) for p in panels], axis=1)
for k in range(P):
    path = os.path.join(DIR, f"seam_{k + 1:02d}.png")
    if not os.path.exists(path) and (ONLY is None or f"s{k + 1}" in ONLY):
        t0 = time.time()
        gen, _ = seam(k, panels[k], panels[(k + 1) % P])
        gen.save(path)
        print(f"s{k + 1} {time.time() - t0:.0f}s", flush=True)
    if not os.path.exists(path):
        continue
    win = np.concatenate([np.asarray(panels[k], np.float32)[:, HALF:], np.asarray(panels[(k + 1) % P], np.float32)[:, :HALF]], axis=1)
    out = blend(win, Image.open(path).convert("RGB"))
    x = (k + 1) * W - HALF                       # window start in the room; the ring window wraps around
    if k < P - 1:
        room[:, x:x + W] = out
    else:
        room[:, x:] = out[:, :HALF]
        room[:, :HALF] = out[:, HALF:]

if args.coarse:
    coarse = Image.fromarray(np.clip(room + 0.5, 0, 255).astype(np.uint8))
    coarse.save(os.path.join(DIR, f"{args.concept}_coarse.png"))
    room = np.asarray(coarse.resize((args.screens * SCREEN_W, H_FULL), Image.Resampling.LANCZOS), np.float32)
    room = detail_pass(room)
top = (H_FULL - ROOM_H) // 2
img = Image.fromarray(np.clip(room[top:top + ROOM_H] + 0.5, 0, 255).astype(np.uint8))
px = args.pixel_size if args.pixel_size is not None else C.get("pixel", 0)
name = os.path.join(DIR, f"{args.concept}_room")
if px:
    # A true pixel grid with one shared palette: image models only draw fake, uneven pixels.
    small = img.resize((img.width // px, img.height // px), Image.Resampling.BOX)
    small = small.quantize(args.colors, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE).convert("RGB")
    small.resize((small.width * px, small.height * px), Image.Resampling.NEAREST).save(name + ".png", optimize=True)
    print(name + ".png", small.size, f"x{px}")
else:
    img.save(name + ".jpg", quality=92)
    print(name + ".jpg", img.size)
img.resize((img.width // 8, img.height // 8), Image.Resampling.BOX).save(name + "_preview.jpg", quality=90)
