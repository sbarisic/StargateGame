"""Blender background asset render; not a game launch or test.
blender -b --python tools/render_lantern_thumbnail.py
"""
from pathlib import Path
import math
import bpy
from mathutils import Vector

root=Path(__file__).resolve().parents[1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(root/'assets_itch/props/wooden_lantern/assets/models/woodenLantern01.glb'))
scene=bpy.context.scene
points=[obj.matrix_world@Vector(c) for obj in scene.objects if obj.type=='MESH' for c in obj.bound_box]
lo=Vector(tuple(min(p[i] for p in points) for i in range(3)));hi=Vector(tuple(max(p[i] for p in points) for i in range(3)))
centre=(lo+hi)*.5;size=max(hi-lo)
marker=Vector((0,.0068,.235))
rings=[(-.022,0.),(-.018,.009),(-.006,.012),(.009,.007),(.025,0.)]
verts=[marker+Vector((r*math.cos(i*math.tau/16),r*math.sin(i*math.tau/16),z)) for z,r in rings for i in range(16)]
faces=[(j*16+i,j*16+(i+1)%16,(j+1)*16+(i+1)%16,(j+1)*16+i) for j in range(4) for i in range(16)]
mesh=bpy.data.meshes.new('Steady flame');mesh.from_pydata(verts,[],faces)
obj=bpy.data.objects.new('Steady flame',mesh);scene.collection.objects.link(obj)
mat=bpy.data.materials.new('Warm emission');mat.use_nodes=True
bs=mat.node_tree.nodes.get('Principled BSDF');bs.inputs['Emission Color'].default_value=(1,.55,.25,1);bs.inputs['Emission Strength'].default_value=12
mesh.materials.append(mat)
bpy.ops.object.light_add(type='POINT',location=marker);bpy.context.object.data.energy=2;bpy.context.object.data.color=(1,.55,.25);bpy.context.object.data.shadow_soft_size=.04
bpy.ops.object.camera_add(location=centre+Vector((1.1,-1.8,1.0))*size)
camera=bpy.context.object;camera.rotation_euler=(centre-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.type='ORTHO';camera.data.ortho_scale=size*1.35;scene.camera=camera
bpy.ops.object.light_add(type='AREA',location=centre+Vector((1,-2,3))*size);bpy.context.object.data.energy=200*size*size;bpy.context.object.data.size=size*2
scene.world=bpy.data.worlds.new('World');scene.world.use_nodes=True;scene.world.node_tree.nodes['Background'].inputs[1].default_value=.6
scene.render.engine='CYCLES';scene.cycles.samples=32;scene.cycles.use_denoising=True
scene.render.resolution_x=256;scene.render.resolution_y=192;scene.render.resolution_percentage=100
scene.render.film_transparent=True;scene.render.image_settings.file_format='PNG';scene.view_settings.view_transform='AgX'
scene.render.filepath=str(root/'assets/gui/building/wooden-lantern.png')
bpy.ops.render.render(write_still=True)
