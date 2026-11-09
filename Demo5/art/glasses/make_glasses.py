"""The five novelty glasses (black wayfarers, pink hearts, gold stars, purple shutter shades, brown round specs), built
low-poly and faceted like the concept art, at real size, as FBX in unity/Assets/Models/Glasses/<Name>.fbx.

Run with Blender (a flatpak here; give it an absolute path):
    flatpak run org.blender.Blender -b --python "$PWD/art/glasses/make_glasses.py"
    ... -- --inspect      renders a preview of each (three-quarter view) to art/glasses/previews, no export

Each is sized for the robot's standard head (visor 0.17 m wide, head 0.22 m): the front about 0.22 m across, the
temples running back along the head's sides. The pivot is the middle of the lenses on the frame's back face's plane
(the Eyes spot sits 2 cm in front of the visor), front toward Unity's +Z, up +Y. Every piece is a closed solid, so
nothing is see-through from behind; the facets are flat-shaded.
Built here as x right, y up, z forward (toward the viewer), mapped to Blender's x, -y, z on the way out.
"""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Vector

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "unity", "Assets", "Models", "Glasses")
PREVIEWS = os.path.join(ROOT, "art", "glasses", "previews")
INSPECT = "--inspect" in sys.argv

DEPTH = 0.011        # frame thickness, front to back
CHAMFER = 0.0022     # the bevel round the frame's front edges (the facets that catch the light)
TEMPLE_LEN = 0.135   # back along the head


def srgb(*c):
    return tuple(x ** 2.2 for x in c) + (1.0,)


def material(name, colour, rough=0.55, metal=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs["Base Color"].default_value = srgb(*colour)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = metal
    m.diffuse_color = srgb(*colour)
    return m


# ---------------------------------------------------------------- 2D outlines

def area(poly):
    return sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(poly, poly[1:] + poly[:1])) / 2


def ccw(poly):
    return poly if area(poly) > 0 else poly[::-1]


def offset(poly, d, cap=2.4):
    """The outline pushed out by d (in by -d), mitred at the corners (a sharp point's mitre capped at cap x d)."""
    out, n = [], len(poly)
    for i in range(n):
        p0, p1, p2 = Vector(poly[i - 1]), Vector(poly[i]), Vector(poly[(i + 1) % n])
        e1, e2 = (p1 - p0).normalized(), (p2 - p1).normalized()
        n1, n2 = Vector((e1.y, -e1.x)), Vector((e2.y, -e2.x))   # outward, for a counter-clockwise outline
        m = (n1 + n2)
        m = m.normalized() if m.length > 1e-6 else n1
        k = min(cap, 1 / max(0.2, m.dot(n1)))
        q = p1 + m * d * k
        out.append((q.x, q.y))
    return out


def moved(poly, dx, dy=0.0, sx=1.0):
    return [(x * sx + dx, y + dy) for x, y in poly]


def mirrored(poly):
    return ccw([(-x, y) for x, y in poly])


def lens_pair(right, cx):
    """The right lens's outline (centred on 0) at +cx, and its mirror image at -cx."""
    r = ccw(moved(right, cx))
    return r, mirrored(r)


# ---------------------------------------------------------------- solids

