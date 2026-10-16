"""PanoramaToneMatch - undo the VAE decoder's tone shift after outpainting.

The Flux VAE decoder normalises over the image, so the same latent decodes darker or brighter depending on what is
next to it. After outpainting, the kept context (and the first ~150 px of new content next to it) comes back in a
different tone than the original pixels, which shows as seams when panels are stitched.

This node measures the shift on the kept area (mask < 0.01) as a per-channel gain and applies it fully there,
fading out over `fade_px` columns into the generated area (far-away new content decodes in its own, correct tone).
"""
import torch


class PanoramaToneMatch:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            "original": ("IMAGE", {"tooltip": "Image that went into the sampler (with the kept context)"}),
            "generated": ("IMAGE", {"tooltip": "Decoded sampler output, same size"}),
            "mask": ("MASK", {"tooltip": "Outpaint/inpaint mask: 0 = kept, 1 = generated"}),
            "fade_px": ("INT", {"default": 256, "min": 0, "max": 4096, "tooltip": "Columns over which the correction fades out into the generated area"}),
        }}

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "match"
    CATEGORY = "image/panorama"

    def match(self, original, generated, mask, fade_px):
        m = mask.reshape(-1, mask.shape[-2], mask.shape[-1])[0].to(generated.device)
        if m.shape != generated.shape[1:3]:
            return (generated,)
        kept = m < 0.01
        if kept.sum() < 64:
            return (generated,)

        # The shift grows towards the mask edge, so measure it per column (smoothed over +-32 kept columns), carry the
        # nearest kept column's gain into the generated area and fade it out over fade_px columns.
        w = m.shape[1]
        kept_cols = kept.float().mean(dim=0) > 0.5
        idx = torch.arange(w, device=m.device, dtype=torch.float32)
        kc = idx[kept_cols]
        if kc.numel() == 0:
            return (generated,)
        d = (idx[:, None] - kc[None, :]).abs()
        dist, nearest = d.min(dim=1)
        nearest_col = kc[nearest].long()
        weight = (1.0 - dist / max(fade_px, 1)).clamp(0.0, 1.0) if fade_px > 0 else (dist == 0).float()

        k = kept_cols.float()[None, None, :]
        kernel = torch.ones(1, 1, 65, device=m.device) / 65.0

        def smooth(v):  # mean over kept columns only, within the window
            num = torch.nn.functional.conv1d((v * k), kernel, padding=32)
            den = torch.nn.functional.conv1d(k, kernel, padding=32).clamp(min=1e-6)
            return (num / den)[0, 0]

        out = generated.clone()
        for c in range(3):
            o_col = smooth(original[0, :, :, c].mean(dim=0)[None, None, :].to(m.device))
            g_col = smooth(generated[0, :, :, c].mean(dim=0)[None, None, :].to(m.device))
            gain_col = (o_col / g_col.clamp(min=1e-4)).clamp(0.25, 4.0)
            gain = 1.0 + (gain_col[nearest_col] - 1.0) * weight
            out[..., c] = generated[..., c] * gain[None, None, :]
        return (out.clamp(0.0, 1.0),)


class PanoramaLatentSlice:
    """Columns x .. x+width (in pixels) of a latent. Core LatentCrop assumes 8x latents; Flux.2 uses 16x."""

    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            "samples": ("LATENT",),
            "x": ("INT", {"default": 0, "min": 0, "max": 65536, "step": 16}),
            "width": ("INT", {"default": 336, "min": 16, "max": 65536, "step": 16}),
            "downscale": ("INT", {"default": 16, "min": 1, "max": 64, "tooltip": "Pixels per latent cell (16 for Flux.2)"}),
        }}

    RETURN_TYPES = ("LATENT",)
    FUNCTION = "slice"
    CATEGORY = "latent/panorama"

    def slice(self, samples, x, width, downscale):
        s = samples["samples"]
        return ({"samples": s[..., x // downscale:(x + width) // downscale].clone()},)


class PanoramaLatentPadRight:
    """Append `right` pixels of zero latent (to be filled by a masked sampler)."""

    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            "samples": ("LATENT",),
            "right": ("INT", {"default": 1008, "min": 16, "max": 65536, "step": 16}),
            "downscale": ("INT", {"default": 16, "min": 1, "max": 64}),
        }}

    RETURN_TYPES = ("LATENT",)
    FUNCTION = "pad"
    CATEGORY = "latent/panorama"

    def pad(self, samples, right, downscale):
        s = samples["samples"]
        zeros = torch.zeros(*s.shape[:-1], right // downscale, dtype=s.dtype, device=s.device)
        return ({"samples": torch.cat([s, zeros], dim=-1)},)


class PanoramaLatentConcat:
    """Put latent b to the right of latent a (same height)."""

    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"left": ("LATENT",), "right": ("LATENT",)}}

    RETURN_TYPES = ("LATENT",)
    FUNCTION = "concat"
    CATEGORY = "latent/panorama"

    def concat(self, left, right):
        return ({"samples": torch.cat([left["samples"], right["samples"]], dim=-1)},)


NODE_CLASS_MAPPINGS = {
    "PanoramaToneMatch": PanoramaToneMatch,
    "PanoramaLatentSlice": PanoramaLatentSlice,
    "PanoramaLatentPadRight": PanoramaLatentPadRight,
    "PanoramaLatentConcat": PanoramaLatentConcat,
}
NODE_DISPLAY_NAME_MAPPINGS = {
    "PanoramaToneMatch": "Panorama Tone Match",
    "PanoramaLatentSlice": "Panorama Latent Slice (x, width)",
    "PanoramaLatentPadRight": "Panorama Latent Pad Right",
    "PanoramaLatentConcat": "Panorama Latent Concat (left | right)",
}
