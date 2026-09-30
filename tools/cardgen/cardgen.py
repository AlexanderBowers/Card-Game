"""
Aim for 20 - card renderer (Blender, headless via the bpy module).

Builds each card as a small 3D object - a bevelled rounded slab, a raised metal rim, a recessed
inner panel, embossed numbers, domed enamel pips, bar-built signs - lights it with ONE fixed rig
and renders it orthographically to a 560x760 transparent PNG. Every card in the game comes out of
this one file, so they all share the same light, materials and proportions.

    python3 cardgen.py --only main_7_bronze        # one card
    python3 cardgen.py --set all --out ../renders   # everything

The card list and the file names are at the bottom (CARDS). ART_PIPELINE.md explains where the
PNGs go in the game.
"""
import argparse
import math
import os
import sys

import bpy
import bmesh
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
FONT_NUM = os.path.join(HERE, "fonts", "Fredoka-Bold.ttf")

# Card: 560x760 px canvas. 1 Blender unit = 100 px, so the canvas is 5.6 x 7.6 units.
CANVAS_W, CANVAS_H = 5.6, 7.6
CARD_W, CARD_H = 5.44, 7.44          # a hair inside the canvas, so the bevel never clips
CORNER = 0.40                        # corner radius (~7% of the width)
THICK = 0.10


# ----------------------------------------------------------------------------------------------
# Colour
# ----------------------------------------------------------------------------------------------
def srgb(h):
    """'#rrggbb' -> linear RGBA, which is what Blender materials want."""
    h = h.lstrip("#")
    c = [int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4)]
    lin = [x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4 for x in c]
    return (*lin, 1.0)


# Per-look palette: face, inner panel, rim metal, ink (numbers/pips).
LOOKS = {
    # Main deck, one look per rank.
    "bronze":   dict(face="#f4efe4", panel="#e7f1e6", rim="#b0703a", ink="#1e6b45"),
    "silver":   dict(face="#eef2f5", panel="#e3edf3", rim="#a9b6c2", ink="#1d5a6b"),
    "gold":     dict(face="#fbf5e3", panel="#f7ecc9", rim="#d9a62e", ink="#6b4a0e"),
    "ruby":     dict(face="#fbeff0", panel="#f6dfe2", rim="#b3263a", ink="#7a1426"),
    "obsidian": dict(face="#f0edf6", panel="#e4def0", rim="#3d2f5c", ink="#3a2466"),
    # Endless: its own set - a deep night frame with an iridescent (thin-film) trim.
    "endless":  dict(face="#f4f2ff", panel="#e8e4ff", rim="#2a1670", ink="#4b2bb8", iridescent=True),
    # Modifiers.
    "plus":     dict(face="#eef5ff", panel="#dbe9ff", rim="#3f7fe0", ink="#1f4fa8"),
    "minus":    dict(face="#fff0f0", panel="#ffdede", rim="#d8474f", ink="#a51f2c"),
    "flip":     dict(face="#f4f0ff", panel="#e6ddff", rim="#7a57d1", ink="#4b2f96"),
    "rescue":   dict(face="#ecfbf6", panel="#d3f5ea", rim="#1fae8a", ink="#0d6b55"),
    "effect":   dict(face="#fff8e6", panel="#fdecc0", rim="#d9a62e", ink="#6b4a0e"),
    "back":     dict(face="#20304a", panel="#2b4166", rim="#d9a62e", ink="#f2c75c"),
}
PLUS_INK, MINUS_INK = "#1f4fa8", "#a51f2c"


# ----------------------------------------------------------------------------------------------
# Scene
# ----------------------------------------------------------------------------------------------
def reset_scene(samples):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    try:
        scene.cycles.denoiser = "OPENIMAGEDENOISE"
    except Exception:
        pass
    scene.render.resolution_x = 560
    scene.render.resolution_y = 760
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "8"
    # Standard, not AgX: card faces are flat graphic colours that have to come out as picked.
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.exposure = -0.2

    # Camera: straight down, orthographic, exactly the canvas.
    cam_data = bpy.data.cameras.new("Cam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = CANVAS_H
    cam = bpy.data.objects.new("Cam", cam_data)
    cam.location = (0, 0, 10)
    scene.collection.objects.link(cam)
    scene.camera = cam

    # World: a soft grey dome for reflections on the metal and the gloss.
    world = bpy.data.worlds.new("World")
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.55, 0.57, 0.62, 1)
    bg.inputs[1].default_value = 0.35
    scene.world = world

    # THE light rig. Never changed per card.
    def area(name, loc, rot, size, energy, color=(1, 1, 1)):
        d = bpy.data.lights.new(name, "AREA")
        d.size = size
        d.energy = energy
        d.color = color
        o = bpy.data.objects.new(name, d)
        o.location = loc
        o.rotation_euler = rot
        scene.collection.objects.link(o)

    area("Key", (-4.5, 5.0, 7.0), (math.radians(-38), math.radians(-30), 0), 7.0, 1400, (1.0, 0.97, 0.92))
    area("Fill", (6.0, -2.0, 6.0), (math.radians(15), math.radians(42), 0), 8.0, 380, (0.9, 0.95, 1.0))
    area("Rim", (0.0, 7.5, 3.0), (math.radians(-68), 0, 0), 6.0, 300)
    return scene


