#!/usr/bin/env python3
"""Generates every icon "Aim for 20" ships, from one description.

    python3 tools/make_icon.py

Writes into assets/icon/. Kept in the repo rather than the PNGs being hand-drawn once and never
explained: the icon is now a thing that can be re-rendered when the felt colour or the name
changes, instead of a binary nobody dares touch.

The mark: two fanned cards on the table's own felt green, the front one showing 20. It has to
survive a 48px launcher grid, so it is ONE readable idea - the fan says "card game", the number
says which one - with no detail that dies small.

Needs Pillow and any bold geometric sans; Poppins if it is installed, DejaVu otherwise.
"""
from PIL import Image, ImageDraw, ImageFont
import os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets", "icon")
SS = 4  # supersample, then downsample: cheap antialiasing on the rounded corners

FONTS = [
    "/usr/share/fonts/truetype/google-fonts/Poppins-Bold.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
]

# The table's own felt (project.godot: default_clear_color 0.07, 0.24, 0.13), so the icon and the
# game are visibly the same object.
FELT       = (15, 61, 33)
FELT_DEEP  = (8, 36, 19)
CARD_FRONT = (246, 241, 226)
CARD_BACK  = (203, 193, 166)
INK        = (11, 46, 25)


def font(size):
    for path in FONTS:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    raise SystemExit("No bold sans found - install Poppins or DejaVu.")


def card(w, h, fill, text=None):
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, w - 1, h - 1], radius=int(h * 0.085), fill=fill,
                        outline=(0, 0, 0, 55), width=max(1, int(h * 0.014)))
    if text:
        f = font(int(h * 0.54))
        bb = d.textbbox((0, 0), text, font=f)
        d.text(((w - (bb[2] - bb[0])) / 2 - bb[0], (h - (bb[3] - bb[1])) / 2 - bb[1]),
               text, font=f, fill=INK)
    return im


def cards(px, scale=1.0):
    """The fan, on transparency. The back card is rotated hard and pushed well clear: at 48px a
    timid fan reads as one card with a smudge, which is worse than no fan at all."""
    layer = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    cw, ch = int(px * 0.375 * scale), int(px * 0.515 * scale)

    back = card(cw, ch, CARD_BACK).rotate(21, expand=True, resample=Image.BICUBIC)
    front = card(cw, ch, CARD_FRONT, "20")

    cx, cy = px // 2, px // 2
    layer.alpha_composite(back, (int(cx - back.width / 2 + px * 0.086),
                                 int(cy - back.height / 2 - px * 0.034)))
    layer.alpha_composite(front, (int(cx - cw / 2 - px * 0.046), int(cy - ch / 2 + px * 0.008)))
    return layer


def felt(px, radius_frac):
    bg = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    ImageDraw.Draw(bg).rounded_rectangle([0, 0, px - 1, px - 1],
                                         radius=int(px * radius_frac), fill=FELT)
    vignette = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    dv = ImageDraw.Draw(vignette)
    for i in range(18):
        dv.ellipse([-px * 0.45 + i * px * 0.030, px * 0.10 + i * px * 0.035,
                    px * 1.45 - i * px * 0.030, px * 1.95 - i * px * 0.035],
                   fill=FELT_DEEP + (14,))
    bg.alpha_composite(Image.composite(vignette, Image.new("RGBA", (px, px), (0, 0, 0, 0)),
                                       bg.split()[3]))
    return bg


def compose(px, radius_frac=0.22, with_bg=True, scale=1.0, opaque=False):
    s = px * SS
    base = felt(s, radius_frac) if with_bg else Image.new("RGBA", (s, s), (0, 0, 0, 0))
    base.alpha_composite(cards(s, scale))
    out = base.resize((px, px), Image.LANCZOS)
    if opaque:
        flat = Image.new("RGB", (px, px), FELT)
        flat.paste(out, (0, 0), out)
        return flat
    return out


def monochrome(px, scale=1.0):
    """Android tints this layer one flat theme colour, so ALPHA is the only thing carrying
    meaning. Two cards would tint into a single irregular blob; one card with the number punched
    THROUGH it is the silhouette that still reads."""
    s = px * SS
    cw, ch = int(s * 0.375 * scale), int(s * 0.515 * scale)

    plain = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
    ImageDraw.Draw(plain).rounded_rectangle([0, 0, cw - 1, ch - 1], radius=int(ch * 0.085),
                                            fill=(255, 255, 255, 255))

    solid = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    fx, fy = int(s // 2 - cw / 2), int(s // 2 - ch / 2)
    solid.alpha_composite(plain, (fx, fy))

    hole = Image.new("L", (s, s), 0)
    dh = ImageDraw.Draw(hole)
    f = font(int(ch * 0.54))
    bb = dh.textbbox((0, 0), "20", font=f)
    dh.text((fx + (cw - (bb[2] - bb[0])) / 2 - bb[0], fy + (ch - (bb[3] - bb[1])) / 2 - bb[1]),
            "20", font=f, fill=255)

    alpha = Image.composite(Image.new("L", (s, s), 0), solid.split()[3], hole)
    out = Image.new("RGBA", (s, s), (255, 255, 255, 255))
    out.putalpha(alpha)
    return out.resize((px, px), Image.LANCZOS)


def main():
    os.makedirs(OUT, exist_ok=True)

    # In-engine / editor icon (project.godot config/icon).
    compose(256, 0.22).save(os.path.join(OUT, "icon_256.png"))

    # Android launcher icons (export_presets.cfg).
    compose(192, 0.22).save(os.path.join(OUT, "launcher_192.png"))
    compose(432, with_bg=False, scale=0.86).save(os.path.join(OUT, "adaptive_foreground_432.png"))
    Image.new("RGBA", (432, 432), FELT + (255,)).save(os.path.join(OUT, "adaptive_background_432.png"))
    monochrome(432, scale=0.86).save(os.path.join(OUT, "adaptive_monochrome_432.png"))

    # The Play / App Store listing icon: FULL BLEED and opaque. Both stores apply their own mask,
    # and an icon that arrives pre-rounded gets its corners clipped twice.
    compose(512, radius_frac=0.0, opaque=True).save(os.path.join(OUT, "store_512.png"))

    for name in sorted(os.listdir(OUT)):
        print("  ", name)


if __name__ == "__main__":
    main()
