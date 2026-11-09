"""The player robot's parts, low poly and flat shaded, after the concept sheet (Skinny, Standard, Chonk), exported as
one FBX for Unity: unity/Assets/Player/Models/robot_parts.fbx. PlayerBuilder (Freestyle > Build VR Player) puts the
parts on the robot's skeleton.

Run with Blender (a flatpak here):
    flatpak run org.blender.Blender -b --python "$PWD/art/robot/make_robot.py"   (an absolute path: the flatpak runs elsewhere)

Everything is modelled in Unity's space (metres, +Y up, +Z forward, +X the robot's right) and each part's pivot is the
bone it hangs on, so Unity places it with an identity transform:
  Head_<Skinny|Standard|Chonk>_{Shell,Visor,Dark}  pivot at the head bone (base of the skull); the visor's front is
                                                    VISOR_FRONT ahead of it, where the LED panels go
  Head_Round_{Shell,Visor,Dark}                     the round head: a ball with a flat, rounded visor set into its
                                                    face (front at VISOR_FRONT too), round ear pods
  Body_<Skinny|Standard|Chonk>_{Shell,Dark,Light}  pivot at the chest bone
  Limb      an octagonal rod, 1 wide and 2 long along Y, centred (LimbVisual stretches it between two joints)
  Joint     a low-poly ball, 1 across
  Hand_<L|R>_{Palm,Thumb,Cuff}, Finger_Seg         the mitten: pivot at the wrist, fingers along +Z, palm facing -Y;
                                                    the finger is three Finger_Segs (pivot at each knuckle)
Parts are split by material (Shell is paintable, Dark, Visor, Light glow), one object each.
Blender (x, y, z) arrives in Unity as (-x, z, -y): the script converts once, at the end.
"""
import os
import bmesh
import bpy
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "unity", "Assets", "Player", "Models", "robot_parts.fbx")

# head sizes: (width, height, depth), visor (width, height), ear pod radius
HEADS = {
    "Skinny": ((0.15, 0.19, 0.16), (0.115, 0.125), 0.022),
    "Standard": ((0.22, 0.18, 0.19), (0.17, 0.115), 0.034),
    "Chonk": ((0.27, 0.22, 0.22), (0.215, 0.145), 0.044),
}
EYE_Y = 0.10          # the visor's centre, above the head bone
VISOR_FRONT = 0.10    # the visor's front, ahead of the head bone (the LED panels sit just in front of it)
NECK_Y = 0.18         # the neck bone, above the chest bone (PlayerBuilder: 1.50 - 1.32): the heads' necks end here

parts = []            # (name, bmesh)


def part(name):
    bm = bmesh.new()
    parts.append((name, bm))
    return bm


def trs(center, size=(1, 1, 1), rot=Matrix.Identity(4)):
    return Matrix.Translation(Vector(center)) @ rot @ Matrix.Diagonal(Vector((*size, 1)))


def box(bm, center, size, bevel=0.0, segments=2):
    """A box, its edges bevelled (low poly: two segments)."""
    made = bmesh.ops.create_cube(bm, size=1.0, matrix=trs(center, size))["verts"]
    if bevel > 0:
        edges = list({e for v in made for e in v.link_edges})
        bmesh.ops.bevel(bm, geom=edges, offset=bevel, segments=segments, profile=0.5, affect='EDGES')


def cylinder(bm, center, radius, length, axis="y", segments=12):
    rot = {"y": Matrix.Rotation(-1.5708, 4, "X"), "x": Matrix.Rotation(1.5708, 4, "Y"), "z": Matrix.Identity(4)}[axis]
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments, radius1=radius, radius2=radius,
                          depth=length, matrix=trs(center, rot=rot))


def ellipsoid(bm, center, radii, u=14, v=9):
    bmesh.ops.create_uvsphere(bm, u_segments=u, v_segments=v, radius=1.0, matrix=trs(center, radii))