class Builder:
    def __init__(self, name):
        self.bm = bmesh.new()
        self.name = name
        self.mats = []

    def slot(self, mat):
        if mat not in self.mats:
            self.mats.append(mat)
        return self.mats.index(mat)

    def v(self, x, y, z):
        return self.bm.verts.new((x, -z, y))   # (x right, y up, z forward) -> Blender

    def face(self, verts, mat):
        try:
            f = self.bm.faces.new(verts)
            f.material_index = self.slot(mat)
            return f
        except ValueError:
            return None

    def bridge(self, a, b, mat):
        n = len(a)
        for i in range(n):
            self.face([a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]], mat)

    def frame(self, inner, width, mat, depth=DEPTH, z0=0.0, chamfer=CHAMFER):
        """A rim round a lens opening: `width` wide, beveled on the front edges, closed at the back."""
        inner = ccw(inner)
        outer = offset(inner, width)
        back, front = z0, z0 + depth
        c = min(chamfer, width / 3)
        rings = [
            (inner, back), (inner, front - c), (offset(inner, c), front),
            (offset(inner, width - c), front), (outer, front - c), (outer, back),
        ]
        loops = [[self.v(x, y, z) for x, y in poly] for poly, z in rings]
        for a, b in zip(loops, loops[1:]):
            self.bridge(b, a, mat)
        self.bridge(loops[0], loops[-1], mat)   # the back face
        return outer

    def lens(self, poly, mat, z, bulge=0.0025, thick=0.002):
        """A pane filling the opening: a fan to a centre a little proud of its edge (the low-poly facets), front and back."""
        poly = ccw(poly)
        cx = sum(p[0] for p in poly) / len(poly)
        cy = sum(p[1] for p in poly) / len(poly)
        ring_f = [self.v(x, y, z) for x, y in poly]
        ring_b = [self.v(x, y, z - thick) for x, y in poly]
        cf = self.v(cx, cy, z + bulge)
        cb = self.v(cx, cy, z - thick - bulge * 0.4)
        n = len(poly)
        for i in range(n):
            self.face([ring_f[i], ring_f[(i + 1) % n], cf], mat)
            self.face([ring_b[(i + 1) % n], ring_b[i], cb], mat)
        self.bridge(ring_b, ring_f, mat)

    def box(self, x0, x1, y0, y1, z0, z1, mat, chamfer=0.0):
        """An axis-aligned block (its front edges beveled by `chamfer`)."""
        c = chamfer
        back = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
        if c > 0:
            front_in = [(x0 + c, y0 + c), (x1 - c, y0 + c), (x1 - c, y1 - c), (x0 + c, y1 - c)]
            a = [self.v(x, y, z0) for x, y in back]
            b = [self.v(x, y, z1 - c) for x, y in back]
            f = [self.v(x, y, z1) for x, y in front_in]
            self.bridge(b, a, mat)
            self.bridge(f, b, mat)
            self.face(f, mat)
            self.face(a[::-1], mat)
        else:
            a = [self.v(x, y, z0) for x, y in back]
            b = [self.v(x, y, z1) for x, y in back]
            self.bridge(b, a, mat)
            self.face(b, mat)
            self.face(a[::-1], mat)

    def bar(self, p, q, half_w, half_h, mat):
        """A block from p to q (3D points, here-space), half_w across (sideways) and half_h tall."""
        p, q = Vector(p), Vector(q)
        d = (q - p).normalized()
        side = d.cross(Vector((0, 1, 0))).normalized() * half_w
        up = Vector((0, half_h, 0))
        corners = [-side - up, side - up, side + up, -side + up]
        a = [self.v(*(p + c)) for c in corners]
        b = [self.v(*(q + c)) for c in corners]
        self.bridge(a, b, mat)
        self.face(a, mat)
        self.face(b[::-1], mat)

    def temples(self, xo, y, mat, flare=0.012, half_w=0.0032, half_h=0.0042):
        """The arms: a hinge block at the frame's outer edges, then back along the head, flaring out round it."""
        for s in (1, -1):
            x = s * xo
            self.box(min(x, x - s * 0.008), max(x, x - s * 0.008), y - 0.006, y + 0.006, -0.012, 0.004, mat)
            self.bar((x - s * 0.004, y, -0.004), (x + s * flare * 0.7, y, -0.04), half_w, half_h, mat)
            self.bar((x + s * flare * 0.7, y, -0.04), (x + s * flare, y - 0.004, -TEMPLE_LEN), half_w, half_h, mat)

    def finish(self):
        bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
        me = bpy.data.meshes.new(self.name)
        self.bm.to_mesh(me)
        self.bm.free()
        for m in self.mats:
            me.materials.append(m)
        for p in me.polygons:
            p.use_smooth = False
        ob = bpy.data.objects.new(self.name, me)
        bpy.context.scene.collection.objects.link(ob)
        return ob


