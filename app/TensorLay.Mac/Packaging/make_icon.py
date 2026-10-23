"""Render the macOS app icon (TensorLay.icns) — the two-ring logo on a white
Big Sur-style rounded square. Pillow only, so it runs on the Linux build host.

usage: python3 make_icon.py <out_dir>   -> <out_dir>/TensorLay.icns + icon_1024.png
"""
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

S = 1024
PURPLE = (0x6C, 0x5C, 0xE7, 255)
GREEN = (0x2E, 0xCC, 0x71, 255)


def render() -> Image.Image:
    scale = 4  # supersample for smooth edges
    W = S * scale
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))

    # Apple's grid: 824 px tile centred on a 1024 canvas, corner radius ~185.
    tile = 824 * scale
    off = (W - tile) // 2
    radius = 185 * scale

    shadow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        (off, off + 12 * scale, off + tile, off + tile + 12 * scale), radius, fill=(0, 0, 0, 70))
    shadow = shadow.filter(ImageFilter.GaussianBlur(18 * scale))
    img.alpha_composite(shadow)

    d = ImageDraw.Draw(img)
    d.rounded_rectangle((off, off, off + tile, off + tile), radius, fill=(250, 250, 247, 255))

    # Two interlocking rings, same proportions as app_icon.png.
    ring_r = 205 * scale
    stroke = 62 * scale
    cy = W // 2
    cx1 = W // 2 - 128 * scale
    cx2 = W // 2 + 128 * scale
    # Green over purple, as in app_icon.png.
    for cx, col in ((cx1, PURPLE), (cx2, GREEN)):
        d.ellipse((cx - ring_r, cy - ring_r, cx + ring_r, cy + ring_r), outline=col, width=stroke)

    return img.resize((S, S), Image.LANCZOS)


def main() -> None:
    out = Path(sys.argv[1] if len(sys.argv) > 1 else ".")
    out.mkdir(parents=True, exist_ok=True)
    icon = render()
    icon.save(out / "icon_1024.png")
    icon.save(out / "TensorLay.icns", sizes=[(16, 16), (32, 32), (64, 64), (128, 128),
                                              (256, 256), (512, 512), (1024, 1024)])


if __name__ == "__main__":
    main()