def ball(bm, center, radius):
    bmesh.ops.create_icosphere(bm, subdivisions=1, radius=radius, matrix=trs(center))


def disc(bm, center, radius, facing="z", segments=16):
    """A thin glowing disc facing +Z (forward) or -Y (down)."""
    if facing == "z":
        cylinder(bm, center, radius, 0.004, axis="z", segments=segments)
    else:
        cylinder(bm, center, radius, 0.004, axis="y", segments=segments)


# ---------------------------------------------------------------- heads (pivot: head bone)

for name, ((w, h, d), (vw, vh), ear) in HEADS.items():
    zc = VISOR_FRONT - 0.012 - d / 2                      # the shell's centre: the visor stands 12 mm proud of it
    box(part(f"Head_{name}_Shell"), (0, EYE_Y + 0.005, zc), (w, h, d), bevel=min(w, h, d) * 0.16)
    box(part(f"Head_{name}_Visor"), (0, EYE_Y, VISOR_FRONT - 0.011), (vw, vh, 0.022), bevel=0.009)
    dark = part(f"Head_{name}_Dark")
    for s in (-1, 1):                                      # ear pods
        cylinder(dark, (s * (w / 2 + 0.008), EYE_Y + 0.005, zc), ear, 0.03, axis="x", segments=10)
        cylinder(dark, (s * (w / 2 + 0.022), EYE_Y + 0.005, zc), ear * 0.6, 0.012, axis="x", segments=10)
    cylinder(dark, (0, -0.035, zc + 0.01), 0.024, 0.09, segments=8)   # neck

# the round head: a low-poly ball, the visor a flat rounded slab set into its face (deep enough that its edges meet the
# curve: the LED panels on it stay flat), round ear pods on its sides
ROUND = ((0.24, 0.23, 0.23), (0.165, 0.11), 0.03)
(rw, rh, rd), (rvw, rvh), rear = ROUND
zc = VISOR_FRONT - 0.012 - rd / 2                          # the ball's front 12 mm behind the visor's, as the boxy heads
ellipsoid(part("Head_Round_Shell"), (0, EYE_Y + 0.005, zc), (rw / 2, rh / 2, rd / 2), u=16, v=10)
box(part("Head_Round_Visor"), (0, EYE_Y, VISOR_FRONT - 0.035), (rvw, rvh, 0.07), bevel=0.03, segments=3)
dark = part("Head_Round_Dark")
for s_ in (-1, 1):                                         # ear pods, half sunk into the ball
    cylinder(dark, (s_ * (rw / 2 - 0.006), EYE_Y + 0.005, zc), rear, 0.03, axis="x", segments=12)
    cylinder(dark, (s_ * (rw / 2 + 0.012), EYE_Y + 0.005, zc), rear * 0.55, 0.012, axis="x", segments=12)
cylinder(dark, (0, -0.035, zc + 0.01), 0.024, 0.09, segments=8)   # neck

# ---------------------------------------------------------------- bodies (pivot: chest bone, 1.32 m up)


def neck_joint(bm, top, radius):
    """The head's neck comes down to a ball at the neck bone; a collar holds the ball up off the torso's top. The head
    turns about its own bone, 8 cm above the ball, so its neck swings a little round it; the ball keeps them joined."""
    if NECK_Y - top > 0.005:
        cylinder(bm, (0, (top + NECK_Y) / 2 - 0.005, 0), radius, NECK_Y - top + 0.01, segments=10)
    ball(bm, (0, NECK_Y, 0), 0.033)


# Skinny: a small chest, a bare spine, a little pelvis
s = part("Body_Skinny_Shell")
box(s, (0, 0.04, 0), (0.17, 0.2, 0.12), bevel=0.025)
box(s, (0, -0.33, 0), (0.13, 0.07, 0.1), bevel=0.018)
dk = part("Body_Skinny_Dark")
cylinder(dk, (0, -0.19, 0), 0.02, 0.24, segments=8)                   # spine
cylinder(dk, (0, 0.12, 0), 0.018, 0.3, axis="x", segments=8)          # collarbone, shoulder to shoulder
for y in (-0.12, -0.2, -0.27):                                        # vertebrae
    cylinder(dk, (0, y, 0), 0.03, 0.02, segments=8)