def mat(name, color, rough=0.45, metal=0.0, coat=0.0, emit=None):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    p = m.node_tree.nodes["Principled BSDF"]
    p.inputs["Base Color"].default_value = color
    p.inputs["Roughness"].default_value = rough
    p.inputs["Metallic"].default_value = metal
    if coat:
        p.inputs["Coat Weight"].default_value = coat
        p.inputs["Coat Roughness"].default_value = 0.08
    return m


def rgba_sock(sockets, name):
    """ShaderNodeMix has float, vector and colour sockets that share one name: pick the colour one."""
    for sock in sockets:
        if sock.name == name and sock.type == "RGBA":
            return sock
    return sockets[name]


def face_mat(name, color):
    """The card face: a gentle top-left-to-bottom-right tone, and a fine paper grain."""
    m = mat(name, color, rough=0.5)
    nt = m.node_tree
    p = nt.nodes["Principled BSDF"]
    tex = nt.nodes.new("ShaderNodeTexCoord")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(tex.outputs["Generated"], sep.inputs[0])
    mix = nt.nodes.new("ShaderNodeMath")
    mix.operation = "SUBTRACT"
    nt.links.new(sep.outputs["Y"], mix.inputs[0])
    nt.links.new(sep.outputs["X"], mix.inputs[1])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    lo = [c * 0.90 for c in color[:3]] + [1]
    ramp.color_ramp.elements[0].color = lo
    ramp.color_ramp.elements[0].position = 0.0
    ramp.color_ramp.elements[1].color = color
    ramp.color_ramp.elements[1].position = 0.75
    rng = nt.nodes.new("ShaderNodeMapRange")
    rng.inputs["From Min"].default_value = -1.0
    rng.inputs["From Max"].default_value = 1.0
    nt.links.new(mix.outputs[0], rng.inputs["Value"])
    nt.links.new(rng.outputs[0], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], p.inputs["Base Color"])
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 220.0
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.035
    nt.links.new(noise.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], p.inputs["Normal"])
    return m


# ----------------------------------------------------------------------------------------------
# Geometry helpers
# ----------------------------------------------------------------------------------------------
def rounded_rect_points(w, h, r, seg=10):
    pts = []
    corners = [(w / 2 - r, h / 2 - r, 0), (-w / 2 + r, h / 2 - r, 90),
               (-w / 2 + r, -h / 2 + r, 180), (w / 2 - r, -h / 2 + r, 270)]
    for cx, cy, a0 in corners:
        for i in range(seg + 1):
            a = math.radians(a0 + 90 * i / seg)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


def slab(name, w, h, r, z, thick, material, bevel=0.02, hole=None):
    """A rounded-rectangle slab, optionally with a rounded hole (a ring)."""
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    outer = [bm.verts.new((x, y, 0)) for x, y in rounded_rect_points(w, h, r)]
    if hole:
        hw, hh, hr = hole
        inner = [bm.verts.new((x, y, 0)) for x, y in rounded_rect_points(hw, hh, hr)]
        n = len(outer)
        for i in range(n):
            j = (i + 1) % n
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


def text(name, body, size, loc, material, rot=0.0, align="CENTER", extrude=0.025, font=FONT_NUM):
    cu = bpy.data.curves.new(name, "FONT")
    cu.body = body
    cu.font = bpy.data.fonts.load(font, check_existing=True)
    cu.size = size
    cu.align_x = align
    cu.align_y = "CENTER"
    cu.extrude = extrude
    cu.bevel_depth = 0.006
    cu.bevel_resolution = 2
    ob = bpy.data.objects.new(name, cu)
    bpy.context.scene.collection.objects.link(ob)
    ob.location = loc
    ob.rotation_euler.z = rot
    ob.data.materials.append(material)
    return ob


def pip(loc, radius, material):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=radius, location=loc, segments=32, ring_count=16)
    ob = bpy.context.active_object
    ob.scale.z = 0.38
    bpy.ops.object.shade_smooth()
    ob.data.materials.append(material)
    return ob


def bar(loc, w, h, material, depth=0.06):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    ob = bpy.context.active_object
    ob.scale = (w, h, depth)
    bv = ob.modifiers.new("Bevel", "BEVEL")
    bv.width = min(w, h) * 0.45
    bv.segments = 6
    bpy.ops.object.shade_smooth()
    ob.data.materials.append(material)
    return ob


def pip_layout(n, cx, cy, area_w, area_h, radius):
    """Two columns like a playing card: ceil(n/2) rows, a lone centred pip on an odd last row."""
    rows = math.ceil(n / 2)
    pitch_y = min(area_h / max(rows, 1), radius * 3.0)
    col_dx = min(area_w / 2, radius * 3.0) / 2 + radius * 0.6
    top = cy + pitch_y * (rows - 1) / 2
    out = []
    left = n
    for r in range(rows):
        y = top - r * pitch_y
        if left >= 2:
            out += [(cx - col_dx, y), (cx + col_dx, y)]
            left -= 2
        else:
            out.append((cx, y))
            left -= 1
    return out


# ----------------------------------------------------------------------------------------------
# Card builders
# ----------------------------------------------------------------------------------------------
FRAME = 0.34   # width of the coloured frame round the face
TRIM = "#f3d27a"


