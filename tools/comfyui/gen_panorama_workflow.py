"""Generate the ComfyUI 'Panorama' workflow for WaBiBaBuSy's Panorama background (FLUX.2 Klein 9B).

One independent panel per screen (1344 x 768, own seed), then every join - including the ring join from the last
panel back to the first - is repainted as a 576 px grey band with both neighbours as context. Every pixel is at most
one generation away from a fresh one, so nothing drifts (a left-to-right outpainting chain degraded visibly after
~6 panels). Saved as the strip plus a lanczos upscale to N x 1920 wide.
See .docs/2026.10_PANORAMA_CONTENT_GUIDE.md section 8 for why it is built the way it is.

Needs a running ComfyUI (reads /object_info for the node definitions) with the custom nodes from
tools/comfyui/panorama_tone_match.py copied into ComfyUI/custom_nodes.

    python gen_panorama_workflow.py --out F:/ComfyUI/ComfyUI/user/default/workflows/Panorama.json
    python gen_panorama_workflow.py --concept neon_harbour --out ".../workflows/Panorama - Neon Harbour.json"
    python gen_panorama_workflow.py --screens 8 --prefix row --out Panorama8.json   # 2-12 screens
"""
import argparse
import json
import urllib.request
import uuid

ap = argparse.ArgumentParser()
ap.add_argument("--server", default="http://127.0.0.1:8188")
ap.add_argument("--object-info", help="saved /object_info JSON instead of asking a running server")
ap.add_argument("--out", required=True, help="workflow file (UI format) to write")
ap.add_argument("--api", help="also write the graph in API format (POST /prompt)")
ap.add_argument("--screens", type=int, default=10, help="screens the strip spans (1344 px of strip per screen)")
ap.add_argument("--steps", type=int, default=20)
ap.add_argument("--concept", default="deep_space",
                choices=["deep_space", "neon_harbour", "mountains", "fantasy_realm", "pixel_quest"])
ap.add_argument("--pixel-size", type=int, help="snap the room image to a pixel grid of N x N screen pixels "
                "(2, 3, 4, 5, 6 or 8; 0 = off; default: the concept's, pixel_quest = 6)")
ap.add_argument("--colors", type=int, default=48, help="palette size for --pixel-size")
ap.add_argument("--prefix", help="output file prefix under output/panorama/ (default: the concept)")
ap.add_argument("--id", help="workflow id (default: stable per concept; deep_space keeps the original Panorama id)")
args = ap.parse_args()
if args.screens < 2:
    ap.error("--screens must be at least 2 (one screen needs no panorama)")
if args.screens * 1344 > 16384:
    ap.error("ComfyUI image nodes stop at 16384 px (12 screens). For 2 rows, make one strip per row: "
             "the join between them lands in the turn gap, which no screen shows.")

OI = (json.load(open(args.object_info, encoding="utf-8")) if args.object_info
      else json.loads(urllib.request.urlopen(args.server + "/object_info").read()))
STEPS, PREFIX = args.steps, args.prefix or args.concept
WORKFLOW_ID = args.id or ("b2d2edd6-9c79-4af2-a88e-5e09edfde2e7" if args.concept == "deep_space"
                          else str(uuid.uuid5(uuid.NAMESPACE_URL, "wabibabusy/panorama/" + args.concept)))

PANEL_W, H = 1344, 768
P = args.screens                            # panels = screens
HALF = PANEL_W // 2                         # a seam window is the right half of one panel + the left half of the next
BAND = 576                                  # repainted band centred on each join (covers Klein's dark panel rims)
CFG = 5.0
SEED = 20261016

EDGES = "evenly lit to the very edges, no vignette, no frames, no borders, no text unless described."
# Panels only (not the seams): keeps each beat's subject away from the edges the seams repaint. Saying "a small centred
# subject" instead made Klein invent one (a lone human figure) wherever the beat's subject was elsewhere.
LAYOUT = "The described subject is in the middle of the image; nothing important near the left and right edges."
LOGO_TEXT, BART, BIER, BUSEN = "bibabu_text.png", "bibabu_bart.png", "bibabu_bier.png", "bibabu_busen.png"
REF = "exactly like the reference line drawing"
SEAM_FILL = "Fill the empty grey band in the middle so the left and right parts join seamlessly with the same colors and brightness: "

