"""Render the two Q menu thumbnails. Run with Blender --background --python."""
from pathlib import Path
import bpy
from mathutils import Vector

root = Path(__file__).resolve().parents[1]
for model, source in [('zpm-hub', 'zpm_hub.glb'), ('zpm', 'zpm_module.glb')]:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(root/'assets_itch/custom_models/zpm_hub'/source))
    scene = bpy.context.scene
    points = [obj.matrix_world@Vector(c) for obj in scene.objects if obj.type == 'MESH' for c in obj.bound_box]
    lo = Vector(tuple(min(p[i] for p in points) for i in range(3)))
    hi = Vector(tuple(max(p[i] for p in points) for i in range(3)))
    centre = (lo+hi)*.5; size = max(hi-lo)
    bpy.ops.object.camera_add(location=centre+Vector((1.1,-1.8,1.0))*size)
    camera=bpy.context.object; camera.rotation_euler=(centre-camera.location).to_track_quat('-Z','Y').to_euler()
    camera.data.type='ORTHO'; camera.data.ortho_scale=size*1.35; scene.camera=camera
    bpy.ops.object.light_add(type='AREA',location=centre+Vector((1,-2,3))*size)
    bpy.context.object.data.energy=200*size*size; bpy.context.object.data.size=size*2
    scene.world=bpy.data.worlds.new('World'); scene.world.use_nodes=True
    scene.world.node_tree.nodes['Background'].inputs[1].default_value=.6
    scene.render.engine='CYCLES'; scene.cycles.samples=16; scene.cycles.use_denoising=True
    scene.render.resolution_x=256; scene.render.resolution_y=192; scene.render.resolution_percentage=100
    scene.render.film_transparent=True; scene.render.image_settings.file_format='PNG'; scene.view_settings.view_transform='AgX'
    scene.render.filepath=str(root/'assets/gui/building'/f'{model}.png')
    bpy.ops.render.render(write_still=True)
