import bpy, bmesh, math, os, sys
from mathutils import Vector

OUT = sys.argv[-1]
os.makedirs(OUT, exist_ok=True)
W, H, T, R = 1.4, 1.9, 0.05, 0.16      # card size (matches 140x190), thickness, corner radius
PX = 4                                   # 4x the in-game 140x190 -> 560x760


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.device = 'CPU'
    sc.cycles.samples = 48
    sc.cycles.use_denoising = True
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGBA'
    sc.view_settings.view_transform = 'Standard'
    return sc


def rounded_rect_mesh(name, w, h, r, z=0.0, seg=10):
    pts = []
    cx, cy = w / 2 - r, h / 2 - r
    for (sx, sy, a0) in ((1, 1, 0), (-1, 1, 90), (-1, -1, 180), (1, -1, 270)):
        for i in range(seg + 1):
            a = math.radians(a0 + 90 * i / seg)
            pts.append((sx * cx + r * math.cos(a) if False else (sx * cx) + r * math.cos(a),
                        (sy * cy) + r * math.sin(a)))
    bm = bmesh.new()
    vs = [bm.verts.new((x, y, z)) for x, y in pts]
    bm.faces.new(vs)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def solid(ob, thickness, bevel=0.012):
    m = ob.modifiers.new('solid', 'SOLIDIFY')
    m.thickness = thickness
    m.offset = -1
    b = ob.modifiers.new('bevel', 'BEVEL')
    b.width = bevel
    b.segments = 3
    b.limit_method = 'ANGLE'
    for p in ob.data.polygons:
        p.use_smooth = True


def mat(name, c1, c2, rough=0.55, metal=0.0, rim=None, rainbow=False, axis='Y'):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    bsdf = nt.nodes.new('ShaderNodeBsdfPrincipled')
    bsdf.inputs['Roughness'].default_value = rough
    bsdf.inputs['Metallic'].default_value = metal
    tc = nt.nodes.new('ShaderNodeTexCoord')
    sep = nt.nodes.new('ShaderNodeSeparateXYZ')
    nt.links.new(tc.outputs['Object'], sep.inputs[0])
    mapr = nt.nodes.new('ShaderNodeMapRange')
    if rainbow:
        mapr.inputs['From Min'].default_value = -1.6
        mapr.inputs['From Max'].default_value = 1.6
        add = nt.nodes.new('ShaderNodeMath'); add.operation = 'ADD'
        nt.links.new(sep.outputs['X'], add.inputs[0]); nt.links.new(sep.outputs['Y'], add.inputs[1])
        nt.links.new(add.outputs[0], mapr.inputs[0])
    else:
        mapr.inputs['From Min'].default_value = -H / 2
        mapr.inputs['From Max'].default_value = H / 2
        nt.links.new(sep.outputs['Y'], mapr.inputs[0])
    ramp = nt.nodes.new('ShaderNodeValToRGB')
    nt.links.new(mapr.outputs[0], ramp.inputs[0])
    if rainbow:
        el = ramp.color_ramp.elements
        el[0].color = (1.0, 0.55, 0.85, 1); el[1].color = (0.45, 0.9, 1.0, 1)
        e = el.new(0.35); e.color = (1.0, 0.95, 0.5, 1)
        e = el.new(0.65); e.color = (0.6, 1.0, 0.7, 1)
    else:
        ramp.color_ramp.elements[0].color = c1
        ramp.color_ramp.elements[1].color = c2
    nt.links.new(ramp.outputs['Color'], bsdf.inputs['Base Color'])
    last = bsdf.outputs[0]
    if rim:
        lw = nt.nodes.new('ShaderNodeLayerWeight'); lw.inputs['Blend'].default_value = 0.6
        em = nt.nodes.new('ShaderNodeEmission'); em.inputs['Color'].default_value = rim
        em.inputs['Strength'].default_value = 0.8
        mix = nt.nodes.new('ShaderNodeMixShader')
        nt.links.new(lw.outputs['Fresnel'], mix.inputs[0])
        nt.links.new(bsdf.outputs[0], mix.inputs[1]); nt.links.new(em.outputs[0], mix.inputs[2])
        last = mix.outputs[0]
    nt.links.new(last, out.inputs['Surface'])
    return m


def hexc(h, a=1.0):
    h = h.lstrip('#')
    lin = lambda v: ((v / 255 + 0.055) / 1.055) ** 2.4 if v / 255 > 0.04045 else v / 255 / 12.92
    return (lin(int(h[0:2], 16)), lin(int(h[2:4], 16)), lin(int(h[4:6], 16)), a)