def xspan(poly, y):
    """Where a horizontal line at y crosses the outline: (left, right)."""
    xs = []
    for (x0, y0), (x1, y1) in zip(poly, poly[1:] + poly[:1]):
        if (y0 - y) * (y1 - y) <= 0 and y0 != y1:
            xs.append(x0 + (y - y0) / (y1 - y0) * (x1 - x0))
    return (min(xs), max(xs)) if len(xs) >= 2 else None


# ---------------------------------------------------------------- the five

def black_sunglasses():
    frame = material("Black Frame", (0.05, 0.05, 0.055), 0.45)
    lens = material("Smoke Lens", (0.16, 0.16, 0.17), 0.18)
    b = Builder("Black_Sunglasses")
    # a wayfarer: a wide flat top, the outer corner high, the bottom swept in toward the nose
    right = [(-0.036, 0.022), (0.0, 0.025), (0.036, 0.026), (0.043, 0.016), (0.041, -0.002), (0.034, -0.017),
             (0.018, -0.026), (-0.006, -0.027), (-0.026, -0.022), (-0.036, -0.010), (-0.038, 0.006)]
    cx = 0.052
    for poly in lens_pair(right, cx):
        b.frame(poly, 0.012, frame)
        b.lens(poly, lens, DEPTH * 0.55)
    b.box(-0.016, 0.016, 0.012, 0.031, 0.0, DEPTH, frame, CHAMFER)   # the solid bridge under the brow
    b.temples(cx + 0.043 + 0.01, 0.019, frame)
    return b.finish(), "a pair of black sunglasses"


def pink_heart_glasses():
    frame = material("Coral Frame", (0.92, 0.44, 0.42), 0.5)
    lens = material("Pink Lens", (0.96, 0.62, 0.66), 0.2)
    b = Builder("Pink_Heart_Glasses")
    # the classic heart curve, 18 points (low-poly), 0.078 m wide, lobes filled out a little
    pts = []
    for i in range(18):
        t = 2 * math.pi * i / 18
        pts.append((16 * math.sin(t) ** 3, 13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t)))
    s = 0.078 / 32
    right = [(x * s * 1.06, (y + 2.5) * s * 0.95) for x, y in pts]
    cx = 0.054
    for poly in lens_pair(right, cx):
        b.frame(poly, 0.0088, frame)
        b.lens(poly, lens, DEPTH * 0.55)
    b.box(-0.015, 0.015, 0.006, 0.014, 0.002, DEPTH - 0.002, frame, 0.0015)   # the bridge across the lobes
    b.temples(cx + 0.041 + 0.007, 0.016, frame)
    return b.finish(), "pink heart-shaped glasses"


def gold_star_glasses():
    frame = material("Gold Frame", (0.95, 0.72, 0.18), 0.4, 0.15)
    lens = material("Slate Lens", (0.22, 0.25, 0.34), 0.2)
    b = Builder("Gold_Star_Glasses")
    R, r = 0.044, 0.022
    right = [((R if i % 2 == 0 else r) * math.cos(math.pi / 2 + i * math.pi / 5),
              (R if i % 2 == 0 else r) * math.sin(math.pi / 2 + i * math.pi / 5)) for i in range(10)]
    cx = 0.055
    for poly in lens_pair(right, cx):
        b.frame(poly, 0.0085, frame)
        b.lens(poly, lens, DEPTH * 0.55)
    side_y = R * math.sin(math.radians(18))   # the stars' side points, where the bridge joins them
    b.box(-0.016, 0.016, side_y - 0.011, side_y - 0.003, 0.002, DEPTH - 0.002, frame, 0.0015)
    b.temples(cx + R * math.cos(math.radians(18)) + 0.004, side_y - 0.004, frame)
    return b.finish(), "gold star-shaped glasses"


