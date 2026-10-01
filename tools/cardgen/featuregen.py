"""Compose the Play Store feature graphic (1024x500).

Logo + tagline on the left, two "Modifier math" rows in the middle
(16 [+4] = 20 and 22 [-2] = 20), and the Trade Hands / Veto cards fanned
on the right. Run from tools/cardgen:  python featuregen.py
"""
import math, os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
R = os.path.join(HERE, "renders")
OUT = os.path.join(HERE, "brand_out", "feature_graphic_1024x500.png")
W, H = 1024, 500
SS = 2  # supersample

FRED = os.path.join(HERE, "fonts", "Fredoka-Bold.ttf")
NAVY = (20, 32, 61)
BLUE = (47, 111, 224)
RED = (226, 60, 74)
GREEN = (46, 170, 92)
CREAM = (255, 248, 232)
TAGLINE = "Draw, Play, Hold!"


def S(v):
    return int(round(v * SS))


def font(px):
    return ImageFont.truetype(FRED, S(px))


def cover(im, w, h):
    r = max(w / im.width, h / im.height)
    im = im.resize((math.ceil(im.width * r), math.ceil(im.height * r)), Image.LANCZOS)
    x, y = (im.width - w) // 2, (im.height - h) // 2
    return im.crop((x, y, x + w, y + h))