def frame_mat(name, color):
    """The coloured frame: satin enamel, lighter at the top-left like the light is there."""
    m = face_mat(name, color)
    p = m.node_tree.nodes["Principled BSDF"]
    p.inputs["Roughness"].default_value = 0.32
    p.inputs["Coat Weight"].default_value = 0.5
    p.inputs["Coat Roughness"].default_value = 0.12
    return m


def pattern_face(name, color, tint):
    """The inner face: bright, with a faint guilloche-like wave watermark in the look's colour."""
    m = face_mat(name, color)
    nt = m.node_tree
    p = nt.nodes["Principled BSDF"]
    ramp_out = p.inputs["Base Color"].links[0].from_socket
    wave = nt.nodes.new("ShaderNodeTexWave")
    # Fine concentric guilloche lines round the centre: thin, faint, and only visible up close.
    wave.wave_type = "RINGS"
    wave.rings_direction = "SPHERICAL"
    wave.inputs["Scale"].default_value = 9.0
    wave.inputs["Distortion"].default_value = 0.8
    wave.inputs["Detail"].default_value = 0.0
    thr = nt.nodes.new("ShaderNodeMath")
    thr.operation = "GREATER_THAN"
    thr.inputs[1].default_value = 0.90
    nt.links.new(wave.outputs["Fac"], thr.inputs[0])
    mix = nt.nodes.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    mix.inputs["Factor"].default_value = 0.0
    fac = nt.nodes.new("ShaderNodeMath")
    fac.operation = "MULTIPLY"
    fac.inputs[1].default_value = 0.07
    nt.links.new(thr.outputs[0], fac.inputs[0])
    nt.links.new(fac.outputs[0], mix.inputs["Factor"])
    nt.links.new(ramp_out, rgba_sock(mix.inputs, "A"))
    rgba_sock(mix.inputs, "B").default_value = tint
    nt.links.new(rgba_sock(mix.outputs, "Result"), p.inputs["Base Color"])
    return m


def iridescent(m, thickness=520.0):
    """Holographic metal: a pastel rainbow running diagonally across the piece. (Thin-film alone
    reads as plain silver under a straight-down orthographic camera - the angle never changes.)"""
    nt = m.node_tree
    p = nt.nodes["Principled BSDF"]
    tex = nt.nodes.new("ShaderNodeTexCoord")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(tex.outputs["Object"], sep.inputs[0])
    add = nt.nodes.new("ShaderNodeMath")
    add.operation = "ADD"
    nt.links.new(sep.outputs["X"], add.inputs[0])
    nt.links.new(sep.outputs["Y"], add.inputs[1])
    wrap = nt.nodes.new("ShaderNodeMath")
    wrap.operation = "FRACT"
    scale = nt.nodes.new("ShaderNodeMath")
    scale.operation = "MULTIPLY"
    scale.inputs[1].default_value = 0.16
    nt.links.new(add.outputs[0], scale.inputs[0])
    nt.links.new(scale.outputs[0], wrap.inputs[0])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    stops = ["#f2a7ff", "#9fd8ff", "#a8ffd8", "#fff3a0", "#ffb3c7", "#f2a7ff"]
    ramp.color_ramp.elements[0].color = srgb(stops[0])
    ramp.color_ramp.elements[1].color = srgb(stops[-1])
    for i, c in enumerate(stops[1:-1], start=1):
        e = ramp.color_ramp.elements.new(i / (len(stops) - 1))
        e.color = srgb(c)
    nt.links.new(wrap.outputs[0], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], p.inputs["Base Color"])
    p.inputs["Thin Film Thickness"].default_value = thickness
    return m


def base_card(look, rim_metal=1.0):
    L = LOOKS[look]
    frame = frame_mat("Frame", srgb(L["rim"]))
    face = pattern_face("Face", srgb(L["face"]), srgb(L["rim"]))
    trim = mat("Trim", srgb(TRIM), rough=0.22, metal=1.0)
    if L.get("iridescent"):
        trim = iridescent(mat("Trim", srgb("#e8e8f0"), rough=0.18, metal=1.0), 560.0)
    # The card body is the frame colour; the face sits on it, framed by a thin gold trim.
    slab("Card", CARD_W, CARD_H, CORNER, 0.0, THICK, frame, bevel=0.04)
    fw, fh, fr = CARD_W - 2 * FRAME, CARD_H - 2 * FRAME, CORNER - FRAME * 0.55
    slab("Trim", fw + 0.10, fh + 0.10, fr + 0.05, THICK, 0.02, trim, bevel=0.008,
         hole=(fw - 0.02, fh - 0.02, fr - 0.01))
    slab("Face", fw, fh, fr, THICK, 0.008, face, bevel=0.004)
    return L


def ink_mat(hexcol, name="Ink"):
    return mat(name, srgb(hexcol), rough=0.5, coat=0.15)


def corners(label, ink, size=1.15):
    z = THICK + 0.012
    x, y = CARD_W / 2 - FRAME - 0.14, CARD_H / 2 - FRAME - 0.10 - size * 0.42
    text("TL", label, size, (-x, y, z), ink, align="LEFT")
    text("BR", label, size, (x, -y, z), ink, rot=math.pi, align="LEFT")


def build_main(value, rank):
    L = base_card(rank)
    ink = ink_mat(L["ink"])
    corners(str(value), ink, size=1.75)
    z = THICK + 0.02
    for x, y in pip_layout(value, 0.0, -0.05, 2.4, 3.9, 0.31):
        pip((x, y, z), 0.31, ink)