# One entry per wallpaper: style prefix, one beat per panel (10 for a 10-screen room; more screens repeat them), the
# beats that get the club logo (Bier, Bart, Busen) as a reference image (beat index -> image in ComfyUI/input), and the
# seam prompts (every join and the ring join use them).
CONCEPTS = {
    "deep_space": dict(
        style="Seamless segment of an ultra-wide space panorama, deep navy space with scattered bright stars, a soft band of "
              "purple and violet nebula clouds running horizontally across the middle of the image, soft cinematic "
              "lighting, painterly digital illustration, highly detailed, " + EDGES,
        beats=[
            "A large ringed planet with teal glowing rings.",
            "A comet with a long pale blue tail crossing the upper area.",
            "A small space station; a glowing white neon sign on its hull shows the handwritten lettering \"BiBaBu\" "
            "exactly as in the reference logo image.",
            "A huge spiral galaxy, its bright core glowing white.",
            "A constellation of bright stars connected by faint glowing lines that trace the beard outline " + REF + ".",
            "A twin-star system casting warm orange light into the nebula clouds.",
            "A glowing golden nebula shaped like the foaming beer mug, " + REF + ".",
            "A golden asteroid shaped like a cat's head drifting slowly.",
            "Two round pink moons side by side, outlined by thin glowing white orbit lines shaped " + REF + ".",
            "An asteroid belt with a few tiny lit mining lights.",
        ],
        logos={2: LOGO_TEXT, 4: BART, 6: BIER, 8: BUSEN},
        ring="Only an open star field and the soft nebula band in the grey band, no planets, no asteroids, no spaceships, "
             "no objects.",
        ring_neg="asteroids, rocks, planets, moons, spaceships, space station, comet, galaxy, person, human figure, silhouette, "
                 "text, letters, repeated objects",
    ),
    "neon_harbour": dict(
        style="Seamless segment of an ultra-wide panorama of a cozy synthwave harbour city at night, neon magenta and cyan "
              "with warm orange windows, wet quays reflecting the lights, light rain haze, horizon line exactly at 62 "
              "percent of the image height, calm harbour water in the lower third, painterly digital illustration, soft "
              "bloom, rich detail, " + EDGES,
        beats=[
            "A lighthouse on a stone pier, a cat silhouette sitting at its foot.",
            "Old brick warehouses along the quay, a cargo crane holding a wooden crate.",
            "A waterfront bar with a large glowing pink neon sign showing the handwritten lettering \"BiBaBu\" exactly as "
            "in the reference logo image.",
            "A rooftop LAN party under string lights, many glowing computer monitors.",
            "A large white line-art mural of a beard on a brick wall, painted " + REF + ".",
            "A huge blimp drifting over the skyline, its belly lit by the city lights.",
            "A brewery with a giant glowing yellow neon sign of a foaming beer mug, shaped " + REF + ".",
            "A canal with arched stone bridges and long reflections of neon lights.",
            "A lingerie boutique with a glowing white neon sign of a bra outline, shaped " + REF + ".",
            "A ferry terminal, a ferry with its lights on and a few moored boats.",
        ],
        logos={2: LOGO_TEXT, 4: BART, 6: BIER, 8: BUSEN},
        ring="Only calm harbour water, a quay, plain buildings and soft city lights in the grey band, no signs, no text, "
             "no landmarks.",
        ring_neg="text, letters, neon signs, logos, lighthouse, crane, blimp, tower, boats, person, human figure, silhouette, "
                 "repeated objects",
    ),
    "mountains": dict(
        style="Seamless segment of an ultra-wide alpine panorama at dusk, layered mountain ridges fading into blue haze, "
              "warm peach and lavender sky, a calm lake in the foreground reflecting the sky, horizon line exactly at 60 "
              "percent of the image height, matte painting style, soft light, highly detailed, " + EDGES,
        beats=[
            "A wooden cabin with a lit window on the lake shore.",
            "A pine forest and a waterfall falling from the ridge into the lake.",
            "A mountain hut with a wooden sign over the door, carved with the handwritten lettering \"BiBaBu\" exactly as "
            "in the reference logo image.",
            "A small sailing boat on the still lake.",
            "A rocky mountain face whose snow-covered ridges form the beard outline, " + REF + ".",
            "The highest peak catching the last pink sunlight.",
            "A beer garden on the meadow with a big wooden sign of a foaming beer mug, shaped " + REF + ".",
            "Hot-air balloons over the far ridges.",
            "Two round snowy hilltops side by side with a glowing sunset rim, shaped " + REF + ".",
            "A small chapel on a hill above alpine meadows with flowers.",
        ],
        logos={2: LOGO_TEXT, 4: BART, 6: BIER, 8: BUSEN},
        ring="Only mountain ridges, blue haze, sky and the calm lake in the grey band, no buildings, no signs, no text.",
        ring_neg="text, letters, signs, logos, buildings, cabin, hut, boats, balloons, person, human figure, silhouette, "
                 "repeated objects",
    ),
    "fantasy_realm": dict(
        style="Seamless segment of an ultra-wide high-fantasy landscape panorama at golden hour, a wide river valley with "
              "distant snowy peaks and floating islands, warm golden light and soft mist, horizon line exactly at 60 percent "
              "of the image height, epic painterly concept art, highly detailed, " + EDGES,
        beats=[
            "A white castle with tall spires on a cliff, banners waving.",
            "A floating island with a waterfall pouring into the clouds below.",
            "The great stone gate of a dwarven city in the mountainside, the handwritten lettering \"BiBaBu\" carved in "
            "gold above it exactly as in the reference logo image.",
            "A green dragon flying over a forest, seen from far away.",
            "A colossal dwarf statue carved into the mountain, its stone beard shaped " + REF + ".",
            "An elven forest with giant glowing trees and hanging lanterns.",
            "A cozy fantasy tavern with a hanging wooden sign of a foaming beer mug, shaped " + REF + ".",
            "A wizard tower with a glowing crystal on top.",
            "Two round glowing crystal orbs side by side on a stone altar, their golden rims shaped " + REF + ".",
            "A village with thatched roofs by the river, smoke rising from the chimneys.",
        ],
        logos={2: LOGO_TEXT, 4: BART, 6: BIER, 8: BUSEN},
        ring="Only the river valley, meadows, distant peaks and sky in the grey band, no buildings, no creatures, no text.",
        ring_neg="text, letters, signs, castle, tower, statue, dragon, buildings, person, human figure, silhouette, "
                 "repeated objects",
    ),
    "pixel_quest": dict(
        style="Seamless segment of an ultra-wide 16-bit pixel art video game background, side view like a classic "
              "platformer level, crisp pixels, limited color palette, flat shading, bright blue sky with pixel clouds, "
              "grassy ground along the bottom fifth of the image, horizon line exactly at 60 percent of the image height, "
              + EDGES,
        beats=[
            "A small pixel art castle with flags on a green hill.",
            "Pixel art trees and bushes, a treasure chest in the grass.",
            "A pixel art inn with a big wooden sign showing the handwritten lettering \"BiBaBu\" exactly as in the "
            "reference logo image.",
            "A pixel art windmill and a wooden bridge over a small river.",
            "A giant pixel art stone statue of a beard on a pedestal, shaped " + REF + ".",
            "A pixel art cave entrance in a rocky hill, lit by two torches.",
            "A pixel art tavern with a big hanging sign of a foaming beer mug, shaped " + REF + ".",
            "Golden pixel art coins floating above brick platforms.",
            "Two round pixel art hot-air balloons side by side in the sky, their outlines shaped " + REF + ".",
            "A pixel art campfire with two tents and a sleeping cat.",
        ],
        logos={2: LOGO_TEXT, 4: BART, 6: BIER, 8: BUSEN},
        ring="Only pixel art grass, ground, sky and clouds in the grey band, no buildings, no objects, no text.",
        ring_neg="text, letters, signs, buildings, castle, statue, balloons, person, human figure, silhouette, "
                 "repeated objects, blurry, smooth gradients",
        pixel=6,
    ),
}
C = CONCEPTS[args.concept]
STYLE, RING_PROMPT, RING_NEG = C["style"], C["ring"], C["ring_neg"]
BEATS = [C["beats"][i % len(C["beats"])] for i in range(P)]   # more screens: the beats repeat (edit them in ComfyUI)
LOGOS = C["logos"]
PIXEL = args.pixel_size if args.pixel_size is not None else C.get("pixel", 0)
if PIXEL and (PIXEL not in (2, 3, 4, 5, 6, 8)):
    ap.error("--pixel-size must divide 1920 and 1080 and be at most 8: 2, 3, 4, 5, 6 or 8")

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
group("Models (FLUX.2 Klein 9B)", -40, -60, 380, 620, "#444")
# No Differential Diffusion: in its last steps it repaints the kept context too, which shows as seams.
unet = N("UNETLoader", (0, 0), (320, 82), unet_name="flux-2-klein-base-9b-fp8.safetensors", weight_dtype="default")
clip = N("CLIPLoader", (0, 120), (320, 106), clip_name="qwen_3_8b_fp8mixed.safetensors", type="flux2", device="default")
vae = N("VAELoader", (0, 260), (320, 58), vae_name="flux2-vae.safetensors")
sampler = N("KSamplerSelect", (0, 350), (320, 58), sampler_name="euler")
sigmas = N("Flux2Scheduler", (0, 440), (320, 106), steps=STEPS, width=PANEL_W, height=H)
neg = N("CLIPTextEncode", (0, 580), (320, 90), title="Negative (empty)", clip=clip, text="")

