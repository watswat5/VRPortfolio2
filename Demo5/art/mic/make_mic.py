"""The player's handheld mic: a dynamic stage mic (ball grille, tapered handle), with its textures, for Unity:
unity/Assets/Models/mic/mic.fbx plus mic_mesh_albedo.png, mic_mesh_normal.png and mic_mesh_metal.png (the grille's
woven wire mesh: colour, normal map, and metallic in R / smoothness in A for URP Lit).

Run with Blender (a flatpak here; give it an absolute path):
    flatpak run org.blender.Blender -b --python "$PWD/art/mic/make_mic.py"

Parts, one object per material (Freestyle > Build Mic gives each its URP material):
  Body     the matte black handle, tapering down to the XLR end
  Chrome   the band under the grille, the ring round its middle, the XLR end cap
  Grille   the ball: woven wire mesh over the dark foam (its UVs tile the mesh texture)
  Switch   an on/off slide switch on the front of the handle, its knob slid up to ON
  Light    the power light above it (lit: Build Mic makes it glow)
16.5 cm long, 5 cm across the grille. Along +Y from the handle to the grille (Unity), front (switch) toward +Z; the
pivot is where the fist grips the handle.
"""
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "unity", "Assets", "Models", "mic")
GRIP = 0.055          # the pivot: this far up from the bottom
SEG = 24              # round the handle


def material(name, colour, rough, metal=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    b.inputs["Base Color"].default_value = tuple(c ** 2.2 for c in colour) + (1,)
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metal
    return m


def obj(name, bm, mat, smooth=True):
    for f in bm.faces:
        f.smooth = smooth
    me = bpy.data.meshes.new(name)
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    o = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(o)
    o.location = (0, 0, -GRIP)   # (applied below: the pivot at the grip)
    return o


def lathe(bm, profile, seg=SEG, cap_bottom=False, cap_top=False):
    """Round Blender's Z from (radius, z) points, bottom to top."""
    rings = [[bm.verts.new((r * math.cos(2 * math.pi * k / seg), r * math.sin(2 * math.pi * k / seg), z))
              for k in range(seg)] for r, z in profile]
    for a, b in zip(rings, rings[1:]):
        for k in range(seg):
            bm.faces.new([a[k], a[(k + 1) % seg], b[(k + 1) % seg], b[k]])
    if cap_bottom:
        bm.faces.new(rings[0][::-1])
    if cap_top:
        bm.faces.new(rings[-1])


def build():
    body = material("Mic Body", (0.05, 0.05, 0.055), 0.6)
    chrome = material("Mic Chrome", (0.8, 0.8, 0.82), 0.15, 1.0)
    grille = material("Mic Grille", (0.7, 0.7, 0.72), 0.4, 0.9)
    switch = material("Mic Switch", (0.12, 0.12, 0.13), 0.45)
    light = material("Mic Light", (0.2, 1.0, 0.45), 0.3)

    # the handle: from the XLR end up, tapering out to the band
    bm = bmesh.new()
    lathe(bm, [(0.0112, 0.012), (0.0118, 0.03), (0.0135, 0.06), (0.0152, 0.085), (0.0165, 0.1)])
    h = obj("Body", bm, body)

    bm = bmesh.new()
    lathe(bm, [(0.0085, 0.0), (0.0108, 0.0005), (0.0114, 0.003), (0.0114, 0.012)], cap_bottom=True)   # XLR end cap
    lathe(bm, [(0.0165, 0.1), (0.0185, 0.102), (0.0188, 0.11), (0.0175, 0.112)])                       # the band
    bmesh.ops.create_cone(bm, cap_ends=False, segments=SEG, radius1=0.0262, radius2=0.0262, depth=0.003,
                          matrix=Matrix.Translation((0, 0, 0.1365)))                                  # grille's ring
    c = obj("Chrome", bm, chrome)

    # the ball grille, UV'd so the mesh texture tiles round it
    bm = bmesh.new()
    bm.loops.layers.uv.new("UVMap")   # (calc_uvs only fills a UV layer that's already there)
    bmesh.ops.create_uvsphere(bm, u_segments=32, v_segments=20, radius=0.026, calc_uvs=True,
                              matrix=Matrix.Translation((0, 0, 0.1365)))
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.co.z < 0.112], context='VERTS')   # sits in the band
    g = obj("Grille", bm, grille)

    # the switch on the front (-Y here: Unity's +Z), and the power light above it
    front = -1
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1, matrix=Matrix.Translation((0, front * 0.0148, 0.072)) @ Matrix.Diagonal((0.009, 0.004, 0.02, 1)))
    bmesh.ops.create_cube(bm, size=1, matrix=Matrix.Translation((0, front * 0.0172, 0.0765)) @ Matrix.Diagonal((0.006, 0.0025, 0.007, 1)))   # the knob, up: on
    s = obj("Switch", bm, switch, smooth=False)
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=2, radius=0.0022, matrix=Matrix.Translation((0, front * 0.0158, 0.0875)))
    l = obj("Light", bm, light)

    root = bpy.data.objects.new("mic", None)
    bpy.context.scene.collection.objects.link(root)
    for o in (h, c, g, s, l):
        o.parent = root
    return root, (h, c, g, s, l)