def sign(plus, cx, cy, length, ink):
    t = length * 0.27
    z = THICK + 0.03
    bar((cx, cy, z), length, t, ink)
    if plus:
        # Split round the crossing so no two surfaces share a plane (that renders as a black notch).
        arm = (length - t) / 2
        for sy in (1, -1):
            bar((cx, cy + sy * (t / 2 + arm / 2 - 0.02), z), t, arm + 0.04, ink)


def build_modifier(value, rescue=False):
    look = "rescue" if rescue else ("plus" if value > 0 else "minus")
    L = base_card(look)
    ink = ink_mat(L["ink"])
    label = ("+" if value > 0 else "-") + str(abs(value))
    # Playtest (2026-09-30): corner numbers on Modifiers were too small. As big as a main card's
    # for two characters; a three-character rescue ("-11") steps down so it stays inside the face.
    corners(label, ink, size=1.62 if len(label) <= 2 else 1.3)
    z = THICK + 0.02
    n = abs(value)
    if n <= 6:
        sign(value > 0, 0.0, 0.95, 1.35, ink)
        for x, y in pip_layout(n, 0.0, -1.35, 2.0, 2.2, 0.22):
            pip((x, y, z), 0.22, ink)
    else:
        # Big rescue values (up to 11): a smaller sign up top, pips given the rest of the face.
        sign(value > 0, 0.0, 1.75, 1.0, ink)
        for x, y in pip_layout(n, 0.0, -0.75, 2.0, 3.6, 0.19):
            pip((x, y, z), 0.19, ink)


GREY_INK = "#9a9aa6"


def build_flip(value, chosen_plus=True):
    """+/-: blue + half over red - half. Two renders per value: the half in play is in full
    colour, the other is greyed back - the same "which way is it set" reading the game had."""
    base_card("flip")
    plus_ink = ink_mat(PLUS_INK if chosen_plus else GREY_INK, "PlusInk")
    minus_ink = ink_mat(MINUS_INK if not chosen_plus else GREY_INK, "MinusInk")
    # A thin divider across the middle.
    div = mat("Div", srgb(LOOKS["flip"]["rim"]), rough=0.3, metal=1.0)
    bar((0, 0, THICK + 0.01), CARD_W - 1.0, 0.05, div, depth=0.012)
    n = abs(value)
    sign(True, -1.15, 1.75, 1.05, plus_ink)
    sign(False, -1.15, -1.75, 1.05, minus_ink)
    z = THICK + 0.02
    for x, y in pip_layout(n, 1.0, 1.75, 1.6, 2.4, 0.2):
        pip((x, y, z), 0.2, plus_ink)
    for x, y in pip_layout(n, 1.0, -1.75, 1.6, 2.4, 0.2):
        pip((x, y, z), 0.2, minus_ink)


BACK_LOOKS = {
    "default":  dict(face="#20304a", rim="#d9a62e", metal="#e0b64a"),
    "bronze":   dict(face="#1d3a2c", rim="#b0703a", metal="#d99a62"),
    "silver":   dict(face="#1d3440", rim="#a9b6c2", metal="#d8e2ea"),
    "gold":     dict(face="#3a2f12", rim="#d9a62e", metal="#f0c95a"),
    "ruby":     dict(face="#3a1018", rim="#b3263a", metal="#e7b25a"),
    "obsidian": dict(face="#1a1428", rim="#3d2f5c", metal="#b89cf0"),
    "endless":  dict(face="#150e3e", rim="#2a1670", metal="#e8e8f0", iridescent=True),
}


def build_back(style="default"):
    B = BACK_LOOKS[style]
    LOOKS["back"] = dict(face=B["face"], panel=B["face"], rim=B["rim"], ink=B["metal"])
    LOOKS["back"]["iridescent"] = B.get("iridescent", False)
    L = base_card("back")
    gold = mat("Gold", srgb(B["metal"]), rough=0.25, metal=1.0)
    if B.get("iridescent"):
        # Endless: the ring and an infinity sign, in iridescent metal.
        iri = iridescent(mat("Iri", srgb(B["metal"]), rough=0.14, metal=1.0), 560.0)
        slab("EmblemRing", 2.9, 2.9, 1.45, THICK, 0.03, iri, bevel=0.01, hole=(2.5, 2.5, 1.25))
        text("Inf", "\u221e", 2.1, (0, -0.05, THICK + 0.012), iri, font=FONT_TITLE)
        for sx in (-1, 1):
            for sy in (-1, 1):
                pip((sx * 1.9, sy * 2.85, THICK + 0.02), 0.13, iri)
        return
    # Emblem: a ring with "20" inside.
    ring = slab("EmblemRing", 2.9, 2.9, 1.45, THICK, 0.03, gold, bevel=0.01, hole=(2.5, 2.5, 1.25))
    text("Twenty", "20", 1.45, (0, 0, THICK + 0.012), gold)
    for sx in (-1, 1):
        for sy in (-1, 1):
            pip((sx * 1.9, sy * 2.85, THICK + 0.02), 0.13, gold)


# ----------------------------------------------------------------------------------------------
# Effect cards: a title and one emblem, each in its own colour, on the gold foil frame.
# ----------------------------------------------------------------------------------------------
FONT_TITLE = os.path.join(HERE, "fonts", "Nunito-Black.ttf")


