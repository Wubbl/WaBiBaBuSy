"""Generate the ComfyUI 'Panorama' workflow for WaBiBaBuSy's Panorama background (FLUX.2 Klein 9B).

A hero panel, then outpainting extensions to the right, stitched into one strip of N x 1344 x 768 px, the ring closed
(right edge flows into the left edge), saved as the strip plus a lanczos upscale to N x 1920 wide.
See .docs/2026.10_PANORAMA_CONTENT_GUIDE.md section 8 for why it is built the way it is.

Needs a running ComfyUI (reads /object_info for the node definitions) with the custom nodes from
tools/comfyui/panorama_tone_match.py copied into ComfyUI/custom_nodes.

    python gen_panorama_workflow.py --out F:/ComfyUI/ComfyUI/user/default/workflows/Panorama.json
    python gen_panorama_workflow.py --screens 8 --prefix row --out Panorama8.json   # 1-12 screens
"""
import argparse
import json
import math
import urllib.request

ap = argparse.ArgumentParser()
ap.add_argument("--server", default="http://127.0.0.1:8188")
ap.add_argument("--object-info", help="saved /object_info JSON instead of asking a running server")
ap.add_argument("--out", required=True, help="workflow file (UI format) to write")
ap.add_argument("--api", help="also write the graph in API format (POST /prompt)")
ap.add_argument("--screens", type=int, default=10, help="screens the strip spans (1344 px of strip per screen)")
ap.add_argument("--steps", type=int, default=20)
ap.add_argument("--prefix", default="deep_space", help="output file prefix under output/panorama/")
ap.add_argument("--id", default="b2d2edd6-9c79-4af2-a88e-5e09edfde2e7", help="workflow id (keep an existing file's id)")
args = ap.parse_args()
if args.screens < 2:
    ap.error("--screens must be at least 2 (one screen needs no panorama)")
if args.screens * 1344 > 16384:
    ap.error("ComfyUI image nodes stop at 16384 px (12 screens). For 2 rows, make one strip per row: "
             "the join between them lands in the turn gap, which no screen shows.")

OI = (json.load(open(args.object_info, encoding="utf-8")) if args.object_info
      else json.loads(urllib.request.urlopen(args.server + "/object_info").read()))
STEPS, PREFIX = args.steps, args.prefix

PANEL_W, H = 1344, 768
CTX, NEW = 336, 1008                        # context kept from the previous panel, new pixels
RIM = 128                                   # Klein darkens image borders (vignette): drop each panel's right rim
FEATHER = 64                                # soft mask edge inside the context
TARGET_W = args.screens * PANEL_W           # final strip width, cropped from the slightly longer chain
N_EXT = math.ceil((TARGET_W - (PANEL_W - RIM)) / (NEW - RIM))
RING = 672                                  # half of the ring-closure window
CFG = 5.0
SEED = 20261016

STYLE = ("Seamless segment of an ultra-wide space panorama, deep navy and violet nebula clouds, scattered bright stars, "
         "soft cinematic lighting, painterly digital illustration, highly detailed, open star field through the middle of "
         "the image, consistent star density and brightness, evenly lit to the very edges, no vignette, no frames, no borders, "
         "no text unless described.")
FILL = "Fill the empty grey area on the right so the image continues seamlessly with the same colors and brightness: "
BEATS = [
    "A large ringed planet low on the left, its rings glowing teal.",
    "Thin nebula filaments in magenta and blue drifting to the right.",
    "A small space station; a glowing white neon sign on its hull shows the handwritten lettering \"BiBaBu\" exactly as in "
    "the reference logo image.",
    "An asteroid belt with a few tiny lit mining lights.",
    "A huge spiral galaxy filling the upper area, its bright core glowing white.",
    "A comet with a long pale blue tail crossing the upper area.",
    "A twin-star system casting warm orange light into the nebula clouds.",
    "A constellation of bright stars connected by faint glowing lines that trace exactly the beard outline of the reference "
    "line drawing.",
    "A distant fleet of tiny spaceships with running lights in a row.",
    "A glowing golden nebula shaped exactly like the foaming beer mug of the reference line drawing.",
    "A small moon with a moon base whose dome windows glow in warm light.",
    "Glowing blue gas clouds with a few bright young stars inside.",
    "A golden asteroid shaped like a cat's head drifting slowly.",
    "Two round pink moons side by side, outlined by thin glowing white orbit lines shaped exactly like the reference line "
    "drawing.",
    "The nebula thins out, mostly stars, a faint teal glow.",
]
BEATS = [BEATS[i % len(BEATS)] for i in range(N_EXT + 1)]   # more screens: the beats repeat (edit them in ComfyUI)
# Club logo (Bier, Bart, Busen) as a second reference image on some beats: beat index -> image in ComfyUI/input
LOGOS = {2: "bibabu_text.png", 7: "bibabu_bart.png", 9: "bibabu_bier.png", 13: "bibabu_busen.png"}
RING_PROMPT = "Only an open star field and soft nebula clouds in the grey band, no planets, no asteroids, no spaceships, no objects."

