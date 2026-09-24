import bpy, math, os, sys
sys.path.insert(0, os.path.dirname(__file__))
import make_cards as mc
from make_cards import hexc, rounded_rect_mesh, solid, mat, reset, lighting_and_camera, render
from PIL import Image

OUT = sys.argv[-1]
os.makedirs(OUT, exist_ok=True)


def radial_mat(name, inner, outer, rough=0.95, bump=0.15, span=(0.0, 1.0), scale=1.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    b = nt.nodes.new('ShaderNodeBsdfPrincipled')
    b.inputs['Roughness'].default_value = rough
    tc = nt.nodes.new('ShaderNodeTexCoord')
    ln = nt.nodes.new('ShaderNodeVectorMath'); ln.operation = 'LENGTH'
    nt.links.new(tc.outputs['Object'], ln.inputs[0])
    mr = nt.nodes.new('ShaderNodeMapRange')
    mr.inputs['From Min'].default_value = span[0] * scale
    mr.inputs['From Max'].default_value = span[1] * scale
    nt.links.new(ln.outputs['Value'], mr.inputs[0])
    ramp = nt.nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].color = inner
    ramp.color_ramp.elements[1].color = outer
    nt.links.new(mr.outputs[0], ramp.inputs[0])
    nt.links.new(ramp.outputs['Color'], b.inputs['Base Color'])
    # fine felt grain
    noise = nt.nodes.new('ShaderNodeTexNoise')
    noise.inputs['Scale'].default_value = 900
    noise.inputs['Detail'].default_value = 6
    bp = nt.nodes.new('ShaderNodeBump'); bp.inputs['Strength'].default_value = bump
    nt.links.new(tc.outputs['Object'], noise.inputs['Vector'])
    nt.links.new(noise.outputs['Fac'], bp.inputs['Height'])
    nt.links.new(bp.outputs['Normal'], b.inputs['Normal'])
    nt.links.new(b.outputs[0], out.inputs['Surface'])
    return m


def playmat(name, w_units, h_units, w_px, h_px):
    sc = reset()
    sc.render.film_transparent = False
    sc.cycles.samples = 32
    base = rounded_rect_mesh('mat', w_units, h_units, 0.0001, seg=1)
    diag = math.hypot(w_units, h_units) / 2
    base.data.materials.append(radial_mat('felt', hexc('#14585A'), hexc('#081F27'), span=(0.0, 1.0), scale=diag))
    # thin inset frame line, lighter, gives the "table edge" without clutter
    m = 0.55
    fo = rounded_rect_mesh('fo', w_units - m, h_units - m, 0.35, z=0.002)
    solid(fo, 0.004, bevel=0.002)
    fo.data.materials.append(radial_mat('fo', hexc('#2C8A86'), hexc('#1A5F66'), span=(0.0, 1.0), scale=diag, bump=0.05))
    fi = rounded_rect_mesh('fi', w_units - m - 0.035, h_units - m - 0.035, 0.33, z=0.0065)
    solid(fi, 0.004, bevel=0.002)
    fi.data.materials.append(radial_mat('fi', hexc('#14585A'), hexc('#081F27'), span=(0.0, 1.0), scale=diag))
    lighting_and_camera(w_px, h_px, max(w_units, h_units))
    bpy.data.objects.remove(bpy.data.objects['rim'])
    k = bpy.data.objects['key']; k.location = (0, 0, 6); k.rotation_euler = (0, 0, 0)
    k.data.size = 26; k.data.energy = float(os.environ.get('KEY', 250))
    bpy.context.scene.world.node_tree.nodes['Background'].inputs['Strength'].default_value = 0.5
    render(name)


def icon():
    sc = reset()
    sc.render.film_transparent = False
    S = 6.0
    bg = rounded_rect_mesh('bg', S, S, 0.0001, seg=1, z=-0.2)
    bg.data.materials.append(radial_mat('bg', hexc('#1B9AA6'), hexc('#0A1F44'), rough=0.9, bump=0.0, span=(0.0, 1.0), scale=3.6))
    # gold target ring behind cards
    bpy.ops.mesh.primitive_torus_add(major_radius=2.0, minor_radius=0.11, major_segments=96, minor_segments=24,
                                     location=(0, 0, -0.1))
    ring = bpy.context.object
    ring.data.materials.append(mat('ring', hexc('#FFD86B'), hexc('#FFB347'), rough=0.3, rim=hexc('#FFF1B8')))
    ring.scale = (1, 1, 0.4)
    # back card, fanned left
    mc_body = lambda n, spec, rot, loc, z:  None
    def card(spec, rot, loc, z):
        b = rounded_rect_mesh('c', mc.W, mc.H, mc.R, z=z)
        solid(b, mc.T)
        b.data.materials.append(mat('cb', spec['c_top'], spec['c_bot'], rough=0.45, rim=spec['rim']))
        p = rounded_rect_mesh('p', mc.W - 0.22, mc.H - 0.22, mc.R * 0.65, z=z + mc.T + 0.001)
        solid(p, 0.012, bevel=0.005)
        p.data.materials.append(mat('cp', spec['panel_bot'], spec['panel_top'], rough=0.8))
        for o in (b, p):
            o.rotation_euler = (0, 0, math.radians(rot)); o.location = (loc[0]*1.2, loc[1]*1.2, 0); o.scale = (1.2, 1.2, 1)
        return z + mc.T + 0.013
    card(mc.SPECS['card_minus'], 16, (-0.78, -0.12), 0.0)
    card(mc.SPECS['card_plus'], -16, (0.78, -0.12), 0.02)
    ztop = card(mc.SPECS['card_main'], 0, (0, 0.05), 0.06)
    bpy.ops.object.text_add(location=(0, 0.02, ztop + 0.01))
    t = bpy.context.object
    t.data.body = '20'
    t.data.size = 1.02
    t.data.extrude = 0.03
    t.data.align_x = 'CENTER'; t.data.align_y = 'CENTER'
    t.data.materials.append(mat('txt', hexc('#1E8F55'), hexc('#0F6A3C'), rough=0.4))
    lighting_and_camera(1024, 1024, 4.6)
    render('icon_1024')


if __name__ == '__main__':
    what = os.environ.get('WHAT', 'all')
    if what in ('all', 'mat'):
        playmat('playmat_portrait', 9, 16, int(os.environ.get('PW',1080)), int(os.environ.get('PH',1920)))
        playmat('playmat_landscape', 16, 9, int(os.environ.get('PH',1920)), int(os.environ.get('PW',1080)))
    if what in ('all', 'icon'):
        icon()
        im = Image.open(os.path.join(OUT, 'icon_1024.png')).convert('RGB')
        im.resize((512, 512), Image.LANCZOS).save(os.path.join(OUT, 'store_icon_512.png'))