def mini_card(cx, cy, w, h, color, rot=0.0, z=THICK + 0.02, name="Mini"):
    ob = slab(name, w, h, w * 0.14, z, 0.05, mat(name + "M", srgb(color), rough=0.35, coat=0.4), bevel=0.012)
    ob.location.x, ob.location.y = cx, cy
    ob.rotation_euler.z = rot
    return ob


def arrow_arc(cx, cy, radius, a0, a1, thickness, material, z=THICK + 0.08, head=True):
    """A thick curved arrow from angle a0 to a1 (degrees, anticlockwise), with a cone head."""
    cu = bpy.data.curves.new("Arc", "CURVE")
    cu.dimensions = "3D"
    cu.bevel_depth = thickness
    cu.bevel_resolution = 4
    sp = cu.splines.new("POLY")
    steps = 28
    end_a = a1 - (8 if head else 0) * (1 if a1 > a0 else -1)
    sp.points.add(steps)
    for i in range(steps + 1):
        a = math.radians(a0 + (end_a - a0) * i / steps)
        sp.points[i].co = (cx + radius * math.cos(a), cy + radius * math.sin(a), z, 1)
    ob = bpy.data.objects.new("Arc", cu)
    bpy.context.scene.collection.objects.link(ob)
    ob.data.materials.append(material)
    if head:
        a = math.radians(end_a)
        tip = math.radians(a1)
        bpy.ops.mesh.primitive_cone_add(vertices=24, radius1=thickness * 2.4, depth=thickness * 4.0,
                                        location=(cx + radius * math.cos(a), cy + radius * math.sin(a), z))
        cone = bpy.context.active_object
        dirn = Vector((math.cos(tip) - math.cos(a), math.sin(tip) - math.sin(a), 0)).normalized()
        cone.rotation_mode = "QUATERNION"
        cone.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(dirn)
        cone.location += dirn * thickness * 1.6
        bpy.ops.object.shade_smooth()
        cone.data.materials.append(material)
    return ob


def token(cx, cy, r, color, label, ink="#ffffff", z=THICK + 0.02):
    bpy.ops.mesh.primitive_cylinder_add(vertices=64, radius=r, depth=0.12, location=(cx, cy, z + 0.06))
    ob = bpy.context.active_object
    bv = ob.modifiers.new("Bevel", "BEVEL")
    bv.width = 0.04
    bv.segments = 4
    bpy.ops.object.shade_smooth()
    ob.data.materials.append(mat("Token", srgb(color), rough=0.3, metal=0.6, coat=0.5))
    text("TokLabel", label, r * 1.05, (cx, cy, z + 0.125), ink_mat(ink, "TokInk"), extrude=0.012)
    return ob


def lighten(hexcol, t):
    h = hexcol.lstrip("#")
    c = [int(h[i:i + 2], 16) for i in (0, 2, 4)]
    c = [round(x + (255 - x) * t) for x in c]
    return "#%02x%02x%02x" % tuple(c)


def sparkle(cx, cy, size, material, z):
    """A four-point glint: two thin bevelled bars crossed at right angles."""
    for rot in (0.0, math.pi / 2):
        bpy.ops.mesh.primitive_cube_add(size=1, location=(cx, cy, z))
        d = bpy.context.active_object
        d.scale = (size, size * 0.16, 0.02)
        d.rotation_euler.z = rot
        bv = d.modifiers.new("Bevel", "BEVEL"); bv.width = size * 0.07; bv.segments = 3
        bpy.ops.object.shade_smooth()
        d.data.materials.append(material)


def medallion(color, cy=-0.45):
    """The stage every emblem stands on: a pale sunburst disc in the card's colour, a metal ring
    round it, and a couple of glints - so the emblem reads as a crest, not shapes on paper."""
    z = THICK + 0.01
    disc = face_mat("Disc", srgb(lighten(color, 0.82)))
    slab("Disc", 3.9, 3.9, 1.95, z, 0.01, disc, bevel=0.004).location.y = cy
    rays = mat("Rays", srgb(lighten(color, 0.62)), rough=0.5)
    for i in range(12):
        a = i * math.pi / 6
        bpy.ops.mesh.primitive_cube_add(size=1, location=(math.cos(a) * 1.15, cy + math.sin(a) * 1.15, z + 0.012))
        r = bpy.context.active_object
        r.scale = (1.3, 0.16, 0.004)
        r.rotation_euler.z = a
        r.data.materials.append(rays)
    ring = mat("Ring", srgb(color), rough=0.25, metal=0.85, coat=0.4)
    slab("Ring", 4.05, 4.05, 2.02, z + 0.005, 0.03, ring, bevel=0.01, hole=(3.8, 3.8, 1.9)).location.y = cy
    glint = mat("Glint", srgb("#fffaf0"), rough=0.3)
    gp = glint.node_tree.nodes["Principled BSDF"]
    gp.inputs["Emission Color"].default_value = srgb("#fff3c4")
    gp.inputs["Emission Strength"].default_value = 1.6
    sparkle(1.55, cy + 1.55, 0.42, glint, z + 0.25)
    sparkle(-1.7, cy - 1.35, 0.3, glint, z + 0.25)