def textures(n=512, wires=8):
    """The woven wire mesh as one tile: wires along U and V going over and under each other, the dark foam behind."""
    t = np.arange(n) / n * wires                       # wire coordinate across the tile
    u, v = np.meshgrid(t, t)
    def wire(x, other):
        d = np.abs((x % 1.0) - 0.5) * 2                 # 0 on a wire's centre line, 1 halfway between
        prof = np.sqrt(np.clip(1 - (d / 0.42) ** 2, 0, 1))   # round wire, 42% of the pitch wide
        weave = 0.5 + 0.5 * np.cos(math.pi * (np.floor(x) + other))   # over, under, over...
        return prof * (0.65 + 0.35 * weave)
    hu, hv = wire(u, v), wire(v, u + 0.5)
    height = np.maximum(hu, hv)
    on = (height > 0.02).astype(float)
    shade = 0.45 + 0.55 * height                       # the wires' rounded tops catch more light
    albedo = np.stack([0.06 + on * (shade * 0.78 - 0.06)] * 3, -1)
    albedo[..., 2] += on * 0.02
    dy, dx = np.gradient(height * 6.0)
    nrm = np.stack([-dx, dy, np.ones_like(dx)], -1)   # tangent space, +Y up the texture (Unity's convention): rows run down
    nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True)
    metal = np.stack([on, on, on, 0.25 + on * 0.35], -1)   # R metallic, A smoothness
    return albedo, nrm * 0.5 + 0.5, metal


def save_png(name, rgb):
    h, w = rgb.shape[:2]
    a = rgb if rgb.shape[2] == 4 else np.concatenate([rgb, np.ones((h, w, 1))], -1)
    img = bpy.data.images.new(name, w, h, alpha=True, float_buffer=False)
    img.colorspace_settings.name = "Non-Color" if "normal" in name or "metal" in name else "sRGB"
    img.pixels = np.flipud(np.clip(a, 0, 1)).ravel().tolist()
    img.filepath_raw = os.path.join(OUT, name + ".png")
    img.file_format = "PNG"
    img.save()


bpy.ops.wm.read_factory_settings(use_empty=True)
os.makedirs(OUT, exist_ok=True)
root, parts = build()
albedo, normal, metal = textures()
save_png("mic_mesh_albedo", albedo)
save_png("mic_mesh_normal", normal)
save_png("mic_mesh_metal", metal)
for o in (root,) + parts:
    o.select_set(True)
bpy.context.view_layer.objects.active = root
bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, "mic.fbx"), use_selection=True, object_types={'MESH', 'EMPTY'},
                         apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y', bake_space_transform=True,
                         mesh_smooth_type='FACE')
print("EXPORTED mic ->", OUT)
