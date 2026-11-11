"""Blender stage of import_building_kit.py. Read-only source scenes; native metre grid."""

import bpy, json, math, struct, sys, hashlib
from pathlib import Path
from mathutils import Vector, Matrix

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "assets/models"
UI = ROOT / "assets/gui/building"
RAW = ROOT / "artifacts/building-kit"
for p in (OUT, UI, RAW):
    p.mkdir(parents=True, exist_ok=True)
preview = Path(sys.argv[sys.argv.index("--") + 1])
mega = ROOT / "assets_itch/Medieval Village MegaKit[Standard]/glTF"
NAMES = [
    "Wall_UnevenBrick_Straight",
    "Wall_UnevenBrick_Door_Flat",
    "Wall_UnevenBrick_Window_Wide_Flat",
    "Wall_Plaster_Straight",
    "Floor_Brick",
    "Floor_WoodDark",
    "Door_1_Flat",
    "DoorFrame_Flat_Brick",
    "Stair_Interior_Simple",
    "Stairs_Exterior_Straight",
    "Roof_Modular_RoundTiles",
    "Roof_RoundTiles_6x6",
    "Roof_Front_Brick6",
    "Corner_Exterior_Brick",
    "Window_Wide_Flat1",
    "Stairs_Exterior_Platform",
]
images = {}
records = []


def image_for(socket, seen=None):
    seen = set() if seen is None else seen
    for link in socket.links:
        node = link.from_node
        if node in seen:
            continue
        seen.add(node)
        if node.type == "TEX_IMAGE" and node.image:
            return node.image
        for upstream in node.inputs:
            found = image_for(upstream, seen)
            if found:
                return found
    return None


def raw_image(image):
    if image is None:
        return None
    data = (
        bytes(image.packed_file.data)
        if image.packed_file
        else Path(bpy.path.abspath(image.filepath)).read_bytes()
    )
    digest = hashlib.sha256(data).hexdigest()[:16]
    path = RAW / (digest + ".image")
    path.write_bytes(data)
    images[digest] = {"raw": path.name, "name": image.name}
    return digest


def material_data(mat):
    bs = (
        next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if mat and mat.use_nodes
        else None
    )
    if bs is None:
        return {"color": [0.5, 0.5, 0.5, 1], "roughness": 0.8, "metallic": 0}
    return {
        "color": list(bs.inputs["Base Color"].default_value),
        "roughness": float(bs.inputs["Roughness"].default_value),
        "metallic": float(bs.inputs["Metallic"].default_value),
        "base": raw_image(image_for(bs.inputs["Base Color"])),
        "normal": raw_image(image_for(bs.inputs["Normal"])),
        "rough": raw_image(image_for(bs.inputs["Roughness"])),
        "metal": raw_image(image_for(bs.inputs["Metallic"])),
    }