def effect_card(title, color):
    L = base_card("effect")
    medallion(color)
    ink = ink_mat(color, "TitleInk")
    # Title banner across the top of the face.
    banner = mat("Banner", srgb(color), rough=0.35, coat=0.5)
    fw = CARD_W - 2 * FRAME
    slab("Banner", fw - 0.3, 0.95, 0.3, THICK + 0.01, 0.03, banner, bevel=0.01).location.y = CARD_H / 2 - FRAME - 0.72
    text("Title", title.upper(), 0.62 if len(title) < 9 else 0.5, (0, CARD_H / 2 - FRAME - 0.72, THICK + 0.05),
         ink_mat("#ffffff", "TitleWhite"), extrude=0.015, font=FONT_TITLE)
    return srgb(color)


def build_effect(kind):
    z = THICK + 0.02
    if kind == "copy":
        c = "#1f9aa8"
        effect_card("Copy", c)
        ghost = mini_card(-0.45, 0.1, 1.7, 2.3, "#bfe9ee", rot=math.radians(-8), name="Ghost")
        front = mini_card(0.45, -0.35, 1.7, 2.3, "#ffffff", rot=math.radians(6), z=z + 0.08, name="Front")
        for cx, cy, zz, col in ((-0.45, 0.1, z + 0.08, "#7cc9d2"), (0.45, -0.35, z + 0.16, c)):
            for x, y in pip_layout(3, cx, cy, 0.8, 1.2, 0.14):
                pip((x, y, zz), 0.14, ink_mat(col, "P" + col))
        arrow_arc(0.0, -0.1, 1.55, 150, 40, 0.07, ink_mat(c, "Arr"))
    elif kind == "tradetotals":
        c = "#7a3fd1"
        effect_card("Trade Totals", c)
        token(-1.0, -0.2, 0.75, "#3f7fe0", "12")
        token(1.0, -0.2, 0.75, "#d8474f", "18")
        a = ink_mat(c, "Arr")
        arrow_arc(0.0, -0.2, 1.25, 160, 20, 0.08, a)
        arrow_arc(0.0, -0.2, 1.25, -20, -160, 0.08, a)
    elif kind == "tradehands":
        c = "#e0782a"
        effect_card("Trade Hands", c)
        # Two fanned hands - blue top-left, red bottom-right - with arrows passing between them.
        for i, rot in enumerate((22, 0, -22)):
            mini_card(-0.85 + (i - 1) * 0.42, 0.55 - abs(i - 1) * 0.1, 1.05, 1.45, "#3f7fe0",
                      rot=math.radians(rot), z=z + i * 0.03, name=f"L{i}")
            mini_card(0.85 + (i - 1) * 0.42, -1.3 - abs(i - 1) * 0.1, 1.05, 1.45, "#d8474f",
                      rot=math.radians(rot), z=z + i * 0.03, name=f"R{i}")
        a = ink_mat(c, "Arr")
        arrow_arc(0.0, -0.35, 1.75, 120, 10, 0.08, a)
        arrow_arc(0.0, -0.35, 1.75, -60, -170, 0.08, a)
    elif kind == "shave":
        c = "#c2303f"
        effect_card("Shave", c)
        token(-0.35, -0.7, 1.05, "#3f7fe0", "19")
        # A blade: a thin wedge sweeping across the token, and the shaved sliver flying off.
        steel = mat("Steel", srgb("#dfe6ee"), rough=0.12, metal=1.0)
        bpy.ops.mesh.primitive_cube_add(size=1, location=(0.35, 0.55, z + 0.3))
        blade = bpy.context.active_object
        blade.scale = (2.6, 0.42, 0.05)
        blade.rotation_euler.z = math.radians(-24)
        bv = blade.modifiers.new("Bevel", "BEVEL"); bv.width = 0.03; bv.segments = 3
        blade.data.materials.append(steel)
        grip = mat("Grip", srgb(c), rough=0.4, coat=0.4)
        bar((1.75, -0.1, z + 0.3), 0.9, 0.5, grip).rotation_euler.z = math.radians(-24)
        mini = mat("Sliver", srgb("#3f7fe0"), rough=0.3, metal=0.6)
        slab("Sliver", 0.7, 0.18, 0.08, z + 0.1, 0.05, mini, bevel=0.01).location = (0.55, 1.55, z + 0.1)
        text("Minus", "-1", 0.95, (1.55, 1.25, z + 0.12), ink_mat(c, "Mi"))
    elif kind == "recall":
        c = "#1e8f5a"
        effect_card("Recall", c)
        mini_card(0.3, -0.6, 1.6, 2.2, "#ffffff", rot=math.radians(-6), name="Card")
        for x, y in pip_layout(4, 0.3, -0.6, 0.8, 1.2, 0.14):
            pip((x, y, z + 0.08), 0.14, ink_mat(c, "RP"))
        arrow_arc(0.0, -0.3, 1.75, 20, 250, 0.08, ink_mat(c, "Arr"))
    elif kind == "veto":
        c = "#b3263a"
        effect_card("Veto", c)
        mini_card(0.0, -0.4, 1.7, 2.3, "#f3f3f3", name="Card")
        for x, y in pip_layout(3, 0.0, -0.4, 0.8, 1.2, 0.15):
            pip((x, y, z + 0.08), 0.15, ink_mat("#9a9aa6", "VP"))
        red = mat("No", srgb(c), rough=0.3, coat=0.6)
        slab("NoRing", 2.9, 2.9, 1.45, z + 0.14, 0.06, red, bevel=0.012, hole=(2.4, 2.4, 1.2)).location.y = -0.4
        slash = bar((0.0, -0.4, z + 0.2), 2.55, 0.26, red, depth=0.06)
        slash.rotation_euler.z = math.radians(45)


