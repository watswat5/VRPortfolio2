"""A generic spray paint can, low-poly, real size, exported to unity/Assets/Models/spray_can/spray_can.fbx.

Run with Blender (a flatpak here; give it an absolute path):
    flatpak run org.blender.Blender -b --python "$PWD/art/spraycan/make_spray_can.py"
    ... -- --inspect      renders a preview to art/spraycan/previews, no export

Three objects under the can (its pivot at the middle of its base, the nozzle's opening toward Unity +Z):
  Body    the metal: the can, its rolled rims, the domed shoulder and the cup round the actuator
  Label   the printed band round the middle: its own object with its own material ("Label"),
          so a can's colour is whatever material you give the Label (SprayCan reads it and sprays that colour)
  Nozzle  the white actuator on top; its pivot is the opening, where the spray comes out (SprayCan's nozzle)
6.6 cm across and 19 cm tall, as a standard 400 ml can without its cap. Flat-shaded ends, smooth sides.
"""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "unity", "Assets", "Models", "spray_can", "spray_can.fbx")
PREVIEWS = os.path.join(ROOT, "art", "spraycan", "previews")
INSPECT = "--inspect" in sys.argv

SEG = 20                 # round the can
R = 0.033                # radius
H = 0.165                # the straight part
LABEL = (0.03, 0.135)    # the label band, bottom to top
RIM = 0.0015             # how far the rolled rims stand out


def srgb(c):
    return tuple(x ** 2.2 for x in c) + (1.0,)


def material(name, colour, rough, metal=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs["Base Color"].default_value = srgb(colour)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = metal
    m.diffuse_color = srgb(colour)
    return m


def lathe(bm, profile, seg=SEG, cap_bottom=False, cap_top=False, smooth=True):
    """A solid of revolution round Blender's Z from (radius, height) points, bottom to top."""
    rings = []
    for r, z in profile:
        rings.append([bm.verts.new((r * math.cos(2 * math.pi * k / seg), r * math.sin(2 * math.pi * k / seg), z))
                      for k in range(seg)])
    faces = []
    for a, b in zip(rings, rings[1:]):
        for k in range(seg):
            f = bm.faces.new([a[k], a[(k + 1) % seg], b[(k + 1) % seg], b[k]])
            f.smooth = smooth
            faces.append(f)
    if cap_bottom:
        f = bm.faces.new(rings[0][::-1])
        f.smooth = False
    if cap_top:
        f = bm.faces.new(rings[-1])
        f.smooth = False
    return faces


def obj(name, bm, mat):
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def build():
    metal = material("Can Metal", (0.78, 0.79, 0.81), 0.3, 0.9)
    label = material("Label", (0.92, 0.15, 0.2), 0.45)   # a placeholder red: give each can its own
    plastic = material("Nozzle Plastic", (0.95, 0.95, 0.94), 0.5)
    lo, hi = LABEL

    # the metal: base rim, the bare can below and above the label, the domed shoulder up to the valve cup
    bm = bmesh.new()
    lathe(bm, [(R - 0.006, 0.0), (R, 0.003), (R + RIM, 0.005), (R, 0.007), (R, lo)], cap_bottom=True)
    lathe(bm, [(R, hi), (R, H - 0.004), (R + RIM, H - 0.002), (R, H)])
    dome = [(R * math.cos(a), H + 0.022 * math.sin(a)) for a in (i * math.pi / 2 / 5 for i in range(6))]
    dome = [(max(r, 0.0125), z) for r, z in dome]   # stops at the valve cup
    lathe(bm, [(R, H)] + dome[1:] + [(0.0125, H + 0.024), (0.0135, H + 0.026), (0.0125, H + 0.028), (0.011, H + 0.028)],
          cap_top=True)
    body = obj("Body", bm, metal)

    # the label: a band a hair proud of the can, closed at its edges
    bm = bmesh.new()
    lathe(bm, [(R - 0.0005, lo), (R + 0.0006, lo + 0.0006), (R + 0.0006, hi - 0.0006), (R - 0.0005, hi)])
    lab = obj("Label", bm, label)

    # the actuator: a white button on the valve with the opening facing front (-Y here, Unity +Z)
    top = H + 0.028
    bm = bmesh.new()
    lathe(bm, [(0.0105, top), (0.0105, top + 0.014), (0.0098, top + 0.0155)], seg=14, cap_bottom=True, cap_top=True)
    # the spout: a little block out of its front
    z0, z1 = top + 0.004, top + 0.011
    y0, y1 = -0.0095, -0.0135
    geom = bmesh.ops.create_cube(bm, size=1.0)
    for v in geom["verts"]:
        v.co = Vector((v.co.x * 0.008, (y0 + y1) / 2 + v.co.y * abs(y1 - y0), (z0 + z1) / 2 + v.co.z * (z1 - z0)))
    nozzle = obj("Nozzle", bm, plastic)
    for p in nozzle.data.polygons:
        p.use_smooth = False
    # its pivot at the opening (SprayCan sprays from the Nozzle's position along the can's front)
    opening = Vector((0, y1, (z0 + z1) / 2))
    nozzle.data.transform(Matrix.Translation(-opening))
    nozzle.location = opening

    root = bpy.data.objects.new("spray_can", None)
    bpy.context.scene.collection.objects.link(root)
    for o in (body, lab, nozzle):
        o.parent = root
    return root, (body, lab, nozzle)


def preview(parts):
    os.makedirs(PREVIEWS, exist_ok=True)
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'MATERIAL'
    sc.render.resolution_x, sc.render.resolution_y = 400, 600
    sc.world = bpy.data.worlds.new("w")
    cd = bpy.data.cameras.new("cam")
    cd.type = 'ORTHO'
    cd.ortho_scale = 0.24
    cam = bpy.data.objects.new("cam", cd)
    sc.collection.objects.link(cam)
    sc.camera = cam
    c = Vector((0, 0, 0.1))
    for tag, d in (("front", (0, -1, 0.15)), ("three_quarter", (0.8, -1, 0.5))):
        cam.location = c + Vector(d).normalized()
        cam.rotation_euler = (c - cam.location).to_track_quat('-Z', 'Y').to_euler()
        sc.render.filepath = os.path.join(PREVIEWS, f"spray_can_{tag}.png")
        bpy.ops.render.render(write_still=True)


bpy.ops.wm.read_factory_settings(use_empty=True)
root, parts = build()
if INSPECT:
    preview(parts)
else:
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    for o in (root,) + parts:
        o.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.fbx(filepath=OUT, use_selection=True, object_types={'MESH', 'EMPTY'},
                             apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
                             bake_space_transform=True, mesh_smooth_type='FACE')
    print(f"EXPORTED spray_can -> {OUT}")
