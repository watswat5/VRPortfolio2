"""A small boombox, low-poly, real size, exported to unity/Assets/Models/boombox/boombox.fbx.

Run with Blender (a flatpak here; give it an absolute path):
    flatpak run org.blender.Blender -b --python "$PWD/art/boombox/make_boombox.py"
    ... -- --inspect      renders a preview to art/boombox/previews, no export

It sits on the dressing room's spray table under the song list and plays the picked beat (Venue/Boombox.cs).
Four objects under the root (its pivot at the middle of its base, the front toward Unity +Z):
  Body     everything that doesn't move: the bevelled case, the speakers' surrounds, the tape deck and its reels,
           the piano keys, the carry handle, the folded antenna and the feet (materials Shell, Trim, Grille, Rubber)
  Cone L   the left speaker's cone and dust cap; its pivot on the case's front at the speaker's middle, so
           scaling it along its forward pushes it out of the box (Boombox thumps it on the beat)
  Cone R   the right one
  Display  the strip above the tape deck (material "Display": Boombox lights it while it plays)
36 cm wide, 14 cm tall (17.6 cm with the handle), 10 cm deep.
"""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "unity", "Assets", "Models", "boombox", "boombox.fbx")
PREVIEWS = os.path.join(ROOT, "art", "boombox", "previews")
INSPECT = "--inspect" in sys.argv

W, H, D = 0.36, 0.14, 0.10     # the case (Blender: x across, -y the front, z up)
FRONT = -D / 2
SPK_X, SPK_Z = W / 2 - 0.07, 0.066   # the speakers' middles
SURROUND = 0.056                     # the speakers' outer radius
SEG = 24


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


def obj(name, bm, mat, smooth=False):
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    for p in me.polygons:
        p.use_smooth = smooth
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def box(name, centre, size, mat, bevel=0.0, segments=2):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((centre[0] + v.co.x * size[0], centre[1] + v.co.y * size[1], centre[2] + v.co.z * size[2]))
    if bevel > 0:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=segments, profile=0.5, affect='EDGES')
    return obj(name, bm, mat, smooth=bevel > 0)


def lathe(name, profile, mat, at, axis='front', seg=SEG, cap_first=False, cap_last=False, smooth=True):
    """A solid of revolution from (radius, height) points: round Blender's Z, then turned so that height runs out of
    the front (-Y) or along +X (axis='x'), and moved to `at`."""
    bm = bmesh.new()
    rings = [[bm.verts.new((r * math.cos(2 * math.pi * k / seg), r * math.sin(2 * math.pi * k / seg), h)) for k in range(seg)]
             for r, h in profile]
    for a, b in zip(rings, rings[1:]):
        for k in range(seg):
            bm.faces.new([a[k], a[(k + 1) % seg], b[(k + 1) % seg], b[k]])
    if cap_first:
        bm.faces.new(rings[0][::-1])
    if cap_last:
        bm.faces.new(rings[-1])
    turn = Matrix.Rotation(math.radians(90), 4, 'X') if axis == 'front' else Matrix.Rotation(math.radians(90), 4, 'Y')
    bmesh.ops.transform(bm, matrix=Matrix.Translation(at) @ turn, verts=bm.verts)
    return obj(name, bm, mat, smooth)


def join(name, parts):
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    ob.name = ob.data.name = name
    return ob


