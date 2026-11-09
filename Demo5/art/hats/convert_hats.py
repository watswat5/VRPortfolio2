"""The hats in unity/Assets/Models, converted to FBX at real size for the scene (metres), each beside its source:
unity/Assets/Models/<hat>/<hat>.fbx, with its textures embedded.

Run with Blender (a flatpak here; give it an absolute path):
    flatpak run org.blender.Blender -b --python "$PWD/art/hats/convert_hats.py"
    ... -- --inspect      renders previews (front, side, top) of each hat as imported to art/hats/previews, no export

Each hat is joined into one mesh, turned so it sits upright with its front (brim, peak) toward Unity's +Z, scaled so
it's WIDTH wide (side to side), and its pivot put at the middle of its opening: the point that sits on top of the
head (an Accessory's attach point, if the hat has no other).
Blender 5 has no Collada importer, so the two .dae hats (Assimp exports: one polylist of positions, normals and UVs)
are read here.
"""
import math
import os
import sys
import xml.etree.ElementTree as ET

import bmesh
import bpy
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MODELS = os.path.join(ROOT, "unity", "Assets", "Models")
PREVIEWS = os.path.join(ROOT, "art", "hats", "previews")

# hat: source file, texture for its colour (if the importer doesn't hook one up), width in metres, extra turn about up
# (degrees) to bring its front to -Y in Blender (Unity +Z), and how far its opening sits up from its lowest point (as a
# share of its height: brims hang below the opening)
HATS = {   # sized for the robot's standard head (0.22 m wide)
    "tophat": ("tophat/tophat.blend.blend", None, 0.30, 0, 0.0),
    "leaderhut": ("leaderhut/model.dae", "leaderhut/textures/DefaultMaterial_albedo.jpg", 0.34, 0, 0.1),   # a tricorne
    "hiphop_hat": ("hiphop_hat/Hat_Hip-Hop.obj", "hiphop_hat/Hat_COL.png", 0.24, 0, 0.04),
    "military_hat": ("military_hat/model.dae", "military_hat/textures/lambert1_albedo.jpg", 0.24, 0, 0.08),
    "cowboy_hat": ("cowboy_hat/cowboy hat1.glb", None, 0.42, 0, 0.12),
}


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def read_dae(path):
    """An Assimp-written Collada file: its one polylist (or triangles) as a mesh object, in metres, Z up."""
    ns = {"c": "http://www.collada.org/2005/11/COLLADASchema"}
    root = ET.parse(path).getroot()
    unit = float(root.find("c:asset/c:unit", ns).get("meter", "1"))
    floats = {s.get("id"): [float(x) for x in s.find("c:float_array", ns).text.split()]
              for s in root.iter("{%s}source" % ns["c"]) if s.find("c:float_array", ns) is not None}
    stride = {s.get("id"): int(s.find("c:technique_common/c:accessor", ns).get("stride"))
              for s in root.iter("{%s}source" % ns["c"]) if s.find("c:technique_common/c:accessor", ns) is not None}
    mesh_el = root.find(".//c:mesh", ns)
    vertices = mesh_el.find("c:vertices", ns)
    pos_src = vertices.find("c:input[@semantic='POSITION']", ns).get("source")[1:]
    prim = mesh_el.find("c:polylist", ns)
    if prim is None:
        prim = mesh_el.find("c:triangles", ns)
    inputs = prim.findall("c:input", ns)
    n_in = max(int(i.get("offset")) for i in inputs) + 1
    uv_in = next((i for i in inputs if i.get("semantic") == "TEXCOORD"), None)
    # the UVs may be per corner (in the polylist) or per vertex (in <vertices>, indexed like the positions)
    uv_vert = vertices.find("c:input[@semantic='TEXCOORD']", ns) if uv_in is None else None
    idx = [int(x) for x in prim.find("c:p", ns).text.split()]
    vc = prim.find("c:vcount", ns)
    counts = [int(x) for x in vc.text.split()] if vc is not None else [3] * (len(idx) // (3 * n_in))
    P, sp = floats[pos_src], stride[pos_src]
    verts = [Vector((P[i * sp], -P[i * sp + 2], P[i * sp + 1])) * unit for i in range(len(P) // sp)]   # Y up -> Z up
    bm = bmesh.new()
    bv = [bm.verts.new(v) for v in verts]
    uv_layer = bm.loops.layers.uv.new("UVMap")
    uv_src = uv_in if uv_in is not None else uv_vert
    T = floats[uv_src.get("source")[1:]] if uv_src is not None else None
    ts = stride[uv_src.get("source")[1:]] if uv_src is not None else 2
    k = 0
    for c in counts:
        corner = [idx[(k + j) * n_in:(k + j + 1) * n_in] for j in range(c)]
        k += c
        try:
            f = bm.faces.new([bv[cc[0]] for cc in corner])
        except ValueError:
            continue   # a duplicate face
        if T is not None:
            off = int(uv_in.get("offset")) if uv_in is not None else 0   # per vertex: the vertex index
            for loop, cc in zip(f.loops, corner):
                t = cc[off]
                loop[uv_layer].uv = (T[t * ts], T[t * ts + 1])
    mesh = bpy.data.meshes.new(os.path.basename(os.path.dirname(path)))
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(mesh.name, mesh)
    bpy.context.collection.objects.link(obj)
    return obj


def import_hat(rel):
    path = os.path.join(MODELS, rel)
    ext = os.path.splitext(path)[1].lower()
    before = set(bpy.data.objects)
    if ext == ".dae":
        read_dae(path)
    elif ext == ".obj":
        bpy.ops.wm.obj_import(filepath=path)
    elif ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == ".blend":
        with bpy.data.libraries.load(path, link=False) as (src, dst):
            dst.objects = src.objects
        for o in dst.objects:
            if o is not None and o.type == 'MESH':
                bpy.context.collection.objects.link(o)
    meshes = [o for o in bpy.data.objects if o not in before and o.type == 'MESH']
    # one object: transforms applied, joined
    bpy.ops.object.select_all(action='DESELECT')
    for o in meshes:
        if o.name not in bpy.context.collection.objects:
            bpy.context.collection.objects.link(o)
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    if len(meshes) > 1:
        bpy.ops.object.join()
    hat = bpy.context.view_layer.objects.active
    for o in list(bpy.data.objects):   # drop everything else (empties, cameras, lights from the source)
        if o is not hat:
            bpy.data.objects.remove(o, do_unlink=True)
    return hat


def bounds(obj):
    vs = [obj.matrix_world @ v.co for v in obj.data.vertices]
    lo = Vector((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs)))
    hi = Vector((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs)))
    return lo, hi


MAX_TEXTURE = 1024   # a hat's colour texture is cut down to this (px) if bigger


def texture(obj, rel):
    """Gives the hat one material with this colour texture (the source's may point at the wrong image), cut down to
    MAX_TEXTURE as <name>_<size>.jpg beside the source if it's bigger."""
    mat = bpy.data.materials.new(obj.name)
    nodes = mat.node_tree.nodes
    tex = nodes.new("ShaderNodeTexImage")
    path = os.path.join(MODELS, rel)
    image = bpy.data.images.load(path)
    if max(image.size) > MAX_TEXTURE:
        image.scale(MAX_TEXTURE, MAX_TEXTURE)
        small = f"{os.path.splitext(path)[0]}_{MAX_TEXTURE}.jpg"
        image.filepath_raw = small
        image.file_format = 'JPEG'
        image.save()
        image.filepath = small
    tex.image = image
    mat.node_tree.links.new(tex.outputs["Color"], nodes["Principled BSDF"].inputs["Base Color"])
    obj.data.materials.clear()
    obj.data.materials.append(mat)


def exportable(obj, folder):
    """FBX carries a Principled BSDF's colour and textures only, from image files: a material built from other shaders
    (velvet, gloss) becomes a Principled one in that shader's colour; packed images are written out beside the hat."""
    for slot in obj.material_slots:
        m = slot.material
        if not m:
            continue
        nodes = m.node_tree.nodes
        if not any(n.type == 'BSDF_PRINCIPLED' for n in nodes):
            colour = next((n.inputs["Color"].default_value[:] for n in nodes if "Color" in n.inputs and n.type.startswith('BSDF')),
                          m.diffuse_color[:])
            nodes.clear()
            bsdf = nodes.new("ShaderNodeBsdfPrincipled")
            bsdf.inputs["Base Color"].default_value = colour
            bsdf.inputs["Roughness"].default_value = 0.6
            out = nodes.new("ShaderNodeOutputMaterial")
            m.node_tree.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    for im in bpy.data.images:
        if im.packed_file is not None:
            name = os.path.splitext(os.path.basename(im.name))[0] + ".png"
            im.filepath_raw = os.path.join(folder, name)
            im.file_format = 'PNG'
            im.save()
            im.unpack(method='REMOVE')
            im.filepath = os.path.join(folder, name)


def preview(obj, name):
    """Front (looking along +Y), side (along -X) and top, orthographic, workbench, into one strip."""
    os.makedirs(PREVIEWS, exist_ok=True)
    lo, hi = bounds(obj)
    c, size = (lo + hi) / 2, max(hi - lo) * 1.3
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.color_type = 'TEXTURE'
    scene.render.resolution_x = scene.render.resolution_y = 400
    cam_data = bpy.data.cameras.new("cam")
    cam_data.type = 'ORTHO'
    cam_data.ortho_scale = size
    cam_data.clip_start, cam_data.clip_end = size * 0.01, size * 10
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    views = {"front": ((0, -1, 0), (90, 0, 0)), "side": ((1, 0, 0), (90, 0, 90)), "top": ((0, 0, 1), (0, 0, 0))}
    for view, (d, rot) in views.items():
        cam.location = c + Vector(d) * size * 2
        cam.rotation_euler = [math.radians(a) for a in rot]
        scene.render.filepath = os.path.join(PREVIEWS, f"{name}_{view}.png")
        bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(cam, do_unlink=True)


def convert(name, src, tex, width, turn, rise):
    clear()
    hat = import_hat(src)
    hat.name = hat.data.name = name
    if tex:
        texture(hat, tex)
    if "--inspect" in sys.argv:
        lo, hi = bounds(hat)
        print(f"HAT {name}: size {tuple(round(x, 4) for x in (hi - lo))} lo {tuple(round(x, 4) for x in lo)} "
              f"verts {len(hat.data.vertices)} mats {[s.material.name for s in hat.material_slots if s.material]}")
        preview(hat, name)
        return
    hat.data.transform(Matrix.Rotation(math.radians(turn), 4, 'Z'))
    lo, hi = bounds(hat)
    s = width / max(hi.x - lo.x, 1e-6)
    pivot = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z + rise * (hi.z - lo.z)))
    hat.data.transform(Matrix.Diagonal(Vector((s, s, s, 1))) @ Matrix.Translation(-pivot))
    for p in hat.data.polygons:
        p.use_smooth = True
    exportable(hat, os.path.join(MODELS, name))
    out = os.path.join(MODELS, name, name + ".fbx")
    bpy.ops.export_scene.fbx(filepath=out, object_types={'MESH'}, apply_scale_options='FBX_SCALE_ALL',
                             axis_forward='-Z', axis_up='Y', bake_space_transform=True, path_mode='COPY',
                             embed_textures=True, mesh_smooth_type='FACE')
    lo, hi = bounds(hat)
    print(f"EXPORTED {name}: {tuple(round(x, 3) for x in (hi - lo))} m -> {out}")


for name, args in HATS.items():
    convert(name, *args)
