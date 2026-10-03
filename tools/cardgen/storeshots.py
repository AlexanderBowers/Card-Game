"""Make phone screenshots fit Google Play's 2:1 limit without cutting anything off.

Play rejects a screenshot whose long side is more than twice its short side. The S25's
19.5:9 screen is taller than that, so instead of cropping off the score or the hand, this
pads the short side with the game's room colour (the near-white behind the board), which
looks like part of the game.

    python storeshots.py <folder of screenshots> [out folder]

Writes PNGs to <folder>/play (or the out folder). Shots that already fit are copied as-is.
"""
import os
import sys
from PIL import Image

ROOM = (238, 241, 246)  # TableWorld3D.RoomColor
MAX_RATIO = 2.0


def fit(im):
    w, h = im.size
    long_side, short_side = max(w, h), min(w, h)
    if long_side <= short_side * MAX_RATIO:
        return im
    need = -(-long_side // 2)  # ceil(long / 2): the short side that makes exactly 2:1
    if h > w:
        out = Image.new("RGB", (need, h), ROOM)
        out.paste(im, ((need - w) // 2, 0))
    else:
        out = Image.new("RGB", (w, need), ROOM)
        out.paste(im, (0, (need - h) // 2))
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else os.path.join(src, "play")
    os.makedirs(dst, exist_ok=True)
    for name in sorted(os.listdir(src)):
        if not name.lower().endswith((".png", ".jpg", ".jpeg", ".webp")):
            continue
        im = Image.open(os.path.join(src, name)).convert("RGB")
        out = fit(im)
        path = os.path.join(dst, os.path.splitext(name)[0] + ".png")
        out.save(path, optimize=True)
        print(f"{name}: {im.size[0]}x{im.size[1]} -> {out.size[0]}x{out.size[1]}")


if __name__ == "__main__":
    main()