EFFECTS = ["copy", "tradetotals", "tradehands", "shave", "recall", "veto"]


# ----------------------------------------------------------------------------------------------
# Playmats: one table per rank, portrait 1080x1920 and landscape 1920x1080.
#
# The table itself is calm - a surface texture and soft light falloff - because the cards are the
# show. Detail lives at the edges: an inlaid border line and corner ornaments. The game draws its
# own centre line and ring on top.
# ----------------------------------------------------------------------------------------------
MATS = {
    #           surface       inlay       kind      (noise scale, bump, roughness)
    "bronze":   ("#1f4a36", "#b0703a", "felt"),
    "silver":   ("#27404d", "#a9b6c2", "slate"),
    "gold":     ("#6a3f0c", "#e6b54a", "lacquer"),
    "ruby":     ("#4a1420", "#d9a62e", "velvet"),
    "obsidian": ("#17121f", "#8e6ad8", "glass"),
    "endless":  ("#120f2e", "#e8e8f0", "void"),
}


def surface_mat(color, kind):
    m = mat("Surface", srgb(color))
    nt = m.node_tree
    p = nt.nodes["Principled BSDF"]
    noise = nt.nodes.new("ShaderNodeTexNoise")
    bump = nt.nodes.new("ShaderNodeBump")
    nt.links.new(noise.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], p.inputs["Normal"])
    settings = {
        "felt":    (160.0, 0.25, 0.95, 0.0),
        "slate":   (6.0, 0.20, 0.70, 0.0),
        "lacquer": (2.0, 0.03, 0.40, 0.0),
        "velvet":  (120.0, 0.18, 0.85, 0.0),
        "glass":   (3.0, 0.06, 0.42, 0.0),
        "void":    (1.4, 0.02, 0.55, 0.0),
    }[kind]
    noise.inputs["Scale"].default_value = settings[0]
    noise.inputs["Detail"].default_value = 6.0
    bump.inputs["Strength"].default_value = settings[1]
    p.inputs["Roughness"].default_value = settings[2]
    if kind == "lacquer":
        # Lacquered wood: long soft grain bands, darker streaks in the amber.
        grain = nt.nodes.new("ShaderNodeTexWave")
        grain.wave_type = "BANDS"
        grain.bands_direction = "Y"
        grain.inputs["Scale"].default_value = 7.0
        grain.inputs["Distortion"].default_value = 2.5
        grain.inputs["Detail"].default_value = 4.0
        ramp = nt.nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].color = srgb("#58330a")
        ramp.color_ramp.elements[1].color = srgb(color)
        nt.links.new(grain.outputs["Fac"], ramp.inputs["Fac"])
        nt.links.new(ramp.outputs["Color"], p.inputs["Base Color"])
    if kind == "void":
        # A night sky of slow colour: indigo drifting to violet and teal, with faint star specks.
        ramp = nt.nodes.new("ShaderNodeValToRGB")
        noise.inputs["Scale"].default_value = 3.5
        noise.inputs["Detail"].default_value = 8.0
        ramp.color_ramp.elements[0].position = 0.32
        ramp.color_ramp.elements[0].color = srgb("#07061a")
        ramp.color_ramp.elements[1].position = 0.70
        ramp.color_ramp.elements[1].color = srgb("#0c4a63")
        mid = ramp.color_ramp.elements.new(0.5)
        mid.color = srgb("#3b1d86")
        nt.links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
        stars = nt.nodes.new("ShaderNodeTexVoronoi")
        stars.inputs["Scale"].default_value = 90.0
        thr = nt.nodes.new("ShaderNodeMath")
        thr.operation = "LESS_THAN"
        thr.inputs[1].default_value = 0.035
        nt.links.new(stars.outputs["Distance"], thr.inputs[0])
        mixs = nt.nodes.new("ShaderNodeMix")
        mixs.data_type = "RGBA"
        nt.links.new(thr.outputs[0], mixs.inputs["Factor"])
        nt.links.new(ramp.outputs["Color"], rgba_sock(mixs.inputs, "A"))
        rgba_sock(mixs.inputs, "B").default_value = (0.8, 0.82, 1.0, 1)
        nt.links.new(rgba_sock(mixs.outputs, "Result"), p.inputs["Base Color"])
    if kind == "velvet":
        p.inputs["Sheen Weight"].default_value = 0.6
    if kind in ("glass", "lacquer"):
        # A satin coat: the light shows as a soft glow, not a mirror image of the lamp.
        p.inputs["Specular IOR Level"].default_value = 0.25
    # Slate and glass get faint veins, so they read as stone rather than paint.
    if kind in ("slate", "glass"):
        vein = nt.nodes.new("ShaderNodeTexWave")
        vein.bands_direction = "DIAGONAL"
        vein.inputs["Scale"].default_value = 0.7
        vein.inputs["Distortion"].default_value = 18.0
        vein.inputs["Detail"].default_value = 8.0
        vein.inputs["Detail Scale"].default_value = 2.0
        ramp = nt.nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].position = 0.955
        ramp.color_ramp.elements[0].color = srgb(color)
        lighter = [min(1.0, c * (1.5 if kind == "slate" else 2.2)) for c in srgb(color)[:3]] + [1]
        ramp.color_ramp.elements[1].color = lighter
        nt.links.new(vein.outputs["Fac"], ramp.inputs["Fac"])
        nt.links.new(ramp.outputs["Color"], p.inputs["Base Color"])
    return m


