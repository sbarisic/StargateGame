"""Blender-only visual review of exported skinning, independent of source rigs.

blender -b --python tools/render_viewmodel_review.py -- --clip reload --time .5
Time is a normalized clip position. Output stays under artifacts/review-evidence.
"""
import argparse
import gzip
import json
import math
from pathlib import Path
import sys

import bpy
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--clip", default="idle")
parser.add_argument("--time", type=float, default=.35)
parser.add_argument("--asset", type=Path, default=ROOT / "assets/characters/retro-rifle.sga.gz")
parser.add_argument("--output", type=Path, default=ROOT / "artifacts/review-evidence/arms2")
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
data = json.loads(gzip.decompress(args.asset.read_bytes()))
clip = next(c for c in data["clips"] if c["name"] == args.clip)
frame = round(args.time * (clip["frames"] - 1))
local = np.asarray(clip["matrices"]).reshape(clip["frames"], -1, 4, 4)[frame]
world = np.empty_like(local)
for i, parent in enumerate(data["parents"]):
    world[i] = local[i] if parent < 0 else world[parent] @ local[i]
skin = world @ np.asarray(data["inverseBind"]).reshape(-1, 4, 4)
bpy.ops.wm.read_factory_settings(use_empty=True)
for part in data["parts"]:
    positions = np.asarray(part["positions"]).reshape(-1, 3)
    joints = np.asarray(part["joints"], dtype=int).reshape(-1, 4)
    weights = np.asarray(part["weights"]).reshape(-1, 4)
    transforms = sum(skin[joints[:, j]] * weights[:, j, None, None] for j in range(4))
    vertices = np.einsum("nij,nj->ni", transforms, np.column_stack((positions, np.ones(len(positions)))))[:, :3]
    normal = np.asarray(part["normals"]).reshape(-1, 3)
    normal = np.einsum("nij,nj->ni", np.linalg.inv(transforms[:, :3, :3]).transpose(0, 2, 1), normal)
    normal /= np.linalg.norm(normal, axis=1)[:, None]
    mesh = bpy.data.meshes.new(part["name"])
    mesh.from_pydata(vertices.tolist(), [], np.asarray(part["indices"]).reshape(-1, 3).tolist())
    mesh.update()
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    mesh.normals_split_custom_set_from_vertices(normal.tolist())
    uv = np.asarray(part["uvs"]).reshape(-1, 2)
    layer = mesh.uv_layers.new()
    for loop in mesh.loops:
        layer.data[loop.index].uv = uv[loop.vertex_index]
    obj = bpy.data.objects.new(part["name"], mesh)
    bpy.context.collection.objects.link(obj)
    material = bpy.data.materials.new(part["name"])
    material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    shader = nodes.get("Principled BSDF")

    def texture(field):
        node = nodes.new("ShaderNodeTexImage")
        node.image = bpy.data.images.load(str(ROOT / "assets/characters" / part[field]), check_existing=True)
        if field != "albedo":
            node.image.colorspace_settings.name = "Non-Color"
        return node

    links.new(texture("albedo").outputs["Color"], shader.inputs["Base Color"])
    shader.inputs["Roughness"].default_value = .64
    if "normal" in part:
        normal_map = nodes.new("ShaderNodeNormalMap")
        links.new(texture("normal").outputs["Color"], normal_map.inputs["Color"])
        links.new(normal_map.outputs["Normal"], shader.inputs["Normal"])
    if "metallicRoughness" in part:
        separate = nodes.new("ShaderNodeSeparateColor")
        links.new(texture("metallicRoughness").outputs["Color"], separate.inputs["Color"])
        links.new(separate.outputs["Green"], shader.inputs["Roughness"])
    mesh.materials.append(material)

scene = bpy.context.scene
camera = bpy.data.objects.new("Viewmodel camera", bpy.data.cameras.new("Viewmodel camera"))
bpy.context.collection.objects.link(camera)
scene.camera = camera
camera.data.type = "PERSP"
camera.data.sensor_fit = "VERTICAL"
camera.data.sensor_height = 32
camera.data.lens = 16 / math.tan(math.radians(45 if args.clip == "aim" else 60) / 2)
camera.data.clip_start = .015
for name, location, energy, size in [("Sunward", (-1, 2, 1), 180, 2), ("Fill", (2, 0, 1), 45, 3)]:
    lamp = bpy.data.lights.new(name, "AREA")
    lamp.energy, lamp.shape, lamp.size = energy, "DISK", size
    obj = bpy.data.objects.new(name, lamp)
    bpy.context.collection.objects.link(obj)
    obj.location = location
    from mathutils import Vector
    obj.rotation_euler = (Vector((0, -.2, -.6)) - obj.location).to_track_quat("-Z", "Y").to_euler()
scene.world = bpy.data.worlds.new("Review grey")
scene.world.color = (.16, .19, .23)
scene.render.engine = "CYCLES"
scene.cycles.samples = 16
scene.cycles.use_denoising = True
scene.render.resolution_x, scene.render.resolution_y = 960, 540
scene.render.resolution_percentage = 100
scene.view_settings.view_transform = "AgX"
args.output.mkdir(parents=True, exist_ok=True)
scene.render.filepath = str(args.output / f"{args.clip}-{frame:03}.png")
bpy.ops.render.render(write_still=True)
