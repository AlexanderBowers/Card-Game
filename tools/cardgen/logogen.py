"""
Critical Count - the logo (Alexander's direction, 2026-10-03): the real -1 Modifier card on the
left, the real +2 Modifier card on the right, and in the middle the "20 in a ring" emblem from the
Classic deck's card back (teal ring, dark disc, gold 20), large and in front.

Everything is built in ONE Blender scene from cardgen.py's own builders, so the cards and the
emblem share the card light rig and come out exactly like the in-game art. Rendered on
transparency.

    python3 logogen.py --out renders/brand --size 1600x800             # the wide logo
    python3 logogen.py --out renders/brand --size 1024x1024 --square   # the icon mark

Replaces the "+/- 20" mark used while the game was called "Aim for 20".
"""
import argparse, math, os, sys
import bpy
import bmesh
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


def round_slab(name, r_out, r_in, z, thick, material, bevel=0.01, seg=192):
    """A true disc (r_in=0) or ring. cardgen's slab() makes circles from a rounded rect, which
    leaves seams at the quarter joins - invisible at card size, visible this large."""
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    outer = [bm.verts.new((r_out * math.cos(2 * math.pi * i / seg), r_out * math.sin(2 * math.pi * i / seg), 0))
             for i in range(seg)]
    if r_in > 0:
        inner = [bm.verts.new((r_in * math.cos(2 * math.pi * i / seg), r_in * math.sin(2 * math.pi * i / seg), 0))
                 for i in range(seg)]
        for i in range(seg):
            j = (i + 1) % seg
            bm.faces.new((outer[i], outer[j], inner[j], inner[i]))
    else:
        bm.faces.new(outer)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    ob.location.z = z
    ob.data.materials.append(material)
    sol = ob.modifiers.new("Solid", "SOLIDIFY")
    sol.thickness = thick
    sol.offset = 1.0
    if bevel:
        bv = ob.modifiers.new("Bevel", "BEVEL")
        bv.width = bevel
        bv.segments = 4
        bv.limit_method = "ANGLE"
    for poly in ob.data.polygons:
        poly.use_smooth = True
    return ob


def classic_emblem():
    """The Classic back's emblem on its own - same ring, disc and gold 20 as build_back_classic."""
    T = cg.THICK
    teal = cg.mat("Ring", cg.srgb("#5fd6e2"), rough=0.3, coat=0.4)
    r_out, r_in = 1.62, 1.18
    round_slab("Ring", r_out, r_in, T, 0.035, teal, bevel=0.012)
    disc = cg.mat("Disc", cg.srgb("#3a4659"), rough=0.6)
    round_slab("Disc", r_in, 0, T, 0.012, disc, bevel=0.004)
    gold = cg.mat("Twenty", cg.srgb("#ffc86e"), rough=0.3, coat=0.5)
    cg.text("Twenty", "20", 1.5, (0, -0.02, T + 0.012), gold, extrude=0.04)
    # A navy backing disc a touch wider than the ring, so the emblem holds an edge against any
    # background (on the card it sat on the navy face).
    back = cg.mat("EmblemBack", cg.srgb("#2a3d62"), rough=0.5, coat=0.2)
    round_slab("EmblemBack", r_out + 0.12, 0, 0.0, T, back, bevel=0.03)


def build_logo(w, h, square=False):
    scene = cg.reset_scene(96)
    scene.render.resolution_x, scene.render.resolution_y = w, h
    if square:
        scene.camera.data.ortho_scale = 10.4
        placed(lambda: cg.build_modifier(-1), -2.45, -0.1, 0.0, 12, 0.72)
        placed(lambda: cg.build_modifier(2), 2.45, -0.1, 0.0, -12, 0.72)
        placed(classic_emblem, 0.0, -0.1, 0.6, 0, 1.45)
        return
    # Wide: camera height 9 units.
    scene.camera.data.ortho_scale = 9.0 * max(1.0, w / h)
    placed(lambda: cg.build_modifier(-1), -5.2, 0.0, 0.0, 10, 0.92)
    placed(lambda: cg.build_modifier(2), 5.2, 0.0, 0.0, -10, 0.92)
    placed(classic_emblem, 0.0, 0.0, 0.6, 0, 2.1)


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