neck_joint(dk, 0.14, 0.022)
lt = part("Body_Skinny_Light")
disc(lt, (0, 0.06, 0.062), 0.02)
disc(lt, (0, -0.368, 0), 0.04, facing="y")                           # the hover glow underneath

# Standard: a chest box, a waist, a pelvis
s = part("Body_Standard_Shell")
box(s, (0, 0.02, 0), (0.3, 0.26, 0.2), bevel=0.04)
box(s, (0, -0.27, 0), (0.22, 0.1, 0.16), bevel=0.025)
dk = part("Body_Standard_Dark")
cylinder(dk, (0, -0.16, 0), 0.07, 0.14, segments=10)                  # waist
for x in (-0.2, 0.2):
    ball(dk, (x, 0.11, 0), 0.045)                                     # shoulder sockets
neck_joint(dk, 0.15, 0.03)
lt = part("Body_Standard_Light")
disc(lt, (0, 0.05, 0.102), 0.028)
disc(lt, (0, -0.322, 0), 0.07, facing="y")

# Chonk: one big round body, a dark band at the bottom
s = part("Body_Chonk_Shell")
ellipsoid(s, (0, -0.12, 0), (0.31, 0.3, 0.26))
dk = part("Body_Chonk_Dark")
cylinder(dk, (0, -0.39, 0), 0.2, 0.06, segments=14)
for x in (-0.33, 0.33):
    ball(dk, (x, 0.05, 0), 0.07)                                      # shoulder sockets
neck_joint(dk, 0.18, 0.035)                                           # (the round top is at the neck: just the ball)
lt = part("Body_Chonk_Light")
disc(lt, (0, 0.0, 0.245), 0.04)
disc(lt, (0, -0.422, 0), 0.12, facing="y")

# ---------------------------------------------------------------- arms

lb = part("Limb")
cylinder(lb, (0, 0, 0), 0.5, 2.0, segments=8)
ball(part("Joint"), (0, 0, 0), 0.5)

# ---------------------------------------------------------------- hands (pivot: wrist; fingers +Z, palm -Y)

PALM = 0.08                                                           # wrist to knuckles
box(part("Finger_Seg"), (0, 0, 0.016), (0.07, 0.024, 0.032), bevel=0.008)
for side, sx in (("L", 1), ("R", -1)):                                 # the thumb is on +X for the left hand
    box(part(f"Hand_{side}_Palm"), (0, 0, PALM / 2 + 0.004), (0.078, 0.032, PALM), bevel=0.01)
    box(part(f"Hand_{side}_Thumb"), (0, 0, 0.022), (0.024, 0.024, 0.044), bevel=0.008)
    cylinder(part(f"Hand_{side}_Cuff"), (0, 0, 0.002), 0.03, 0.022, axis="z", segments=10)

# ---------------------------------------------------------------- export

bpy.ops.wm.read_factory_settings(use_empty=True)
to_blender = Matrix(((-1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))   # Unity (x, y, z) -> Blender
for name, bm in parts:
    bm.transform(to_blender)
    bmesh.ops.reverse_faces(bm, faces=bm.faces)                        # the conversion mirrors: keep faces outward
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    for p in mesh.polygons:
        p.use_smooth = False
    bpy.context.collection.objects.link(bpy.data.objects.new(name, mesh))
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.export_scene.fbx(filepath=OUT, object_types={'MESH'}, apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z',
                         axis_up='Y', bake_space_transform=True, mesh_smooth_type='FACE')
print(f"EXPORTED {len(parts)} parts -> {OUT}")