# Prompts
PX = 420
group("Prompts - style prefix + one beat per screen (edit here)", PX - 40, -200, 560, 300 + 130 * (P + 2), "#3f5f3f")
style = N("PrimitiveStringMultiline", (PX, 0), (480, 140), title="Style prefix (every panel)", value=STYLE)
beat_txt = [N("PrimitiveStringMultiline", (PX, 170 + 130 * i), (480, 110), title=f"Beat {i + 1} - screen {i + 1}", value=BEATS[i])
            for i in range(P)]
seam_fill = N("PrimitiveStringMultiline", (PX, -150), (480, 90), title="Seam fill instruction", value=SEAM_FILL)
layout = N("PrimitiveStringMultiline", (PX - 520, 0), (440, 90), title="Panel layout (panels only)", value=LAYOUT)
panel_head = N("StringConcatenate", (PX - 520, 130), (260, 106), string_a=style, string_b=layout, delimiter=" ")
seam_txt = N("PrimitiveStringMultiline", (PX, 170 + 130 * P), (480, 110), title="Seams (every join + ring)", value=RING_PROMPT)

# Logo references (each loaded, downsized and encoded once)
logo_lat = {}
used = sorted({img for k, img in LOGOS.items() if k < P})
if used:
    LY = 170 + 130 * (P + 2) + 60
    group("Logo references (BiBaBu: Bier, Bart, Busen) - swap the images here", PX - 40, LY - 40, 560, 60 + 330 * len(used), "#7a3f5f")
    for i, img in enumerate(used):
        y = LY + 330 * i
        li = N("LoadImage", (PX, y), (240, 300), title=img, image=img)
        sc = N("ImageScaleToTotalPixels", (PX + 260, y), (220, 106), image=li[0], upscale_method="lanczos", megapixels=0.5, resolution_steps=16)
        logo_lat[img] = N("VAEEncode", (PX + 260, y + 150), (200, 50), pixels=sc, vae=vae)


