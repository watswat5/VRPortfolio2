"""A clown wig worn as a hat: a low-poly rainbow afro, a cloud of faceted puffs over a core, cut off flat underneath so
it sits on top of the head like the other hats. Exported to unity/Assets/Models/clown_wig/clown_wig.fbx.

Run with Blender (a flatpak here; give it an absolute path):
    flatpak run org.blender.Blender -b --python "$PWD/art/hats/make_clown_wig.py"
    ... -- --inspect      renders a preview (front, three-quarter, underneath) to art/hats/previews, no export

As convert_hats.py leaves the hats: sized for the robot's standard head (0.22 m wide), front toward Unity +Z (Blender -Y),
the pivot in the middle of the flat base (the point that sits on top of the head). The rainbow runs round it in six
wedges (red at the front). Flat-shaded; the base is a solid disc, so nothing is see-through from below.
"""
import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Vector

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "unity", "Assets", "Models", "clown_wig", "clown_wig.fbx")
PREVIEWS = os.path.join(ROOT, "art", "hats", "previews")
INSPECT = "--inspect" in sys.argv

WIDTH = 0.36          # across, puffs and all (a big clown afro on a 0.22 m head)
HEIGHT = 0.23         # from the base to the top
SEED = 7
RAINBOW = [(0.93, 0.16, 0.18), (0.98, 0.52, 0.10), (0.99, 0.86, 0.15), (0.20, 0.75, 0.30), (0.17, 0.45, 0.92), (0.55, 0.25, 0.80)]
BASE = (0.22, 0.12, 0.26)   # underneath: a dark felt


def srgb(c):
    return tuple(x ** 2.2 for x in c) + (1.0,)


def material(name, colour, rough=0.85):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs["Base Color"].default_value = srgb(colour)
    bsdf.inputs["Roughness"].default_value = rough
    m.diffuse_color = srgb(colour)
    return m


def wedge(p):
    """Which rainbow colour a point is in: by its angle round the up axis, red centred on the front (-Y)."""
    a = math.atan2(p.x, -p.y) % (2 * math.pi)
    return int((a + math.pi / 6) / (math.pi / 3)) % 6


def blob(bm, centre, radius, squash, rng, subdiv=2, jitter=0.12):
    """A faceted puff: an icosphere, its corners pushed in and out a little, squashed upright by `squash`."""
    geom = bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=radius)
    verts = geom["verts"]
    for v in verts:
        v.co *= 1 + rng.uniform(-jitter, jitter)
        v.co.z *= squash
        v.co += centre
    return verts


def build():
    rng = random.Random(SEED)
    mats = [material(f"Wig {n}", c) for n, c in zip(["Red", "Orange", "Yellow", "Green", "Blue", "Purple"], RAINBOW)]
    base_mat = material("Wig Base", BASE, 0.95)
    bm = bmesh.new()

    rx, lift = WIDTH / 2 - 0.06, 0.095   # the shell the puffs sit on: a ball, its middle well over the base, so it bulges
    rz = HEIGHT - lift - 0.055           # out over a narrower base like an afro
    blob(bm, Vector((0, 0, lift)), 1.0, 1.0, rng, subdiv=2, jitter=0.05)   # the core, filling the gaps
    core = list(bm.verts)
    for v in core:
        v.co = Vector((v.co.x * rx, v.co.y * rx, (v.co.z - lift) * rz + lift))

    n = 40   # puffs, spread evenly over the shell (a Fibonacci spiral), none far below the base
    golden = math.pi * (3 - math.sqrt(5))
    for i in range(n * 2):
        z = 1 - 2 * (i + 0.5) / (n * 2)
        if z < -0.75:
            continue
        r, a = math.sqrt(1 - z * z), i * golden
        d = Vector((r * math.cos(a), r * math.sin(a), z))
        centre = Vector((d.x * rx, d.y * rx, d.z * rz + lift)) * rng.uniform(0.97, 1.05)
        size = rng.uniform(0.058, 0.074) * (0.85 if z > 0.8 else 0.8 if z < -0.4 else 1.0)
        blob(bm, centre, size, rng.uniform(0.85, 1.0), rng, subdiv=1 if rng.random() < 0.4 else 2)

    # cut flat at the base and close it with a disc
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=(0, 0, 0), plane_no=(0, 0, 1),
                           clear_inner=True)
    for f in bm.faces:
        f.material_index = wedge(f.calc_center_median())
    reach = max((Vector((v.co.x, v.co.y)).length for v in bm.verts if abs(v.co.z) < 1e-4), default=WIDTH / 2) - 0.003   # (tucked in: no rim showing)
    ring = [bm.verts.new((reach * math.cos(2 * math.pi * k / 24), reach * math.sin(2 * math.pi * k / 24), 0.0))
            for k in range(24)]
    disc = bm.faces.new(ring[::-1])   # facing down
    disc.material_index = 6

    me = bpy.data.meshes.new("clown_wig")
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    for m in mats + [base_mat]:
        me.materials.append(m)
    ob = bpy.data.objects.new("clown_wig", me)
    bpy.context.scene.collection.objects.link(ob)
    # the base's disc faces down; everything else out from the middle
    for p in me.polygons:
        p.use_smooth = False
    return ob


def bounds(ob):
    pts = [ob.matrix_world @ v.co for v in ob.data.vertices]
    return Vector([min(p[i] for p in pts) for i in range(3)]), Vector([max(p[i] for p in pts) for i in range(3)])


def preview(ob):
    os.makedirs(PREVIEWS, exist_ok=True)
    lo, hi = bounds(ob)
    c, size = (lo + hi) / 2, max(hi - lo)
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'MATERIAL'
    sc.render.resolution_x = sc.render.resolution_y = 400
    sc.world = bpy.data.worlds.new("w")
    cd = bpy.data.cameras.new("cam")
    cd.type = 'ORTHO'
    cd.ortho_scale = size * 1.2
    cam = bpy.data.objects.new("cam", cd)
    sc.collection.objects.link(cam)
    sc.camera = cam
    shots = []
    for tag, d in (("front", (0, -1, 0.05)), ("three_quarter", (-0.7, -1, 0.45)), ("under", (0.3, -0.4, -1))):
        cam.location = c + Vector(d).normalized() * size * 4
        cam.rotation_euler = (c - cam.location).to_track_quat('-Z', 'Y').to_euler()
        path = os.path.join(PREVIEWS, f"clown_wig_{tag}.png")
        sc.render.filepath = path
        bpy.ops.render.render(write_still=True)
        shots.append(path)
    return shots


bpy.ops.wm.read_factory_settings(use_empty=True)
wig = build()
lo, hi = bounds(wig)
if INSPECT:
    preview(wig)
else:
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    wig.select_set(True)
    bpy.context.view_layer.objects.active = wig
    bpy.ops.export_scene.fbx(filepath=OUT, use_selection=True, object_types={'MESH'}, apply_scale_options='FBX_SCALE_ALL',
                             axis_forward='-Z', axis_up='Y', bake_space_transform=True, mesh_smooth_type='FACE')
    print(f"EXPORTED clown_wig: {tuple(round(x, 3) for x in (hi - lo))} m, {len(wig.data.polygons)} faces -> {OUT}")
