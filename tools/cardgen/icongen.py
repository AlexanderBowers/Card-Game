"""
Critical Count - every icon, from the rendered square mark (logogen.py --square) on the warm cream
radial background.

    python3 logogen.py --out renders/brand --size 1024x1024 --square
    python3 icongen.py renders/brand/mark_1024x1024.png

Writes assets/aimfor20_art/icon_1024.png + store_icon_512.png, and assets/icon/ (store_512,
icon_256, launcher_192, adaptive_background/foreground/monochrome_432).
"""
import math, os, sys
from PIL import Image, ImageDraw

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
ART = os.path.join(ROOT, "assets", "aimfor20_art")
ICON = os.path.join(ROOT, "assets", "icon")
CENTRE, EDGE = (250, 244, 228), (236, 222, 190)   # the cream the old icons used


def cream(px):
    """A soft radial: light in the middle, warmer toward the corners."""
    im = Image.new("RGB", (px, px))
    c = px / 2
    rmax = math.hypot(c, c)
    pix = im.load()
    for y in range(px):
        for x in range(px):
            t = min(1.0, math.hypot(x + 0.5 - c, y + 0.5 - c) / rmax) ** 1.6
            pix[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(CENTRE, EDGE))
    return im.convert("RGBA")


def fit(mark, width):
    """The mark cropped to its pixels and scaled to `width`."""
    m = mark.crop(mark.getchannel("A").getbbox())
    return m.resize((width, round(m.height * width / m.width)), Image.LANCZOS)


def centred(canvas, im, dy=0):
    canvas.alpha_composite(im, ((canvas.width - im.width) // 2, (canvas.height - im.height) // 2 + dy))
    return canvas


def rounded(im, radius_frac):
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, im.width - 1, im.height - 1],
                                           radius=round(im.width * radius_frac), fill=255)
    out = im.copy()
    out.putalpha(mask)
    return out


def main(mark_path):
    mark = Image.open(mark_path).convert("RGBA")
    big = centred(cream(1024), fit(mark, 900))
    big.save(os.path.join(ART, "icon_1024.png"))
    for size, path in ((512, os.path.join(ART, "store_icon_512.png")), (512, os.path.join(ICON, "store_512.png"))):
        big.resize((size, size), Image.LANCZOS).convert("RGB").save(path)
    big.resize((256, 256), Image.LANCZOS).save(os.path.join(ICON, "icon_256.png"))
    rounded(big.resize((192, 192), Image.LANCZOS), 32 / 192).save(os.path.join(ICON, "launcher_192.png"))

    # Adaptive: launchers crop to a circle/squircle, so the mark stays inside the 264 px safe zone.
    cream(432).save(os.path.join(ICON, "adaptive_background_432.png"))
    fg = centred(Image.new("RGBA", (432, 432), (0, 0, 0, 0)), fit(mark, 252))
    fg.save(os.path.join(ICON, "adaptive_foreground_432.png"))
    # Themed (monochrome) icon: the dark disc and outlines are cut out by brightness, so the ring,
    # the 20 and the two cards still read in a single tint instead of one solid blob.
    lum = fg.convert("L").point(lambda v: 255 if v > 150 else 0)
    alpha = Image.composite(fg.getchannel("A"), Image.new("L", fg.size, 0), lum)
    mono = Image.new("RGBA", fg.size, (255, 255, 255, 0))
    mono.putalpha(alpha)
    mono.save(os.path.join(ICON, "adaptive_monochrome_432.png"))
    print("icons written")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "renders", "brand", "mark_1024x1024.png"))