def encode(text_node, pos, head=None):
    cat = N("StringConcatenate", pos, (260, 106), string_a=head or style, string_b=text_node, delimiter=" ")
    return N("CLIPTextEncode", (pos[0], pos[1] + 140), (260, 60), clip=clip, text=cat)


# 1 - Panels: independent text-to-image, one seed each
HX = 1060
panels = []
for i in range(P):
    y = i * 440
    group(f"1.{i + 1} - Panel {i + 1}", HX - 40, y - 40, 1300, 420)
    pos, ng = encode(beat_txt[i], (HX, y), head=panel_head), neg
    if i in LOGOS:   # the club logo this beat draws, as a reference image
        pos = N("ReferenceLatent", (HX + 300, y + 260), (200, 50), title="+ logo reference", conditioning=pos, latent=logo_lat[LOGOS[i]])
        ng = N("ReferenceLatent", (HX + 300, y + 330), (200, 50), title="+ logo reference", conditioning=ng, latent=logo_lat[LOGOS[i]])
    seed = N("RandomNoise", (HX + 300, y), (220, 82), title=f"Seed panel {i + 1}", noise_seed=SEED + i)
    g = N("CFGGuider", (HX + 300, y + 120), (220, 106), model=unet, positive=pos, negative=ng, cfg=CFG)
    lat = N("EmptyFlux2LatentImage", (HX + 540, y), (220, 106), width=PANEL_W, height=H, batch_size=1)
    sm = N("SamplerCustomAdvanced", (HX + 540, y + 150), (220, 110), noise=seed, guider=g, sampler=sampler, sigmas=sigmas, latent_image=lat)
    img = N("VAEDecode", (HX + 790, y), (200, 50), samples=sm[0], vae=vae)
    N("PreviewImage", (HX + 790, y + 80), (400, 260), images=img)
    panels.append((sm, img))