# ---------------------------------------------------------------- graph builder
WIDGET_TYPES = {"INT", "FLOAT", "STRING", "BOOLEAN", "COMBO"}
nodes, links, groups = [], [], []
_next_link = [1]


def spec(t):
    d = OI[t]
    order = d.get("input_order", {})
    allin = {**d["input"].get("required", {}), **d["input"].get("optional", {})}
    names = order.get("required", []) + order.get("optional", []) if order else list(allin)
    socks, widgets = [], []
    for n in names:
        v = allin[n]
        typ = v[0]
        opts = v[1] if len(v) > 1 and isinstance(v[1], dict) else {}
        is_widget = (isinstance(typ, list) or typ in WIDGET_TYPES) and not opts.get("forceInput")
        tname = "COMBO" if isinstance(typ, list) else typ
        (widgets if is_widget else socks).append((n, tname, opts))
    outs = list(zip(d["output_name"], d["output"]))
    return socks, widgets, outs


class N:
    def __init__(self, t, pos, size=(300, 120), title=None, **kw):
        self.t, self.id, self.pos, self.size, self.title = t, len(nodes) + 1, pos, size, title
        self.socks, self.widgets, self.outs = spec(t)
        self.links_in = {}          # input name -> (src node, slot)
        self.values = {}
        for k, v in kw.items():
            if isinstance(v, tuple) and isinstance(v[0], N):
                self.links_in[k] = v
            elif isinstance(v, N):
                self.links_in[k] = (v, 0)
            else:
                self.values[k] = v
        nodes.append(self)

    def __getitem__(self, slot):
        return (self, slot)


def group(title, x, y, w, h, color="#3f789e"):
    groups.append({"id": len(groups) + 1, "title": title, "bounding": [x, y, w, h], "color": color, "font_size": 24, "flags": {}})


# ---------------------------------------------------------------- the graph
X0, Y0 = 0, 0
# Loaders
group("Models (FLUX.2 Klein 9B)", -40, -60, 380, 620, "#444")
# No Differential Diffusion: in its last steps it repaints the kept context too (darker), which shows as seams.
unet = N("UNETLoader", (0, 0), (320, 82), unet_name="flux-2-klein-base-9b-fp8.safetensors", weight_dtype="default")
clip = N("CLIPLoader", (0, 120), (320, 106), clip_name="qwen_3_8b_fp8mixed.safetensors", type="flux2", device="default")
vae = N("VAELoader", (0, 260), (320, 58), vae_name="flux2-vae.safetensors")
sampler = N("KSamplerSelect", (0, 350), (320, 58), sampler_name="euler")
noise = N("RandomNoise", (0, 440), (320, 82), title="Seed (fixed: re-runs reuse cached panels)", noise_seed=SEED)
sigmas = N("Flux2Scheduler", (0, 560), (320, 106), steps=STEPS, width=PANEL_W, height=H)
neg = N("CLIPTextEncode", (0, 700), (320, 90), title="Negative (empty)", clip=clip, text="")

# Prompts
PX = 420
group("Prompts - style prefix + one beat per panel (edit here)", PX - 40, -60, 560, 160 + 130 * (N_EXT + 2), "#3f5f3f")
style = N("PrimitiveStringMultiline", (PX, 0), (480, 140), title="Style prefix (every panel)", value=STYLE)
beat_txt = []
for i in range(N_EXT + 1):
    s = N("PrimitiveStringMultiline", (PX, 170 + 130 * i), (480, 110),
          title=("Beat 1 - hero panel" if i == 0 else f"Beat {i + 1} - extension {i}"), value=BEATS[i])
    beat_txt.append(s)
