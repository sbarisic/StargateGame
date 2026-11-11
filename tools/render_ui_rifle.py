"""Reproducible HUD silhouette from the shipped rifle source, without altering it.
Run using Blender --background --python tools/render_ui_rifle.py.
"""

import bpy, pathlib, hashlib, json
from mathutils import Vector

root = pathlib.Path(__file__).resolve().parents[1]
source = (
    root
    / "assets_itch/RetroWeaponPack/RetroWeaponsPack/Guns/Rifle_01/Fbx_Files/Rifle_01.fbx"
)
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(source))
objects = [o for o in bpy.context.scene.objects if o.type == "MESH"]
points = [o.matrix_world @ Vector(p) for o in objects for p in o.bound_box]
lo = Vector([min(p[i] for p in points) for i in range(3)])
hi = Vector([max(p[i] for p in points) for i in range(3)])
extent = hi - lo
centre = (hi + lo) / 2
# Longest dimension is the barrel; smallest dimension is the side-view axis.
long = max(range(3), key=lambda i: extent[i])
thin = min(range(3), key=lambda i: extent[i])
normal = Vector([1 if i == thin else 0 for i in range(3)])
camera_data = bpy.data.cameras.new("HUD side projection")
camera = bpy.data.objects.new("HUD side projection", camera_data)
bpy.context.scene.collection.objects.link(camera)
camera.location = centre + normal * max(extent) * 4
camera.rotation_euler = (centre - camera.location).to_track_quat("-Z", "Y").to_euler()
camera_data.type = "ORTHO"
camera_data.ortho_scale = max(extent) * 1.1
material = bpy.data.materials.new("Silhouette")
material.use_nodes = True
nodes = material.node_tree.nodes
nodes.clear()
output = nodes.new("ShaderNodeOutputMaterial")
emission = nodes.new("ShaderNodeEmission")
emission.inputs[0].default_value = (1, 1, 1, 1)
material.node_tree.links.new(emission.outputs[0], output.inputs["Surface"])
for o in objects:
    o.data.materials.clear()
    o.data.materials.append(material)
scene = bpy.context.scene
scene.camera = camera
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = 512
scene.render.resolution_y = 256
scene.render.resolution_percentage = 100
scene.render.film_transparent = True
scene.view_settings.view_transform = "Standard"
destination = root / "assets/gui/rifle-silhouette.png"
scene.render.filepath = str(destination)
bpy.ops.render.render(write_still=True)
(root / "assets/gui/rifle-silhouette.provenance.json").write_text(
    json.dumps(
        {
            "source": str(source.relative_to(root)),
            "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "generator": "tools/render_ui_rifle.py",
            "license": "See existing RetroWeaponPack attribution in THIRD-PARTY-NOTICES.md",
        },
        indent=2,
    )
)