def build():
    shell = material("Shell", (0.17, 0.17, 0.19), 0.45, 0.2)
    trim = material("Trim", (0.72, 0.73, 0.76), 0.3, 0.9)
    grille = material("Grille", (0.05, 0.05, 0.055), 0.6)
    rubber = material("Rubber", (0.03, 0.03, 0.03), 0.85)
    display = material("Display", (0.05, 0.12, 0.12), 0.2)

    body = [box("Case", (0, 0, H / 2), (W, D, H), shell, bevel=0.012, segments=3)]
    # a silver band round the front, just inside its bevel
    body.append(box("Front Band", (0, FRONT + 0.0005, H - 0.016), (W - 0.03, 0.002, 0.006), trim))
    body.append(box("Front Band", (0, FRONT + 0.0005, 0.011), (W - 0.03, 0.002, 0.004), trim))

    cones = []
    for side, x in (("L", -SPK_X), ("R", SPK_X)):
        at = Vector((x, FRONT, SPK_Z))
        # the surround: a silver lip standing out of the front, rolled over toward the cone
        body.append(lathe("Surround", [(SURROUND, -0.002), (SURROUND + 0.001, 0.006), (SURROUND - 0.002, 0.011),
                                       (SURROUND - 0.007, 0.012), (SURROUND - 0.01, 0.009)], trim, at))
        # the cone: a shallow dish, closed behind (hidden in the surround), and a domed dust cap
        r0 = SURROUND - 0.01
        cap = [(0.016 * math.cos(a), 0.0035 + 0.006 * math.sin(a)) for a in (i * math.pi / 2 / 4 for i in range(5))]
        cone = lathe(f"Cone {side}", [(r0, 0.0), (r0, 0.009), (0.032, 0.0055), (0.017, 0.0035)] + cap,
                     grille, Vector((0, 0, 0)), cap_first=True)
        cone.data.materials.append(rubber)   # the dust cap in rubber: the faces nearest the middle
        for p in cone.data.polygons:
            if p.center.length < 0.0165 and p.center.y < -0.004:
                p.material_index = 1
        cone.location = at   # its pivot: the speaker's middle on the case's front
        cones.append(cone)

    # the tape deck: a dark well with a window, two reels behind it, and the display strip above
    body.append(box("Deck", (0, FRONT - 0.0015, 0.058), (0.1, 0.004, 0.068), grille, bevel=0.003))
    body.append(box("Window", (0, FRONT - 0.0036, 0.062), (0.064, 0.0012, 0.026), trim))
    for x in (-0.017, 0.017):
        body.append(lathe("Reel", [(0.0075, 0.0), (0.0075, 0.0014), (0.003, 0.0014)], grille,
                          Vector((x, FRONT - 0.0042, 0.062)), seg=12, cap_last=True, smooth=False))
    disp = box("Display", (0, FRONT - 0.0018, 0.113), (0.1, 0.004, 0.016), display)

    # piano keys along the top, toward the back of the deck
    for i in range(5):
        body.append(box("Key", (-0.03 + i * 0.015, 0.015, H + 0.003), (0.012, 0.022, 0.008), trim if i < 4 else rubber,
                        bevel=0.0015, segments=1))
    # the carry handle: two posts and a rounded bar over the top
    hz, hy = H + 0.03, -0.012
    for x in (-1, 1):
        body.append(box("Handle Post", (x * (W / 2 - 0.035), hy, H + 0.016), (0.014, 0.014, 0.032), trim, bevel=0.003))
    body.append(lathe("Handle", [(0.0, -(W / 2 - 0.04)), (0.0075, -(W / 2 - 0.042)), (0.0075, W / 2 - 0.042), (0.0, W / 2 - 0.04)],
                      rubber, Vector((0, hy, hz)), axis='x', seg=12))
    # the antenna folded along the back of the top, its tip ball at the left
    body.append(lathe("Antenna", [(0.0, -0.15), (0.0022, -0.149), (0.0022, 0.12), (0.003, 0.121), (0.003, 0.14), (0.0, 0.141)],
                      trim, Vector((0, D / 2 - 0.012, H + 0.004)), axis='x', seg=8))
    body.append(box("Antenna Foot", (0.14, D / 2 - 0.012, H + 0.002), (0.012, 0.012, 0.006), shell, bevel=0.002))
    # rubber feet
    for x in (-1, 1):
        for y in (-1, 1):
            body.append(box("Foot", (x * (W / 2 - 0.03), y * (D / 2 - 0.02), 0.002), (0.025, 0.018, 0.004), rubber,
                            bevel=0.0015, segments=1))
    # the case sits on its feet
    for p in body + cones + [disp]:
        p.location.z += 0.004

    body = join("Body", body)
    root = bpy.data.objects.new("boombox", None)
    bpy.context.scene.collection.objects.link(root)
    for o in [body, disp] + cones:
        o.parent = root
    return root, (body, disp, *cones)


def preview(parts):
    os.makedirs(PREVIEWS, exist_ok=True)
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'MATERIAL'
    sc.render.resolution_x, sc.render.resolution_y = 800, 500
    sc.world = bpy.data.worlds.new("w")
    cd = bpy.data.cameras.new("cam")
    cd.type = 'ORTHO'
    cd.ortho_scale = 0.44
    cam = bpy.data.objects.new("cam", cd)
    sc.collection.objects.link(cam)
    sc.camera = cam
    c = Vector((0, 0, 0.085))
    for tag, d in (("front", (0, -1, 0.1)), ("three_quarter", (0.7, -1, 0.6)), ("back", (-0.5, 1, 0.7))):
        cam.location = c + Vector(d).normalized()
        cam.rotation_euler = (c - cam.location).to_track_quat('-Z', 'Y').to_euler()
        sc.render.filepath = os.path.join(PREVIEWS, f"boombox_{tag}.png")
        bpy.ops.render.render(write_still=True)


bpy.ops.wm.read_factory_settings(use_empty=True)
root, parts = build()
if INSPECT:
    preview(parts)
else:
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    bpy.ops.object.select_all(action='DESELECT')
    for o in (root,) + parts:
        o.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.fbx(filepath=OUT, use_selection=True, object_types={'MESH', 'EMPTY'},
                             apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
                             bake_space_transform=True, mesh_smooth_type='FACE')
    print(f"EXPORTED boombox -> {OUT}")