ring_txt = N("PrimitiveStringMultiline", (PX, 170 + 130 * (N_EXT + 1)), (480, 110), title="Ring closure", value=RING_PROMPT)


fill = N("PrimitiveStringMultiline", (PX, -150), (480, 90), title="Fill instruction (extensions + ring)", value=FILL)


# Logo references (each loaded, downsized and encoded once)
logo_lat = {}
used = sorted({img for k, img in LOGOS.items() if 1 <= k <= N_EXT})
if used:
    LY = 170 + 130 * (N_EXT + 2) + 60
    group("Logo references (BiBaBu: Bier, Bart, Busen) - swap the images here", PX - 40, LY - 40, 560, 60 + 330 * len(used), "#7a3f5f")
    for i, img in enumerate(used):
        y = LY + 330 * i
        li = N("LoadImage", (PX, y), (240, 300), title=img, image=img)
        sc = N("ImageScaleToTotalPixels", (PX + 260, y), (220, 106), image=li[0], upscale_method="lanczos", megapixels=0.5, resolution_steps=16)
        logo_lat[img] = N("VAEEncode", (PX + 260, y + 150), (200, 50), pixels=sc, vae=vae)


def encode(text_node, pos, instruct=False):
    head = N("StringConcatenate", (pos[0], pos[1] - 120), (260, 106), string_a=fill, string_b=style, delimiter="") if instruct else style
    cat = N("StringConcatenate", pos, (260, 106), string_a=head, string_b=text_node, delimiter=" ")
    return N("CLIPTextEncode", (pos[0], pos[1] + 140), (260, 60), clip=clip, text=cat)


# Hero
HX = 1060
group("1 - Hero panel 1344 x 768", HX - 40, -60, 1000, 560)
pos0 = encode(beat_txt[0], (HX, 0))
guider0 = N("CFGGuider", (HX + 300, 0), (260, 106), model=unet, positive=pos0, negative=neg, cfg=CFG)
lat0 = N("EmptyFlux2LatentImage", (HX + 300, 150), (260, 106), width=PANEL_W, height=H, batch_size=1)
samp0 = N("SamplerCustomAdvanced", (HX + 300, 300), (260, 110), noise=noise, guider=guider0, sampler=sampler, sigmas=sigmas, latent_image=lat0)
hero = N("VAEDecode", (HX + 600, 0), (200, 50), samples=samp0[0], vae=vae)
N("PreviewImage", (HX + 600, 80), (320, 220), images=hero)

