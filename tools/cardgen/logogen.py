"""
Aim for 20 - the logo: a red + over a blue -, then a blue 2 and a red 0 (Alexander's sketch,
2026-09-30). Built like the cards: extruded, bevelled enamel with a white inline and a deep navy
outline, lit by the same rig, rendered on transparency.

    python3 logogen.py --out renders/brand --size 1600x800
"""
import argparse, math, os, sys
import bpy
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cardgen as cg

BLUE, RED, NAVY, WHITE = "#2f6fe0", "#e23c4a", "#14203d", "#ffffff"


def glyph(body, x, y, size, color, name):
    """One character in three layers: navy outline, white inline, enamel face."""
    layers = [(0.16, 0.00, NAVY, 0.30, 0.0), (0.075, 0.06, WHITE, 0.35, 0.0), (0.0, 0.12, color, 0.28, 0.6)]
    for offset, z, col, rough, coat in layers:
        m = cg.mat(name + col, cg.srgb(col), rough=rough, coat=coat)
        ob = cg.text(name + str(offset), body, size, (x, y, z), m, extrude=0.12, font=cg.FONT_NUM)
        ob.data.offset = offset
        ob.data.bevel_depth = 0.035 if offset == 0 else 0.02


def bar_layered(cx, cy, w, h, color, name):
    for pad, z, col, coat in ((0.16, 0.0, NAVY, 0.0), (0.075, 0.06, WHITE, 0.0), (0.0, 0.12, color, 0.6)):
        m = cg.mat(name + col, cg.srgb(col), rough=0.3, coat=coat)
        cg.bar((cx, cy, z + 0.1), w + 2 * pad, h + 2 * pad, m, depth=0.14)


def sign(plus, cx, cy, length, color, name):
    t = length * 0.3
    bar_layered(cx, cy, length, t, color, name + "H")
    if plus:
        # The vertical stroke's navy and white layers are drawn only above and below the crossbar,
        # so the + reads as one piece rather than two bars.
        arm = (length - t) / 2
        for sy in (1, -1):
            bar_layered(cx, cy + sy * (t / 2 + arm / 2), t, arm + 0.02, color, name + ("U" if sy > 0 else "D"))


def build_logo(w, h):
    scene = cg.reset_scene(64)
    scene.render.resolution_x, scene.render.resolution_y = w, h
    scene.camera.data.ortho_scale = 5.0 * max(1.0, w / h)
    size = 5.2
    sign(True, -3.1, 0.95, 1.35, RED, "Plus")
    sign(False, -3.1, -1.05, 1.35, BLUE, "Minus")
    glyph("2", -0.6, -0.1, size, BLUE, "Two")
    glyph("0", 2.3, -0.1, size, RED, "Zero")


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="renders/brand")
    ap.add_argument("--size", default="1600x800")
    a = ap.parse_args(argv)
    w, h = (int(v) for v in a.size.split("x"))
    build_logo(w, h)
    os.makedirs(a.out, exist_ok=True)
    bpy.context.scene.render.filepath = os.path.join(a.out, f"logo_{w}x{h}.png")
    bpy.ops.render.render(write_still=True)
    print("wrote", bpy.context.scene.render.filepath)