def purple_shutter_shades():
    frame = material("Purple Frame", (0.58, 0.32, 0.82), 0.5)
    b = Builder("Purple_Shutter_Shades")
    # one wide visor shape per side, squared off at the top, swept down and out, slats across the openings
    right = [(-0.040, 0.021), (0.0, 0.022), (0.046, 0.022), (0.051, 0.012), (0.049, -0.004), (0.040, -0.019),
             (0.022, -0.027), (0.0, -0.028), (-0.020, -0.024), (-0.033, -0.013), (-0.040, 0.002)]
    cx = 0.052
    polys = lens_pair(right, cx)
    for poly in polys:
        b.frame(poly, 0.0095, frame)
    # the brow runs straight across the top, over the nose
    b.box(-0.022, 0.022, 0.012, 0.0315, 0.0, DEPTH, frame, CHAMFER)
    # the shutters: flat slats across each opening, a little back from the front
    for poly in polys:
        for k in range(6):
            y = 0.016 - k * 0.0078
            span = xspan(poly, y)
            if not span:
                continue
            x0, x1 = span
            b.box(x0 - 0.002, x1 + 0.002, y - 0.0021, y + 0.0021, 0.0015, DEPTH - 0.0025, frame, 0.0008)
    b.temples(cx + 0.051 + 0.008, 0.018, frame)
    return b.finish(), "purple shutter shades"


def brown_intellectual_glasses():
    frame = material("Brown Frame", (0.46, 0.27, 0.16), 0.5)
    lens = material("Clear Lens", (0.86, 0.87, 0.88), 0.08, 0.1)
    b = Builder("Brown_Intellectual_Glasses")
    # a round-ish lens with flats (11 sides), a touch wider than tall and fuller at the top
    right = []
    for i in range(11):
        a = math.pi / 2 + 2 * math.pi * i / 11
        x, y = math.cos(a), math.sin(a)
        right.append((0.036 * x, 0.031 * y + (0.003 if y > 0 else 0)))
    cx = 0.052
    for poly in lens_pair(right, cx):
        b.frame(poly, 0.0085, frame)
        b.lens(poly, lens, DEPTH * 0.55, bulge=0.002)
    b.box(-0.019, 0.019, 0.010, 0.020, 0.001, DEPTH - 0.001, frame, 0.0018)   # the keyhole bridge, high
    b.temples(cx + 0.036 + 0.008, 0.016, frame)
    return b.finish(), "brown round intellectual glasses"


GLASSES = [black_sunglasses, pink_heart_glasses, gold_star_glasses, purple_shutter_shades, brown_intellectual_glasses]


def bounds(ob):
    pts = [ob.matrix_world @ v.co for v in ob.data.vertices]
    return (Vector([min(p[i] for p in pts) for i in range(3)]), Vector([max(p[i] for p in pts) for i in range(3)]))


def preview(ob, name):
    os.makedirs(PREVIEWS, exist_ok=True)
    lo, hi = bounds(ob)
    c, size = (lo + hi) / 2, max(hi - lo)
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'MATERIAL'
    sc.display.shading.show_cavity = True
    sc.render.resolution_x, sc.render.resolution_y = 640, 400
    sc.world = bpy.data.worlds.new("w")
    cd = bpy.data.cameras.new("cam")
    cd.type = 'ORTHO'
    cd.ortho_scale = size * 1.15
    cam = bpy.data.objects.new("cam", cd)
    sc.collection.objects.link(cam)
    sc.camera = cam
    cam.location = c + Vector((-0.45, -1.0, 0.35)).normalized() * size * 4   # three-quarter, from the front left, above
    cam.rotation_euler = (c - cam.location).to_track_quat('-Z', 'Y').to_euler()
    sc.render.filepath = os.path.join(PREVIEWS, name + ".png")
    bpy.ops.render.render(write_still=True)


for make in GLASSES:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    ob, description = make()
    lo, hi = bounds(ob)
    if INSPECT:
        preview(ob, ob.name)
        continue
    os.makedirs(OUT, exist_ok=True)
    out = os.path.join(OUT, ob.name + ".fbx")
    bpy.ops.object.select_all(action='DESELECT')
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.export_scene.fbx(filepath=out, use_selection=True, object_types={'MESH'}, apply_scale_options='FBX_SCALE_ALL',
                             axis_forward='-Z', axis_up='Y', bake_space_transform=True, mesh_smooth_type='FACE')
    print(f"EXPORTED {ob.name} ({description}): {tuple(round(x, 3) for x in (hi - lo))} m, {len(ob.data.polygons)} faces -> {out}")