# Extensions
strip, strip_w, prev, prev_lat = hero, PANEL_W, hero, samp0[0]
exts = []   # (strip x where this extension's kept part starts, its sampled latent)
EY = 560
for k in range(1, N_EXT + 1):
    y = EY + (k - 1) * 520
    group(f"2.{k} - Extension {k} (beat {k + 1})", HX - 40, y - 40, 2200, 500)
    pos = encode(beat_txt[k], (HX, y + 130), instruct=True)
    ctx = N("ImageCrop", (HX + 300, y), (220, 130), title="Context: 336 px before the previous panel's dark rim", image=prev, width=CTX, height=H, x=PANEL_W - RIM - CTX, y=0)
    pad = N("ImagePadForOutpaint", (HX + 300, y + 170), (220, 170), image=ctx, left=0, top=0, right=NEW, bottom=0, feathering=FEATHER)
    pad_lat = N("VAEEncode", (HX + 560, y + 90), (200, 50), title="Encode padded canvas (reference only)", pixels=pad[0], vae=vae)
    # The context comes straight from the previous panel's sampled latent: re-encoded pixels decode in a different
    # tone next to sampled latents (VAE decoder), which showed as dark seams.
    ctx_lat = N("PanoramaLatentSlice", (HX + 560, y + 180), (220, 106), title="Context latent", samples=prev_lat, x=PANEL_W - RIM - CTX, width=CTX, downscale=16)
    canvas = N("PanoramaLatentPadRight", (HX + 560, y + 320), (220, 82), samples=ctx_lat, right=NEW, downscale=16)
    masked = N("SetLatentNoiseMask", (HX + 800, y + 180), (200, 50), samples=canvas, mask=pad[1])
    rpos = N("ReferenceLatent", (HX + 800, y), (200, 50), conditioning=pos, latent=pad_lat)
    rneg = N("ReferenceLatent", (HX + 800, y + 90), (200, 50), conditioning=neg, latent=pad_lat)
    if k in LOGOS:   # second reference: the logo this beat draws
        rpos = N("ReferenceLatent", (HX + 800, y + 330), (200, 50), title="+ logo reference", conditioning=rpos, latent=logo_lat[LOGOS[k]])
        rneg = N("ReferenceLatent", (HX + 800, y + 400), (200, 50), title="+ logo reference", conditioning=rneg, latent=logo_lat[LOGOS[k]])
    g = N("CFGGuider", (HX + 1040, y), (220, 106), model=unet, positive=rpos, negative=rneg, cfg=CFG)
    sm = N("SamplerCustomAdvanced", (HX + 1040, y + 150), (220, 110), noise=noise, guider=g, sampler=sampler, sigmas=sigmas, latent_image=masked)
    ext = N("VAEDecode", (HX + 1300, y), (200, 50), samples=sm[0], vae=vae)
    N("PreviewImage", (HX + 1300, y + 80), (320, 220), images=ext)
    keep = N("ImageCrop", (HX + 1660, y), (220, 130), title="New part (+feather)", image=ext, width=NEW + FEATHER, height=H, x=CTX - FEATHER, y=0)
    trim = N("ImageCrop", (HX + 1660, y + 170), (220, 130), title="Strip so far minus rim + feather", image=strip, width=strip_w - RIM - FEATHER, height=H, x=0, y=0)
    strip = N("ImageStitch", (HX + 1900, y), (220, 130), image1=trim, image2=keep, direction="right", match_image_size=False, spacing_width=0, spacing_color="white")
    exts.append((strip_w - RIM - FEATHER, sm))
    strip_w += NEW - RIM
    prev, prev_lat = ext, sm[0]

# Drop the last panel's rim and cut to the target width
strip_w = min(TARGET_W, strip_w - RIM)
strip = N("ImageCrop", (HX + 1900, EY + N_EXT * 520 - 200), (220, 130), title=f"Strip {strip_w} px", image=strip, width=strip_w, height=H, x=0, y=0)

# Ring closure, in latent space: the end of the last extension | the start of the hero, grey band in the middle
RY = EY + N_EXT * 520
BAND = 448
group(f"3 - Close the ring (right edge flows into left edge), strip {strip_w} x {H}", HX - 40, RY - 40, 2200, 560, "#7a5f3f")
ring_fill = N("PrimitiveStringMultiline", (HX - 520, RY), (440, 90), title="Ring fill instruction",
              value="Fill the empty grey band in the middle so the left and right parts join seamlessly with the same colors and brightness: ")
ring_head = N("StringConcatenate", (HX, RY - 120), (260, 106), string_a=ring_fill, string_b=style, delimiter="")
ring_cat = N("StringConcatenate", (HX, RY), (260, 106), string_a=ring_head, string_b=ring_txt, delimiter=" ")
rpos_txt = N("CLIPTextEncode", (HX, RY + 140), (260, 60), clip=clip, text=ring_cat)
# Which sampled latent holds which strip columns: the hero up to the first extension, then each extension from its
# start (strip x S, its own x CTX - FEATHER) up to the next one's start.
owners = [(0, samp0, 0)] + [(sx, sl, CTX - FEATHER) for sx, sl in exts]
pieces = []
for i, (sx, sl, off) in enumerate(owners):
    seg_end = owners[i + 1][0] if i + 1 < len(owners) else strip_w
    a0, a1 = max(sx, strip_w - RING), min(seg_end, strip_w)
    if a1 > a0:
        assert (a0 - sx + off) % 16 == 0 and (a1 - a0) % 16 == 0
        pieces.append(N("PanoramaLatentSlice", (HX + 300, RY + 120 * len(pieces)), (220, 106), title="Strip end (sampled latent)",
                        samples=sl[0], x=a0 - sx + off, width=a1 - a0, downscale=16))
end_lat = pieces[0]
for pc in pieces[1:]:
    end_lat = N("PanoramaLatentConcat", (HX + 300, RY + 260), (220, 60), left=end_lat, right=pc)
