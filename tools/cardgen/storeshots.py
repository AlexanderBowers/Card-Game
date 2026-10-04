"""Make phone screenshots fit Google Play's 9:16 / 16:9 rule without cutting anything off.

Play's store listing takes phone screenshots only at exactly 16:9 or 9:16 (2026-10-04: the
console rejects other ratios). The S25's 19.5:9 screen is taller than that, so instead of
cropping off the score or the hand, this pads with the game's room colour (the near-white
behind the board), which looks like part of the game.

    python storeshots.py <folder of screenshots> [out folder]

Writes PNGs to <folder>/play (or the out folder).
"""
import os
import sys
from PIL import Image

ROOM = (238, 241, 246)  # TableWorld3D.RoomColor


def fit(im):
    """Pad to exactly 9:16 (or 16:9). Play's phone screenshots must be one of those two ratios;
    the S25's 19.5:9 is taller, so the short side is padded with the room colour and the long
    side is rounded up to a multiple of 16 so the ratio comes out exact."""
    w, h = im.size
    portrait = h >= w
    long_side, short_side = (h, w) if portrait else (w, h)
    k = max(-(-long_side // 16), -(-short_side // 9))  # smallest k with 16k >= long, 9k >= short
    L, S = 16 * k, 9 * k
    out = Image.new("RGB", (S, L) if portrait else (L, S), ROOM)
    out.paste(im.convert("RGB"), ((out.width - w) // 2, (out.height - h) // 2))
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