def export(key, objects):
    parts = []
    collision = []
    points = []
    for o in objects:
        mesh = o.data
        mesh.calc_loop_triangles()
        uv = mesh.uv_layers.active
        for mi in sorted(set(t.material_index for t in mesh.loop_triangles)):
            vertices = []
            for tri in mesh.loop_triangles:
                if tri.material_index != mi:
                    continue
                triangle = []
                for li in tri.loops:
                    loop = mesh.loops[li]
                    p = mesh.vertices[loop.vertex_index].co
                    n = mesh.corner_normals[li].vector
                    tex = uv.data[li].uv if uv else Vector((0, 0))
                    v = (p.x, p.z, -p.y)
                    vertices.append((*v, n.x, n.z, -n.y, tex.x, tex.y))
                    triangle.append(v)
                    points.append(v)
                a, b, c = map(Vector, triangle)
                if (b - a).cross(c - a).length_squared > 1e-12:
                    collision.append(triangle)
            name = f"{key}-{len(parts)}.mesh"
            with (RAW / name).open("wb") as f:
                f.write(b"SGM1" + struct.pack("<I", len(vertices)))
                for v in vertices:
                    f.write(struct.pack("<8f", *v))
            mat = o.data.materials[mi] if mi < len(o.data.materials) else None
            parts.append({"name": o.name, "mesh": name, "material": material_data(mat)})
    lo = [min(p[i] for p in points) for i in range(3)]
    hi = [max(p[i] for p in points) for i in range(3)]
    records.append(
        {"id": key, "min": lo, "max": hi, "parts": parts, "triangles": collision}
    )
    # Every thumbnail is rendered from the same exported geometry, with no scene offsets.
    s = bpy.context.scene
    s.render.engine = "CYCLES"
    s.cycles.samples = 12
    s.cycles.use_denoising = True
    s.render.resolution_x = 256
    s.render.resolution_y = 192
    s.render.resolution_percentage = 100
    s.render.image_settings.file_format = "PNG"
    s.render.film_transparent = True
    s.view_settings.view_transform = "AgX"
    s.world = bpy.data.worlds.new("Thumbnail world")
    s.world.use_nodes = True
    s.world.node_tree.nodes["Background"].inputs[1].default_value = 0.6
    for o in s.objects:
        o.hide_render = o not in objects
    bpy.ops.object.light_add(type="AREA", location=(2, -4, 7))
    light = bpy.context.object
    light.data.energy = 950
    light.data.shape = "DISK"
    light.data.size = 5
    bpy.ops.object.camera_add()
    cam = bpy.context.object
    s.camera = cam
    cam.data.type = "ORTHO"
    pts = [Vector((p[0], -p[2], p[1])) for p in points]
    center = sum(pts, Vector()) / len(pts)
    low = Vector([min(p[i] for p in pts) for i in range(3)])
    high = Vector([max(p[i] for p in pts) for i in range(3)])
    center = (low + high) / 2
    cam.location = center + Vector((1, -1.8, 1.1)).normalized() * max(high - low) * 3
    cam.rotation_euler = (center - cam.location).to_track_quat("-Z", "Y").to_euler()
    bpy.context.view_layer.update()
    inv = cam.matrix_world.inverted()
    cp = [inv @ p for p in pts]
    cam.data.ortho_scale = (
        max(
            max(p.x for p in cp) - min(p.x for p in cp),
            (max(p.y for p in cp) - min(p.y for p in cp)) * 256 / 192,
        )
        * 1.18
    )
    s.render.filepath = str(UI / (key + ".png"))
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(light, do_unlink=True)
    bpy.data.objects.remove(cam, do_unlink=True)
    print("EXPORTED", key, len(collision), flush=True)


for name in NAMES:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(mega / (name + ".gltf")))
    objects = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    for o in objects:
        o.data.transform(o.matrix_world)
        o.parent = None
        o.matrix_world = Matrix.Identity(4)
    export("kit-" + name.lower().replace("_", "-"), objects)
    # The supplied preview used an added foundation. Supply an actual two-metre module.
    if name == "Wall_UnevenBrick_Straight":
        mat = next(
            (m for o in objects for m in o.data.materials if "Brick" in m.name),
            objects[0].data.materials[0],
        )
        bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, -0.5))
        o = bpy.context.object
        o.name = "Stone foundation"
        o.dimensions = (2, 2, 1)
        bpy.ops.object.transform_apply(location=True, rotation=False, scale=True)
        o.data.materials.append(mat)
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.cube_project(cube_size=2)
        bpy.ops.object.mode_set(mode="OBJECT")
        export("kit-stone-foundation", [o])

bpy.ops.wm.open_mainfile(
    filepath=str(preview / "fortification_preview/Kit_Fortification_components.blend")
)
for collection in list(bpy.context.scene.collection.children):
    objects = [o for o in collection.objects if o.type == "MESH"]
    if collection.name not in [
        "solid_wall",
        "firing_window_wall",
        "corner_wall",
        "parapet",
        "landing",
        "stairs",
        "swing_gate",
        "sliding_gate",
        "road_barrier",
        "bollard",
        "gate_control",
    ]:
        continue
    # The preview gallery translations are display offsets. Meshes are already baked.
    for o in objects:
        o.matrix_world = Matrix.Identity(4)
    export("fort-" + collection.name.replace("_", "-"), objects)
(RAW / "export.json").write_text(
    json.dumps({"pieces": records, "images": images}, separators=(",", ":"))
)