start_lat = N("PanoramaLatentSlice", (HX + 300, RY - 150), (220, 106), title="Strip start (hero latent)", samples=samp0[0], x=0, width=RING, downscale=16)
j_lat = N("PanoramaLatentConcat", (HX + 560, RY), (220, 60), left=end_lat, right=start_lat)
ref_l = N("ImageCrop", (HX + 300, RY + 300), (220, 130), title="End, left of the band", image=strip, width=RING - BAND // 2, height=H, x=strip_w - RING, y=0)
grey = N("EmptyImage", (HX + 300, RY + 470), (220, 130), title="Grey band", width=BAND, height=H, batch_size=1, color=0x7F7F7F)
ref_r = N("ImageCrop", (HX + 560, RY + 470), (220, 130), title="Start, right of the band", image=strip, width=RING - BAND // 2, height=H, x=BAND // 2, y=0)
ref_a = N("ImageStitch", (HX + 560, RY + 150), (220, 130), image1=ref_l, image2=grey, direction="right", match_image_size=False, spacing_width=0, spacing_color="white")
ref_img = N("ImageStitch", (HX + 560, RY + 300), (220, 130), title="Reference: end | grey | start", image1=ref_a, image2=ref_r, direction="right", match_image_size=False, spacing_width=0, spacing_color="white")
ref_lat2 = N("VAEEncode", (HX + 800, RY + 300), (200, 50), pixels=ref_img, vae=vae)
m_bg = N("SolidMask", (HX + 800, RY + 380), (220, 106), value=0.0, width=2 * RING, height=H)
m_band = N("SolidMask", (HX + 800, RY + 520), (220, 106), value=1.0, width=BAND, height=H)
m = N("MaskComposite", (HX + 1040, RY + 380), (220, 130), destination=m_bg, source=m_band, x=RING - BAND // 2, y=0, operation="add")
m_soft = N("GrowMaskWithBlur", (HX + 1040, RY + 540), (220, 250), mask=m, expand=0, incremental_expandrate=0.0, tapered_corners=True,
           flip_input=False, blur_radius=24.0, lerp_alpha=1.0, decay_factor=1.0, fill_holes=False)
j_mask = N("SetLatentNoiseMask", (HX + 800, RY + 150), (200, 50), samples=j_lat, mask=m_soft[0])
r_pos = N("ReferenceLatent", (HX + 800, RY), (200, 50), conditioning=rpos_txt, latent=ref_lat2)
ring_neg = N("CLIPTextEncode", (HX - 520, RY + 140), (440, 90), title="Ring negative (no copied objects in the band)", clip=clip,
             text="asteroids, rocks, planets, moons, spaceships, space station, text, letters, repeated objects")
r_neg = N("ReferenceLatent", (HX + 800, RY + 70), (200, 50), conditioning=ring_neg, latent=ref_lat2)
rg = N("CFGGuider", (HX + 1040, RY), (220, 106), model=unet, positive=r_pos, negative=r_neg, cfg=CFG)
rs = N("SamplerCustomAdvanced", (HX + 1040, RY + 150), (220, 110), noise=noise, guider=rg, sampler=sampler, sigmas=sigmas, latent_image=j_mask)
closed_raw = N("VAEDecode", (HX + 1300, RY - 60), (200, 50), samples=rs[0], vae=vae)
strip_end = N("ImageCrop", (HX + 1300, RY + 320), (220, 130), title="Strip end (pixels)", image=strip, width=RING, height=H, x=strip_w - RING, y=0)
strip_start = N("ImageCrop", (HX + 1300, RY + 470), (220, 130), title="Strip start (pixels)", image=strip, width=RING, height=H, x=0, y=0)
joined_px = N("ImageStitch", (HX + 1540, RY + 320), (220, 130), image1=strip_end, image2=strip_start, direction="right", match_image_size=False, spacing_width=0, spacing_color="white")
closed = N("PanoramaToneMatch", (HX + 1300, RY), (220, 80), title="Tone match to the strip", original=joined_px, generated=closed_raw, mask=m_soft[0], fade_px=BAND // 2)
N("PreviewImage", (HX + 1300, RY + 80), (320, 220), images=closed)
new_start = N("ImageCrop", (HX + 1660, RY), (220, 130), title="Fixed start", image=closed, width=RING, height=H, x=RING, y=0)
middle = N("ImageCrop", (HX + 1660, RY + 170), (220, 130), title="Untouched middle", image=strip, width=strip_w - 2 * RING, height=H, x=RING, y=0)
new_end = N("ImageCrop", (HX + 1660, RY + 340), (220, 130), title="Fixed end", image=closed, width=RING, height=H, x=0, y=0)
a = N("ImageStitch", (HX + 1900, RY), (220, 130), image1=new_start, image2=middle, direction="right", match_image_size=False, spacing_width=0, spacing_color="white")
final = N("ImageStitch", (HX + 1900, RY + 170), (220, 130), image1=a, image2=new_end, direction="right", match_image_size=False, spacing_width=0, spacing_color="white")

# Output
OY = RY + 560
ROOM_W = args.screens * 1920
group(f"4 - Save: strip + room size ({ROOM_W} wide for {args.screens} x 1920)", HX - 40, OY - 40, 1400, 420, "#5f3f7a")
N("SaveImage", (HX, OY), (600, 300), title=f"Strip {strip_w} x {H}", images=final, filename_prefix=f"panorama/{PREFIX}_strip")
up = N("ImageScaleBy", (HX + 640, OY), (260, 82), title=f"Upscale to {ROOM_W} wide", image=final, upscale_method="lanczos", scale_by=round(ROOM_W / strip_w, 4))
N("SaveImage", (HX + 920, OY), (440, 300), title="Room size", images=up, filename_prefix=f"panorama/{PREFIX}_room")


# ---------------------------------------------------------------- serialize
def link_value(v):
    src, slot = v
    return [str(src.id), slot]


api = {}
for n in nodes:
    inputs = {}
    for name, _, _ in n.socks + n.widgets:
        if name in n.links_in:
            inputs[name] = link_value(n.links_in[name])
        elif name in n.values:
            inputs[name] = n.values[name]
    api[str(n.id)] = {"class_type": n.t, "inputs": inputs, "_meta": {"title": n.title or n.t}}
if args.api:
    json.dump(api, open(args.api, "w", encoding="utf-8"), indent=1)

ui_links = []
out_links = {}   # (node id, slot) -> [link ids]
in_link = {}     # (node id, input name) -> link id
for n in nodes:
    ordered = n.socks + n.widgets
    for name, tname, _ in ordered:
        if name in n.links_in:
            src, slot = n.links_in[name]
            lid = len(ui_links) + 1
            to_slot = [x[0] for x in ordered].index(name)
            ui_links.append([lid, src.id, slot, n.id, to_slot, src.outs[slot][1]])
            out_links.setdefault((src.id, slot), []).append(lid)
            in_link[(n.id, name)] = lid

ui_nodes = []
for order, n in enumerate(nodes):
    inputs = []
    for name, tname, _ in n.socks:
        inputs.append({"localized_name": name, "name": name, "type": tname, "link": in_link.get((n.id, name))})
    for name, tname, _ in n.widgets:
        inputs.append({"localized_name": name, "name": name, "type": tname, "widget": {"name": name}, "link": in_link.get((n.id, name))})
    outputs = [{"localized_name": on, "name": on, "type": ot, "slot_index": i, "links": out_links.get((n.id, i), [])}
               for i, (on, ot) in enumerate(n.outs)]
    wv = []
    for name, tname, opts in n.widgets:
        v = n.values.get(name, opts.get("default", ""))
        wv.append(v)
        if opts.get("control_after_generate"):
            wv.append("fixed")
        if opts.get("image_upload"):
            wv.append("image")
    node = {"id": n.id, "type": n.t, "pos": list(n.pos), "size": list(n.size), "flags": {}, "order": order, "mode": 0,
            "inputs": inputs, "outputs": outputs, "properties": {"Node name for S&R": n.t}, "widgets_values": wv}
    if n.title:
        node["title"] = n.title
    ui_nodes.append(node)

ui = {"id": args.id, "revision": 0, "last_node_id": len(nodes), "last_link_id": len(ui_links),
      "nodes": ui_nodes, "links": ui_links, "groups": groups, "config": {},
      "extra": {"workflowRendererVersion": "LG", "ds": {"scale": 0.45, "offset": [100, 100]}}, "version": 0.4}
json.dump(ui, open(args.out, "w", encoding="utf-8"), indent=1)
print(f"{len(nodes)} nodes, {len(ui_links)} links, strip {strip_w} x {H}, ext={N_EXT}, steps={STEPS}")
