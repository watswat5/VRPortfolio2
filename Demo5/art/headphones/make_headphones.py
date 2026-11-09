"""The DJ's headphones: low-poly closed-back DJ cans, for Unity: unity/Assets/Models/headphones/headphones.fbx.

Run with Blender (a flatpak here; give it an absolute path):
    flatpak run org.blender.Blender -b --python "$PWD/art/headphones/make_headphones.py"

Parts, one object per material (Freestyle > Build Headphones gives each its URP material):
  Shell    the cups' backs and the headband's outside: matte black plastic
  Pad      the ear cushions and the band's underside padding: dark leatherette
  Metal    the sliders and the swivel yokes: brushed steel
  Accent   a ring round each cup's back (a colour to pick in Unity)
Sized for the DJ (Rocketbox Male_Adult_11): ears 0.092 m either side of the head's middle, the top of the head 0.118 m
above the ears. The pivot is midway between the ears; +Y up, +Z the way the face looks, X across (Unity's axes).
"""
import math
import os

import bmesh
import bpy
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "unity", "Assets", "Models", "headphones")
EAR = 0.092          # from the head's middle to each ear
TOP = 0.118          # from the ears up to the top of the head
CUP_R = 0.043        # the cups' radius
SEG = 12             # round the cups


def material(name, colour, rough, metal=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    b.inputs["Base Color"].default_value = tuple(c ** 2.2 for c in colour) + (1,)
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metal
    return m


def obj(name, bm, mat, smooth=False):
    for f in bm.faces:
        f.smooth = smooth
    me = bpy.data.meshes.new(name)
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    o = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(o)
    return o


def U(x, y, z):
    """A point given in Unity's axes (x across, y up, z forward) in Blender's (the exporter turns Blender's -Y into
    Unity's +Z and its Z into Unity's Y)."""
    return Vector((x, -z, y))


def disc(bm, centre_x, radius, depth, segs=SEG, cap_out=True, cap_in=True):
    """A cylinder across the head (its axis along X), from centre_x - depth/2 to + depth/2."""
    s = 1 if centre_x > 0 else -1
    rings = []
    for dx in (-depth / 2, depth / 2):
        rings.append([bm.verts.new(U(centre_x + dx, radius * math.cos(2 * math.pi * k / segs), radius * math.sin(2 * math.pi * k / segs)))
                      for k in range(segs)])
    a, b = rings
    for k in range(segs):
        f = bm.faces.new([a[k], a[(k + 1) % segs], b[(k + 1) % segs], b[k]])
    if cap_in:
        bm.faces.new(a[::-1] if s > 0 else a)
    if cap_out:
        bm.faces.new(b if s > 0 else b[::-1])
    return rings


def ring(bm, centre_x, r_in, r_out, depth, segs=SEG):
    """A flat ring (a washer) across the head: the cushion and the accent."""
    vs = {}
    for i, dx in enumerate((-depth / 2, depth / 2)):
        for j, r in enumerate((r_in, r_out)):
            vs[i, j] = [bm.verts.new(U(centre_x + dx, r * math.cos(2 * math.pi * k / segs), r * math.sin(2 * math.pi * k / segs)))
                        for k in range(segs)]
    for k in range(segs):
        n = (k + 1) % segs
        for (i, j), (i2, j2) in (((0, 1), (1, 1)), ((1, 0), (0, 0)), ((1, 1), (1, 0)), ((0, 0), (0, 1))):
            bm.faces.new([vs[i, j][k], vs[i, j][n], vs[i2, j2][n], vs[i2, j2][k]])


def band(bm, half_w, base, height, z, width, thick, segs=16):
    """The headband: a rectangle (width along Z, thick outward) swept along a half-ellipse over the head, its ends at
    (+-half_w, base), its top at base + height."""
    loops = []
    for i in range(segs + 1):
        a = math.pi * i / segs
        x, y = -half_w * math.cos(a), base + height * math.sin(a)
        nx, ny = -height * math.cos(a), half_w * math.sin(a)     # outward, away from the ellipse's middle
        n = math.hypot(nx, ny)
        nx, ny = nx / n, ny / n
        loops.append([bm.verts.new(U(px, py, z + dz)) for px, py in ((x, y), (x + nx * thick, y + ny * thick))
                      for dz in (-width / 2, width / 2)])
    for a, b in zip(loops, loops[1:]):
        bm.faces.new([a[0], b[0], b[1], a[1]])
        bm.faces.new([a[3], b[3], b[2], a[2]])
        bm.faces.new([a[2], b[2], b[0], a[0]])
        bm.faces.new([a[1], b[1], b[3], a[3]])
    bm.faces.new([loops[0][0], loops[0][1], loops[0][3], loops[0][2]])
    bm.faces.new([loops[-1][2], loops[-1][3], loops[-1][1], loops[-1][0]])


def box(bm, centre, size, rot=Matrix.Identity(4)):
    bmesh.ops.create_cube(bm, size=1, matrix=Matrix.Translation(U(*centre)) @ rot @ Matrix.Diagonal((size[0], size[2], size[1], 1)))


def build():
    shell = material("Headphones Shell", (0.05, 0.05, 0.055), 0.55)
    pad = material("Headphones Pad", (0.11, 0.11, 0.12), 0.8)
    metal = material("Headphones Metal", (0.7, 0.7, 0.72), 0.3, 1.0)
    accent = material("Headphones Accent", (0.95, 0.15, 0.2), 0.4)

    pad_w, cup_d = 0.016, 0.026
    cx = EAR + pad_w + cup_d / 2 + 0.002            # the cups' middles, out from the ears
    top = TOP + 0.008                               # the band's underside just clears the hair
    base = CUP_R + 0.024                            # the band's ends, above the yokes (the sliders below them)

    bm = bmesh.new()
    for s in (-1, 1):
        disc(bm, s * cx, CUP_R, cup_d, cap_in=False)                   # the cup's shell
    band(bm, cx, base, top + 0.008 - base, -0.004, 0.034, 0.009)       # the band
    o_shell = obj("Shell", bm, shell)

    bm = bmesh.new()
    for s in (-1, 1):
        ring(bm, s * (EAR + pad_w / 2 + 0.002), 0.022, CUP_R + 0.002, pad_w)   # the cushions, against the ears
    band(bm, cx - 0.008, base, top - base, -0.004, 0.026, 0.008)            # the padding under the band
    o_pad = obj("Pad", bm, pad, smooth=True)

    bm = bmesh.new()
    for s in (-1, 1):
        x = s * cx
        box(bm, (x, base - 0.004, -0.004), (0.007, 0.03, 0.014))                  # the slider out of the band's end
        box(bm, (x, CUP_R + 0.006, -0.004), (0.006, 0.006, 2 * CUP_R + 0.018))    # the yoke over the cup
        for z in (-1, 1):                                                          # its arms down to the pivots
            box(bm, (x, (CUP_R + 0.006) / 2, z * (CUP_R + 0.006) - 0.004), (0.006, CUP_R + 0.012, 0.006))
    o_metal = obj("Metal", bm, metal)

    bm = bmesh.new()
    for s in (-1, 1):
        ring(bm, s * (cx + cup_d / 2 + 0.0015), CUP_R * 0.55, CUP_R * 0.75, 0.003)
    o_acc = obj("Accent", bm, accent)

    root = bpy.data.objects.new("headphones", None)
    bpy.context.scene.collection.objects.link(root)
    parts = (o_shell, o_pad, o_metal, o_acc)
    for o in parts:
        o.parent = root
    return root, parts


bpy.ops.wm.read_factory_settings(use_empty=True)
os.makedirs(OUT, exist_ok=True)
root, parts = build()
for o in parts:   # outward faces (the sweeps' winding varies)
    bpy.context.view_layer.objects.active = o
    o.select_set(True)
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.mesh.normals_make_consistent(inside=False)
bpy.ops.object.mode_set(mode='OBJECT')
root.select_set(True)
bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, "headphones.fbx"), use_selection=True, object_types={'MESH', 'EMPTY'},
                         apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y', bake_space_transform=True,
                         mesh_smooth_type='FACE')
print("EXPORTED headphones ->", OUT)