# 2 - Seams: right half of panel k | left half of panel k+1 (the last one wraps to panel 1 = ring), grey band in the middle
SX = HX + 1400
group("2 - Seams: shared prompt, band mask and grey", SX - 40, -200, 600, 1180, "#7a5f3f")
seam_head = N("StringConcatenate", (SX, -150), (260, 106), string_a=seam_fill, string_b=style, delimiter="")
seam_pos = encode(seam_txt, (SX, 0), head=seam_head)
seam_neg = N("CLIPTextEncode", (SX, 260), (440, 90), title="Seam negative (no copied objects in the band)", clip=clip, text=RING_NEG)
m_bg = N("SolidMask", (SX, 400), (220, 106), value=0.0, width=PANEL_W, height=H)
m_band = N("SolidMask", (SX, 540), (220, 106), value=1.0, width=BAND, height=H)
m = N("MaskComposite", (SX + 260, 400), (220, 130), destination=m_bg, source=m_band, x=HALF - BAND // 2, y=0, operation="add")
m_soft = N("GrowMaskWithBlur", (SX + 260, 560), (220, 250), mask=m, expand=0, incremental_expandrate=0.0, tapered_corners=True,
           flip_input=False, blur_radius=24.0, lerp_alpha=1.0, decay_factor=1.0, fill_holes=False)
grey = N("EmptyImage", (SX, 700), (220, 130), title="Grey band", width=BAND, height=H, batch_size=1, color=0x7F7F7F)

windows = []
WX = SX + 700
for k in range(P):
    y = k * 440
    a_sm, a_img = panels[k]
    b_sm, b_img = panels[(k + 1) % P]
    title = f"2.{k + 1} - Seam panel {k + 1} | panel {(k + 1) % P + 1}" + (" (ring)" if k == P - 1 else "")
    group(title, WX - 40, y - 40, 2000, 420, "#7a5f3f")
    l_lat = N("PanoramaLatentSlice", (WX, y), (220, 106), samples=a_sm[0], x=HALF, width=HALF, downscale=16)
    r_lat = N("PanoramaLatentSlice", (WX, y + 140), (220, 106), samples=b_sm[0], x=0, width=HALF, downscale=16)
    j_lat = N("PanoramaLatentConcat", (WX + 240, y), (220, 60), left=l_lat, right=r_lat)
    jm = N("SetLatentNoiseMask", (WX + 240, y + 90), (200, 50), samples=j_lat, mask=m_soft[0])
    l_px = N("ImageCrop", (WX + 480, y), (200, 130), image=a_img, width=HALF, height=H, x=HALF, y=0)
    r_px = N("ImageCrop", (WX + 480, y + 150), (200, 130), image=b_img, width=HALF, height=H, x=0, y=0)
    joined = N("ImageStitch", (WX + 700, y), (200, 130), title="Original pixels", image1=l_px, image2=r_px, direction="right",
               match_image_size=False, spacing_width=0, spacing_color="white")
    ref_l = N("ImageCrop", (WX + 480, y + 300), (200, 130), image=a_img, width=HALF - BAND // 2, height=H, x=HALF, y=0)
    ref_r = N("ImageCrop", (WX + 700, y + 300), (200, 130), image=b_img, width=HALF - BAND // 2, height=H, x=BAND // 2, y=0)
    ref_a = N("ImageStitch", (WX + 920, y + 150), (200, 130), image1=ref_l, image2=grey, direction="right",
              match_image_size=False, spacing_width=0, spacing_color="white")
    ref = N("ImageStitch", (WX + 920, y + 300), (200, 130), title="Reference: left | grey | right", image1=ref_a, image2=ref_r,
            direction="right", match_image_size=False, spacing_width=0, spacing_color="white")
    ref_lat = N("VAEEncode", (WX + 1140, y + 300), (200, 50), pixels=ref, vae=vae)
    rp = N("ReferenceLatent", (WX + 1140, y), (200, 50), conditioning=seam_pos, latent=ref_lat)
    rn = N("ReferenceLatent", (WX + 1140, y + 70), (200, 50), conditioning=seam_neg, latent=ref_lat)
    seed = N("RandomNoise", (WX + 1140, y + 150), (220, 82), title=f"Seed seam {k + 1}", noise_seed=SEED + 100 + k)
    g = N("CFGGuider", (WX + 1380, y), (220, 106), model=unet, positive=rp, negative=rn, cfg=CFG)
    sm = N("SamplerCustomAdvanced", (WX + 1380, y + 140), (220, 110), noise=seed, guider=g, sampler=sampler, sigmas=sigmas, latent_image=jm)
    raw = N("VAEDecode", (WX + 1620, y), (200, 50), samples=sm[0], vae=vae)
    win = N("PanoramaToneMatch", (WX + 1620, y + 80), (220, 80), title="Tone match to the panels", original=joined, generated=raw,
            mask=m_soft[0], fade_px=BAND // 2)
    # Outside the band keep the original panel pixels: decoded kept parts differ slightly per window, which showed
    # as a hard line where two windows meet (at every panel centre).
    win = N("ImageCompositeMasked", (WX + 1860, y), (220, 150), title="Band only, on the original pixels", destination=joined,
            source=win, x=0, y=0, resize_source=False, mask=m_soft[0])
    N("PreviewImage", (WX + 1620, y + 190), (320, 180), images=win)
    windows.append(win)

# 3 - Strip: the seam windows tile it exactly (each centred on a join); the ring window is split across both ends
RY = max(P * 440, 1200)
strip_w = P * PANEL_W
group(f"3 - Strip {strip_w} x {H}", WX - 40, RY - 40, 1400, 300, "#3f789e")
ring = windows[-1]
strip = N("ImageCrop", (WX, RY), (200, 130), title="Ring window, right half", image=ring, width=HALF, height=H, x=HALF, y=0)
for i, w in enumerate(windows[:-1]):
    strip = N("ImageStitch", (WX + 220 + 40 * i, RY + 10 * i), (200, 130), image1=strip, image2=w, direction="right",
              match_image_size=False, spacing_width=0, spacing_color="white")
tail = N("ImageCrop", (WX, RY + 150), (200, 130), title="Ring window, left half", image=ring, width=HALF, height=H, x=0, y=0)
final = N("ImageStitch", (WX + 900, RY), (220, 130), image1=strip, image2=tail, direction="right", match_image_size=False,
          spacing_width=0, spacing_color="white")
HX = WX

# Output
OY = RY + 340
ROOM_W = args.screens * 1920
N("SaveImage", (HX, OY), (600, 300), title=f"Strip {strip_w} x {H}", images=final, filename_prefix=f"panorama/{PREFIX}_strip")
if PIXEL:
    # A true pixel grid: every "pixel" exactly PIXEL x PIXEL screen pixels, one shared palette, room size exactly
    # N x 1920 x 1080 (each screen gets its slice 1:1, so the pixels stay crisp). Image models only draw fake pixels.
    gw, gh = ROOM_W // PIXEL, 1080 // PIXEL
    group(f"4 - Save: strip + pixel art {ROOM_W} x 1080 ({gw} x {gh} pixels of {PIXEL} px, {args.colors} colours)",
          HX - 40, OY - 40, 1900, 420, "#5f3f7a")
    grid = N("ImageScale", (HX + 640, OY), (260, 130), title=f"Down to the pixel grid {gw} x {gh}", image=final,
             upscale_method="area", width=gw, height=gh, crop="disabled")
    pal = N("ImageQuantize", (HX + 920, OY), (260, 82), title=f"Palette: {args.colors} colours", image=grid, colors=args.colors, dither="none")
    up = N("ImageScaleBy", (HX + 1200, OY), (260, 82), title=f"Nearest x{PIXEL} to {ROOM_W} x 1080", image=pal,
           upscale_method="nearest-exact", scale_by=float(PIXEL))
    N("SaveImage", (HX + 1480, OY), (440, 300), title="Room size (pixel art)", images=up, filename_prefix=f"panorama/{PREFIX}_room")
else:
    group(f"4 - Save: strip + room size ({ROOM_W} wide for {args.screens} x 1920)", HX - 40, OY - 40, 1400, 420, "#5f3f7a")
    up = N("ImageScaleBy", (HX + 640, OY), (260, 82), title=f"Upscale to {ROOM_W} wide", image=final, upscale_method="lanczos",
           scale_by=round(ROOM_W / strip_w, 4))
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

ui = {"id": WORKFLOW_ID, "revision": 0, "last_node_id": len(nodes), "last_link_id": len(ui_links),
      "nodes": ui_nodes, "links": ui_links, "groups": groups, "config": {},
      "extra": {"workflowRendererVersion": "LG", "ds": {"scale": 0.45, "offset": [100, 100]}}, "version": 0.4}
json.dump(ui, open(args.out, "w", encoding="utf-8"), indent=1)
print(f"{len(nodes)} nodes, {len(ui_links)} links, strip {strip_w} x {H}, panels={P}, steps={STEPS}")