def build_playmat(rank, portrait):
    color, inlay, kind = MATS[rank]
    scene = bpy.context.scene
    w, h = (10.8, 19.2) if portrait else (19.2, 10.8)
    scene.render.resolution_x, scene.render.resolution_y = int(w * 100), int(h * 100)
    scene.camera.data.ortho_scale = max(w, h)
    scene.render.film_transparent = False
    # The card rig is for cards: a table gets only its own pool of light and a dim sky.
    for name in ("Key", "Fill", "Rim"):
        ob = bpy.data.objects.get(name)
        if ob:
            bpy.data.objects.remove(ob, do_unlink=True)
    scene.world.node_tree.nodes["Background"].inputs[1].default_value = 0.12
    bpy.ops.mesh.primitive_plane_add(size=1, location=(0, 0, 0))
    table = bpy.context.active_object
    table.scale = (w + 0.4, h + 0.4, 1)
    table.data.materials.append(surface_mat(color, kind))
    metal = mat("Inlay", srgb(inlay), rough=0.3, metal=1.0)
    if kind == "void":
        metal = iridescent(mat("Inlay", srgb(inlay), rough=0.15, metal=1.0), 560.0)
    m = 0.45
    slab("Inlay", w - 2 * m, h - 2 * m, 0.6, 0.0, 0.015, metal, bevel=0.005,
         hole=(w - 2 * m - 0.07, h - 2 * m - 0.07, 0.56))
    slab("Inlay2", w - 2 * m - 0.3, h - 2 * m - 0.3, 0.45, 0.0, 0.01, metal, bevel=0.004,
         hole=(w - 2 * m - 0.34, h - 2 * m - 0.34, 0.43))
    for sx in (-1, 1):
        for sy in (-1, 1):
            cx, cy = sx * (w / 2 - m - 0.15), sy * (h / 2 - m - 0.15)
            pip((cx, cy, 0.02), 0.14, metal)
    # A soft pool of light in the middle, falling off to the edges.
    d = bpy.data.lights.new("Pool", "AREA")
    d.shape = "ELLIPSE"
    d.size, d.size_y = w * 0.9, h * 0.9
    d.energy = {"glass": 1000, "void": 700}.get(kind, 1300)
    o = bpy.data.objects.new("Pool", d)
    o.location = (0, 0, 6)
    scene.collection.objects.link(o)


# ----------------------------------------------------------------------------------------------
# The card list
# ----------------------------------------------------------------------------------------------
RANKS = ["bronze", "silver", "gold", "ruby", "obsidian"]
THEMES = RANKS + ["endless"]   # endless is the Endless mode's own set; not a rank, not in the shop


def cards(which):
    out = []
    if which in ("all", "main"):
        for r in THEMES:
            for v in range(1, 11):
                out.append((f"cards/main/main_{v}_{r}", lambda v=v, r=r: build_main(v, r)))
    if which in ("all", "mods"):
        for v in range(1, 7):
            out.append((f"cards/mods/plus_{v}", lambda v=v: build_modifier(v)))
            out.append((f"cards/mods/minus_{v}", lambda v=v: build_modifier(-v)))
            out.append((f"cards/mods/flip_{v}_plus", lambda v=v: build_flip(v, True)))
            out.append((f"cards/mods/flip_{v}_minus", lambda v=v: build_flip(v, False)))
        for v in range(1, 12):
            out.append((f"cards/mods/rescue_plus_{v}", lambda v=v: build_modifier(v, rescue=True)))
            out.append((f"cards/mods/rescue_minus_{v}", lambda v=v: build_modifier(-v, rescue=True)))
    if which in ("all", "effects"):
        for e in EFFECTS:
            out.append((f"cards/effect_{e}", lambda e=e: build_effect(e)))
    if which in ("all", "mats"):
        for r in THEMES:
            for portrait in (True, False):
                orient = "portrait" if portrait else "landscape"
                out.append((f"playmats/playmat_{r}_{orient}", lambda r=r, p=portrait: build_playmat(r, p)))
    if which in ("all", "backs"):
        out.append(("card_back", build_back))
        for r in THEMES:
            out.append((f"backs/card_back_{r}", lambda r=r: build_back(r)))
    return out


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    ap = argparse.ArgumentParser()
    ap.add_argument("--set", default="all")
    ap.add_argument("--only", default=None)
    ap.add_argument("--out", default=os.path.join(HERE, "renders"))
    ap.add_argument("--samples", type=int, default=48)
    a = ap.parse_args(argv)
    todo = cards("all" if a.only else a.set)
    if a.only:
        todo = [c for c in todo if os.path.basename(c[0]) == a.only]
    for name, build in todo:
        reset_scene(a.samples)
        build()
        path = os.path.join(a.out, name + ".png")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        bpy.context.scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        print("wrote", path, flush=True)


if __name__ == "__main__":
    main()
