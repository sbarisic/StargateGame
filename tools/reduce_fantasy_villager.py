"""Blender offline derivative; leaves downloaded source assets untouched."""

import bpy
import hashlib
import json
from pathlib import Path
import shutil
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "assets_itch/npc/fantasy_villager"
OUT = SOURCE / "optimized_400k"
OUT.mkdir(parents=True, exist_ok=True)
TARGET = 400_000


def triangles(obj):
    obj.data.calc_loop_triangles()
    return len(obj.data.loop_triangles)


def bounds(objects):
    points = [o.matrix_world @ Vector(c) for o in objects for c in o.bound_box]
    return [
        [min(p[i] for p in points) for i in range(3)],
        [max(p[i] for p in points) for i in range(3)],
    ]


def render_preview(path):
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    lo, hi = map(Vector, bounds(meshes))
    root = bpy.data.objects.new("Preview normalization only", None)
    bpy.context.collection.objects.link(root)
    for obj in list(bpy.context.scene.objects):
        if obj != root and obj.parent is None:
            obj.parent = root
    scale = 1.8 / (hi.z - lo.z)
    root.scale = (scale,) * 3
    root.location = Vector((-(lo.x + hi.x) / 2, -(lo.y + hi.y) / 2, -lo.z)) * scale
    bpy.context.view_layer.update()
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 32
    scene.cycles.use_denoising = True
    scene.render.resolution_x = 760
    scene.render.resolution_y = 920
    scene.render.resolution_percentage = 100
    scene.world = bpy.data.worlds.new("Studio")
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes["Background"]
    background.inputs[0].default_value = (0.46, 0.51, 0.60, 1)
    background.inputs[1].default_value = 0.45
    scene.view_settings.view_transform = "AgX"
    bpy.ops.mesh.primitive_plane_add(size=200)
    material = bpy.data.materials.new("Neutral floor")
    material.diffuse_color = (0.115, 0.135, 0.16, 1)
    bpy.context.object.data.materials.append(material)
    for location, power, size in [
        ((-3, -4, 5), 550, 4),
        ((4, -1, 3), 350, 3),
        ((0, 3, 4), 650, 3),
    ]:
        bpy.ops.object.light_add(type="AREA", location=location)
        light = bpy.context.object
        light.data.energy = power
        light.data.shape = "DISK"
        light.data.size = size
        light.rotation_euler = (
            (Vector((0, 0, 1)) - light.location).to_track_quat("-Z", "Y").to_euler()
        )
    bpy.ops.object.camera_add(location=(2.4, -7, 2.35))
    camera = bpy.context.object
    scene.camera = camera
    center = Vector((0, 0, 0.9))
    camera.rotation_euler = (
        (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    )
    camera.data.type = "ORTHO"
    rotation = camera.rotation_euler.to_matrix().transposed()
    points = [
        rotation @ (o.matrix_world @ Vector(c) - center)
        for o in meshes
        for c in o.bound_box
    ]
    width = max(p.x for p in points) - min(p.x for p in points)
    height = max(p.y for p in points) - min(p.y for p in points)
    camera.data.ortho_scale = max(height, width * 920 / 760) * 1.15
    scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)


bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(SOURCE / "scene.gltf"))
meshes = sorted(
    (o for o in bpy.context.scene.objects if o.type == "MESH"), key=lambda o: o.name
)
before = sum(triangles(o) for o in meshes)
original_bounds = bounds(meshes)
small = sum(triangles(o) for o in meshes if triangles(o) < 128)
ratio = (TARGET - small - 20) / (before - small)
parts = []
for obj in meshes:
    count = triangles(obj)
    if count >= 128:
        bpy.context.view_layer.objects.active = obj
        modifier = obj.modifiers.new("400k triangle reduction", "DECIMATE")
        modifier.decimate_type = "COLLAPSE"
        modifier.ratio = ratio
        modifier.use_collapse_triangulate = True
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    parts.append(
        dict(name=obj.name, original_triangles=count, reduced_triangles=triangles(obj))
    )
after = sum(triangles(o) for o in meshes)
assert TARGET - 200 <= after <= TARGET, after
assert all(o.data.uv_layers and len(o.data.materials) for o in meshes)
bpy.ops.file.pack_all()
bpy.ops.wm.save_as_mainfile(filepath=str(OUT / "fantasy_villager_400k.blend"))
bpy.ops.export_scene.gltf(
    filepath=str(OUT / "fantasy_villager_400k.glb"),
    export_format="GLB",
    export_animations=False,
)
shutil.copy2(SOURCE / "license.txt", OUT / "license.txt")
report = dict(
    source="../scene.gltf",
    target_triangles=TARGET,
    original_triangles=before,
    reduced_triangles=after,
    reduction_percent=100 * (1 - after / before),
    parts=parts,
    source_bounds=original_bounds,
    reduced_bounds=bounds(meshes),
    source_sha256={
        str(p.relative_to(SOURCE)): hashlib.sha256(p.read_bytes()).hexdigest()
        for p in sorted(SOURCE.rglob("*"))
        if p.is_file() and OUT not in p.parents
    },
    method="Blender collapse decimation; small meshes preserved; UVs and materials retained",
    blender=bpy.app.version_string,
    rigged=False,
)
# Validate and render the exported file, rather than only the Blender working mesh.
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(OUT / "fantasy_villager_400k.glb"))
imported = [o for o in bpy.context.scene.objects if o.type == "MESH"]
report["reimported_triangles"] = sum(triangles(o) for o in imported)
report["reimported_bounds"] = bounds(imported)
assert report["reimported_triangles"] == after
assert all(o.data.uv_layers and len(o.data.materials) for o in imported)
(OUT / "reduction-report.json").write_text(json.dumps(report, indent=2))
render_preview(OUT / "preview.png")
print(json.dumps(dict(original=before, reduced=after, output=str(OUT))), flush=True)
