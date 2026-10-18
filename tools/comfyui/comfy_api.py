"""Minimal ComfyUI HTTP API client for the content scripts (panorama.py, logo_styles.py): build an API-format graph in
Python, queue it, wait, and fetch the first saved image."""
import io
import json
import time
import urllib.parse
import urllib.request
import uuid

from PIL import Image

SERVER = "http://127.0.0.1:8188"


class Graph:
    """g("NodeClass", input=value | (node_id, slot)) adds a node and returns its first output as (node_id, 0)."""

    def __init__(self):
        self.nodes = {}

    def __call__(self, cls, **inputs):
        nid = str(len(self.nodes) + 1)
        self.nodes[nid] = {"class_type": cls, "inputs": {k: list(v) if isinstance(v, tuple) else v for k, v in inputs.items()}}
        return nid, 0


def run(g):
    """Queue the graph, wait for it, return the first image any SaveImage node wrote (RGB)."""
    body = json.dumps({"prompt": g.nodes, "client_id": str(uuid.uuid4())}).encode()
    req = urllib.request.Request(SERVER + "/prompt", body, {"Content-Type": "application/json"})
    pid = json.loads(urllib.request.urlopen(req).read())["prompt_id"]
    while True:
        h = json.loads(urllib.request.urlopen(f"{SERVER}/history/{pid}").read())
        if pid in h:
            st = h[pid].get("status", {})
            if st.get("status_str") == "error":
                raise RuntimeError(json.dumps(st.get("messages"))[:3000])
            if st.get("completed"):
                for o in h[pid]["outputs"].values():
                    for im in o.get("images", []):
                        q = urllib.parse.urlencode({k: im[k] for k in ("filename", "subfolder", "type")})
                        return Image.open(io.BytesIO(urllib.request.urlopen(f"{SERVER}/view?{q}").read())).convert("RGB")
                raise RuntimeError("graph saved no image")
        time.sleep(2)


def upload(img, name, subfolder="scripts"):
    """Put a PIL image into ComfyUI/input/<subfolder>/<name>; returns the LoadImage name."""
    buf = io.BytesIO()
    img.save(buf, "PNG")
    b = uuid.uuid4().hex
    part = lambda k, v: f"--{b}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n".encode()
    body = (f"--{b}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"{name}\"\r\nContent-Type: image/png\r\n\r\n"
            ).encode() + buf.getvalue() + b"\r\n" + part("subfolder", subfolder) + part("overwrite", "true") + f"--{b}--\r\n".encode()
    urllib.request.urlopen(urllib.request.Request(SERVER + "/upload/image", body, {"Content-Type": f"multipart/form-data; boundary={b}"}))
    return f"{subfolder}/{name}"


def klein(g):
    """FLUX.2 Klein 9B base (fp8), its Qwen3 8B text encoder and the Flux.2 VAE."""
    unet = g("UNETLoader", unet_name="flux-2-klein-base-9b-fp8.safetensors", weight_dtype="default")
    clip = g("CLIPLoader", clip_name="qwen_3_8b_fp8mixed.safetensors", type="flux2", device="default")
    vae = g("VAELoader", vae_name="flux2-vae.safetensors")
    return unet, clip, vae


def sample(g, unet, pos, neg, latent, seed, width, height, steps=20, cfg=5.0):
    guider = g("CFGGuider", model=unet, positive=pos, negative=neg, cfg=cfg)
    sig = g("Flux2Scheduler", steps=steps, width=width, height=height)
    return g("SamplerCustomAdvanced", noise=g("RandomNoise", noise_seed=seed), guider=guider,
             sampler=g("KSamplerSelect", sampler_name="euler"), sigmas=sig, latent_image=latent)


def reference(g, vae, pos, neg, image_name, megapixels=0.5):
    """Add a LoadImage as Klein reference image to both conditionings."""
    li = g("LoadImage", image=image_name)
    sc = g("ImageScaleToTotalPixels", image=li, upscale_method="lanczos", megapixels=megapixels, resolution_steps=16)
    lat = g("VAEEncode", pixels=sc, vae=vae)
    return g("ReferenceLatent", conditioning=pos, latent=lat), g("ReferenceLatent", conditioning=neg, latent=lat)
