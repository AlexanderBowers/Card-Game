"""
Critical Count - the logo (Alexander's direction, 2026-10-03): the Classic deck's card back - the
"20" in a ring - upright in the middle and in front, with the real -1 Modifier card fanned out
on the left and the real +2 on the right.

Everything is built in ONE Blender scene from cardgen.py's own builders (build_back_classic,
build_modifier), so the three cards share the card light rig and match the in-game art exactly.
Rendered on transparency.

    python3 logogen.py --out renders/brand --size 1600x800             # the wide logo
    python3 logogen.py --out renders/brand --size 1024x1024 --square   # the icon mark

Replaces the "+/- 20" mark used while the game was called "Aim for 20".
"""
import argparse, math, os, sys
import bpy
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cardgen as cg


def placed(build, x, y, z, rot_deg, scale):
    """Run a cardgen builder (which builds at the origin) and move everything it made as one."""
    before = set(bpy.data.objects)
    build()
    new = [o for o in bpy.data.objects if o not in before and o.type not in ("CAMERA", "LIGHT")]
    root = bpy.data.objects.new("Group", None)
    bpy.context.scene.collection.objects.link(root)
    for o in new:
        o.parent = root
    root.location = (x, y, z)
    root.rotation_euler.z = math.radians(rot_deg)
    root.scale = (scale, scale, scale)
    return root


def build_logo(w, h, square=False):
    scene = cg.reset_scene(96)
    scene.render.resolution_x, scene.render.resolution_y = w, h
    # The middle card is the Classic deck's back (the "20" in a ring), upright and in front; the
    # -1 and +2 Modifiers fan out behind it.
    if square:
        scene.camera.data.ortho_scale = 11.2
        placed(lambda: cg.build_modifier(-1), -2.8, -0.2, 0.0, 13, 0.7)
        placed(lambda: cg.build_modifier(2), 2.8, -0.2, 0.0, -13, 0.7)
        placed(cg.build_back_classic, 0.0, 0.1, 0.5, 0, 0.86)
        return
    # Wide: camera height 9 units.
    scene.camera.data.ortho_scale = 9.0 * max(1.0, w / h)
    placed(lambda: cg.build_modifier(-1), -4.7, -0.1, 0.0, 12, 0.92)
    placed(lambda: cg.build_modifier(2), 4.7, -0.1, 0.0, -12, 0.92)
    placed(cg.build_back_classic, 0.0, 0.0, 0.5, 0, 1.0)


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="renders/brand")
    ap.add_argument("--size", default="1600x800")
    ap.add_argument("--square", action="store_true")
    a = ap.parse_args(argv)
    w, h = (int(v) for v in a.size.split("x"))
    build_logo(w, h, a.square)
    os.makedirs(a.out, exist_ok=True)
    name = f"{'mark' if a.square else 'logo'}_{w}x{h}.png"
    bpy.context.scene.render.filepath = os.path.abspath(os.path.join(a.out, name))
    bpy.ops.render.render(write_still=True)
    print("wrote", bpy.context.scene.render.filepath)