def shadow_paste(base, im, x, y, angle=0.0, blur=10, offset=(6, 10), alpha=150):
    if angle:
        im = im.rotate(angle, resample=Image.BICUBIC, expand=True)
    sh = Image.new("RGBA", im.size, (0, 0, 0, 0))
    a = im.getchannel("A").point(lambda v: v * alpha // 255)
    sh.putalpha(a)
    pad = S(blur) * 3
    shp = Image.new("RGBA", (im.width + pad * 2, im.height + pad * 2), (0, 0, 0, 0))
    shp.paste(sh, (pad, pad))
    shp = shp.filter(ImageFilter.GaussianBlur(S(blur)))
    cx, cy = x - im.width // 2, y - im.height // 2
    base.alpha_composite(shp, (cx - pad + S(offset[0]), cy - pad + S(offset[1])))
    base.alpha_composite(im, (cx, cy))


def card(path, height):
    im = Image.open(os.path.join(R, "cards", path)).convert("RGBA")
    h = S(height)
    return im.resize((round(im.width * h / im.height), h), Image.LANCZOS)


def outlined_text(d, xy, text, px, fill, anchor="mm", outline=NAVY, ow=5, inline=True):
    f = font(px)
    d.text(xy, text, font=f, fill=outline, anchor=anchor, stroke_width=S(ow), stroke_fill=outline)
    if inline:
        d.text(xy, text, font=f, fill=(255, 255, 255), anchor=anchor,
               stroke_width=S(ow * 0.45), stroke_fill=(255, 255, 255))
    d.text(xy, text, font=f, fill=fill, anchor=anchor)


def padlock(d, cx, baseline, px, fill, outline=NAVY, ow=5):
    """A closed padlock the size of a lower-case "o", sitting on the text baseline - the "o" in
    "Hold" (the padlock is what Hold puts on your score in the game)."""
    bw, bh = px * 0.6, px * 0.48           # body
    left, right = cx - S(bw / 2), cx + S(bw / 2)
    bottom, top = baseline, baseline - S(bh)
    sw = px * 0.1                          # shackle thickness
    sr = bw * 0.36                         # shackle radius
    arc_box = [cx - S(sr), top - S(sr * 2.15), cx + S(sr), top + S(sr * 0.5)]
    for col, grow in ((outline, ow), (fill, 0)):
        g = S(grow)
        d.arc([arc_box[0] - g, arc_box[1] - g, arc_box[2] + g, arc_box[3] + g], 180, 360,
              fill=col, width=S(sw) + 2 * g)
        for x in (arc_box[0], arc_box[2] - S(sw)):
            d.rectangle([x - g, top - S(sr * 1.05), x + S(sw) + g, top + g], fill=col)
        d.rounded_rectangle([left - g, top - g, right + g, bottom + g], radius=S(px * 0.1) + g, fill=col)
    # keyhole
    kh = S(px * 0.07)
    ky = (top + bottom) / 2 - S(px * 0.03)
    d.ellipse([cx - kh, ky - kh, cx + kh, ky + kh], fill=outline)
    d.rectangle([cx - kh * 0.45, ky, cx + kh * 0.45, ky + kh * 2.2], fill=outline)


def tagline(d, cx, cy, px, fill):
    """TAGLINE with the "o" of Hold drawn as a padlock."""
    before, after = TAGLINE.split("Hold")
    before += "H"
    after = "ld" + after
    f = font(px)
    lock_w = f.getlength("o")
    total = f.getlength(before) + lock_w + f.getlength(after)
    x = cx - total / 2
    baseline = cy + S(px * 0.36)
    for part in (before, None, after):
        if part is None:
            padlock(d, x + lock_w / 2, baseline, px, fill)
            x += lock_w
            continue
        outlined_text(d, (x, baseline), part, px, fill, anchor="ls", ow=5, inline=False)
        x += f.getlength(part)


def badge(base, cx, cy, value, colour, r=40, glow=None):
    """A round score chip like the in-game score boxes."""
    layer = Image.new("RGBA", base.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    if glow:
        g = Image.new("RGBA", base.size, (0, 0, 0, 0))
        ImageDraw.Draw(g).ellipse([cx - S(r + 16), cy - S(r + 16), cx + S(r + 16), cy + S(r + 16)],
                                  fill=glow + (170,))
        base.alpha_composite(g.filter(ImageFilter.GaussianBlur(S(12))))
    sh = Image.new("RGBA", base.size, (0, 0, 0, 0))
    ImageDraw.Draw(sh).ellipse([cx - S(r), cy - S(r) + S(6), cx + S(r), cy + S(r) + S(6)], fill=(0, 0, 0, 120))
    base.alpha_composite(sh.filter(ImageFilter.GaussianBlur(S(5))))
    d.ellipse([cx - S(r), cy - S(r), cx + S(r), cy + S(r)], fill=NAVY)
    d.ellipse([cx - S(r - 4), cy - S(r - 4), cx + S(r - 4), cy + S(r - 4)], fill=(255, 255, 255))
    d.ellipse([cx - S(r - 8), cy - S(r - 8), cx + S(r - 8), cy + S(r - 8)], fill=colour)
    # soft top highlight
    hl = Image.new("RGBA", base.size, (0, 0, 0, 0))
    ImageDraw.Draw(hl).ellipse([cx - S(r - 16), cy - S(r - 11), cx + S(r - 16), cy - S(4)],
                               fill=(255, 255, 255, 60))
    layer.alpha_composite(hl.filter(ImageFilter.GaussianBlur(S(3))))
    d = ImageDraw.Draw(layer)
    d.text((cx, cy + S(1)), str(value), font=font(r * 0.95), fill=(255, 255, 255), anchor="mm",
           stroke_width=S(3), stroke_fill=NAVY)
    base.alpha_composite(layer)


def equation(base, y, start, mod_card, result, start_colour, x0):
    d = ImageDraw.Draw(base)
    bx = x0 + S(40)
    badge(base, bx, y, start, start_colour)
    shadow_paste(base, card(mod_card, 140), bx + S(40) + S(62), y, angle=-4, blur=6, offset=(4, 6))
    eq_x = bx + S(40) + S(124) + S(24)
    outlined_text(d, (eq_x, y), "=", 40, CREAM, ow=4, inline=False)
    badge(base, eq_x + S(24) + S(40), y, result, GREEN, glow=(120, 255, 150))


def main():
    base = Image.new("RGBA", (S(W), S(H)))
    mat = Image.open(os.path.join(R, "playmats", "playmat_bronze_landscape.png")).convert("RGBA")
    base.alpha_composite(cover(mat, S(W), S(H)))

    # darken the left third so the logo and tagline read cleanly
    vg = Image.new("L", (S(W), S(H)), 0)
    ImageDraw.Draw(vg).ellipse([S(-260), S(-120), S(560), S(620)], fill=150)
    vg = vg.filter(ImageFilter.GaussianBlur(S(80)))
    dark = Image.new("RGBA", (S(W), S(H)), (6, 16, 12, 255))
    dark.putalpha(vg)
    base.alpha_composite(dark)

    # logo + tagline
    logo = Image.open(os.path.join(R, "brand", "logo_1600x800.png")).convert("RGBA")
    lw = S(320)
    logo = logo.resize((lw, lw // 2), Image.LANCZOS)
    shadow_paste(base, logo, S(170), S(150), blur=8, offset=(4, 8), alpha=110)
    d = ImageDraw.Draw(base)
    tagline(d, S(170), S(300), 40, CREAM)

    # Modifier maths, stacked in the middle column
    equation(base, S(150), 16, "mods/plus_4.png", 20, BLUE, S(335))
    equation(base, S(350), 22, "mods/minus_2.png", 20, RED, S(335))

    # the new effect cards fanned on the right
    shadow_paste(base, card("effect_tradehands.png", 262), S(798), S(228), angle=7)
    shadow_paste(base, card("effect_veto.png", 262), S(904), S(290), angle=-6)

    out = base.resize((W, H), Image.LANCZOS).convert("RGB")
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    out.save(OUT, optimize=True)
    print("wrote", OUT)


if __name__ == "__main__":
    main()