def lighting_and_camera(w_px, h_px, scale):
    sc = bpy.context.scene
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    cam.data.type = 'ORTHO'
    cam.data.ortho_scale = scale
    cam.location = (0, 0, 5)
    sc.collection.objects.link(cam)
    sc.camera = cam
    sc.render.resolution_x, sc.render.resolution_y = w_px, h_px
    # soft wide key, low intensity, slightly off-axis so bevels catch light
    key = bpy.data.lights.new('key', 'AREA'); key.size = 6; key.energy = 90
    ko = bpy.data.objects.new('key', key); ko.location = (-1.5, 2.5, 4); ko.rotation_euler = (math.radians(-25), math.radians(-20), 0)
    sc.collection.objects.link(ko)
    # rim light from behind/low angle
    rl = bpy.data.lights.new('rim', 'AREA'); rl.size = 4; rl.energy = 40
    ro = bpy.data.objects.new('rim', rl); ro.location = (1.8, -2.5, 1.2); ro.rotation_euler = (math.radians(70), math.radians(25), 0)
    sc.collection.objects.link(ro)
    w = bpy.data.worlds.new('w'); w.use_nodes = True
    w.node_tree.nodes['Background'].inputs['Color'].default_value = (0.9, 0.93, 1.0, 1)
    w.node_tree.nodes['Background'].inputs['Strength'].default_value = 0.8
    sc.world = w


def render(name):
    bpy.context.scene.render.filepath = os.path.join(OUT, name + '.png')
    bpy.ops.render.render(write_still=True)


def build_card(name, c_top, c_bot, border, rim, panel_top, panel_bot, rainbow=False, emblem=None):
    sc = reset()
    body = rounded_rect_mesh('body', W, H, R)
    solid(body, T)
    body.data.materials.append(mat('body', c_top, c_bot, rough=0.45, rim=rim))
    pan = rounded_rect_mesh('panel', W - 0.22, H - 0.22, R * 0.65, z=T + 0.001)
    solid(pan, 0.012, bevel=0.005)
    pan.data.materials.append(mat('panel', panel_bot, panel_top, rough=0.8 if not rainbow else 0.3,
                                  metal=0.0 if not rainbow else 0.7, rainbow=rainbow))
    if emblem:
        emblem(T + 0.017)
    lighting_and_camera(int(W * 100 * PX), int(H * 100 * PX), H)
    render(name)


def back_emblem(z):
    # simple modern geometric mark: concentric diamond + circle
    bpy.ops.mesh.primitive_cylinder_add(vertices=64, radius=0.42, depth=0.006, location=(0, 0, z))
    c = bpy.context.object
    c.data.materials.append(mat('e1', hexc('#5EE6C8'), hexc('#3BB3F0'), rough=0.35, rim=hexc('#9FFFF0')))
    bpy.ops.mesh.primitive_cylinder_add(vertices=64, radius=0.30, depth=0.008, location=(0, 0, z + 0.002))
    c2 = bpy.context.object
    c2.data.materials.append(mat('e2', hexc('#14304A'), hexc('#0C1E33'), rough=0.6))
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, z + 0.006))
    d = bpy.context.object
    d.scale = (0.22, 0.22, 0.008); d.rotation_euler = (0, 0, math.radians(45))
    d.data.materials.append(mat('e3', hexc('#FFD86B'), hexc('#FFB347'), rough=0.3, rim=hexc('#FFF1B8')))


# name, ring top, ring bottom, border, rim, panel top, panel bottom
SPECS = {
    'card_main':   dict(c_top=hexc('#7CF0A6'), c_bot=hexc('#2FBF71'), border=hexc('#1E8F55'), rim=hexc('#C9FFE0'),
                        panel_top=hexc('#F4FFF8'), panel_bot=hexc('#D3F5E1')),
    'card_plus':   dict(c_top=hexc('#7CC4FF'), c_bot=hexc('#2F7BEA'), border=hexc('#2158B8'), rim=hexc('#CFE6FF'),
                        panel_top=hexc('#F3F9FF'), panel_bot=hexc('#D2E5FB')),
    'card_minus':  dict(c_top=hexc('#FF9A9A'), c_bot=hexc('#E5484D'), border=hexc('#B12F3A'), rim=hexc('#FFD3D3'),
                        panel_top=hexc('#FFF5F5'), panel_bot=hexc('#FAD5D6')),
    'card_flip':   dict(c_top=hexc('#C9A0FF'), c_bot=hexc('#7B4DE0'), border=hexc('#5A34B0'), rim=hexc('#E6D4FF'),
                        panel_top=hexc('#F9F5FF'), panel_bot=hexc('#E2D6FA')),
    'card_foil':   dict(c_top=hexc('#FFE08A'), c_bot=hexc('#F5A524'), border=hexc('#B9770E'), rim=hexc('#FFF3C4'),
                        panel_top=hexc('#FFFFFF'), panel_bot=hexc('#FFFFFF'), rainbow=True),
}

if __name__ == '__main__':
    only = os.environ.get("ONLY")
    for n, s in SPECS.items():
        if only and n != only: continue
        build_card(n, **s)
    # card back
    if not os.environ.get('ONLY') or os.environ['ONLY']=='card_back': build_card('card_back', c_top=hexc('#3BB3F0'), c_bot=hexc('#5EE6C8'), border=hexc('#101E33'), rim=hexc('#7FD8FF'),
               panel_top=hexc('#1D4A73'), panel_bot=hexc('#0F2A47'), emblem=back_emblem)
