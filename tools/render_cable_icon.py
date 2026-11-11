"""Render the Sandbox cable-tool thumbnail from a procedural rubber cable."""
from pathlib import Path
import math,bpy
from mathutils import Vector
root=Path(__file__).resolve().parents[1]
bpy.ops.wm.read_factory_settings(use_empty=True)
curve=bpy.data.curves.new('Cable coil','CURVE');curve.dimensions='3D';curve.resolution_u=2;curve.bevel_depth=.045;curve.bevel_resolution=4
s=curve.splines.new('POLY');s.points.add(191)
for i,p in enumerate(s.points):
 t=i/191;angle=t*math.tau*2.3;r=.6-.18*t
 p.co=(r*math.cos(angle),r*math.sin(angle),.05+.16*t,1)
obj=bpy.data.objects.new('Blue rubber cable',curve);bpy.context.collection.objects.link(obj)
mat=bpy.data.materials.new('Rubber');mat.diffuse_color=(.025,.19,.6,1);mat.use_nodes=True
bs=mat.node_tree.nodes.get('Principled BSDF');bs.inputs['Base Color'].default_value=(.025,.19,.6,1);bs.inputs['Roughness'].default_value=.8;obj.data.materials.append(mat)
scene=bpy.context.scene
bpy.ops.object.camera_add(location=(1.6,-2.1,2.5));cam=bpy.context.object;cam.rotation_euler=(Vector((0,0,.1))-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.type='ORTHO';cam.data.ortho_scale=1.65;scene.camera=cam
bpy.ops.object.light_add(type='AREA',location=(1,-2,3));bpy.context.object.data.energy=250;bpy.context.object.data.size=3
scene.world=bpy.data.worlds.new('World');scene.world.use_nodes=True;scene.world.node_tree.nodes['Background'].inputs[1].default_value=.4
scene.render.engine='CYCLES';scene.cycles.samples=24;scene.cycles.use_denoising=True
scene.render.resolution_x=256;scene.render.resolution_y=192;scene.render.resolution_percentage=100;scene.render.film_transparent=True
scene.render.image_settings.file_format='PNG';scene.render.filepath=str(root/'assets/gui/building/cable.png');bpy.ops.render.render(write_still=True)
