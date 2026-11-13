"""Generate the Q-menu soldier portrait from the supplied model, using Blender background mode."""
from pathlib import Path
import bpy, json, gzip
from mathutils import Vector, Matrix

root = Path(__file__).resolve().parents[1]
bpy.ops.wm.read_factory_settings(use_empty=True)
# Use the imported game's corrected bind transforms and idle pose. The original
# glTF has placeholder binds whose Blender bounds do not represent the character.
data = json.load(gzip.open(root/'assets/characters/military.sga.gz'))
clip = next(c for c in data['clips'] if c['name']=='idle')
def matrix(values):
    return Matrix([values[i:i+4] for i in range(0,16,4)])
world = {}
def bone(i):
    if i not in world:
        local=matrix(clip['matrices'][i])
        world[i]=bone(data['parents'][i])@local if data['parents'][i]>=0 else local
    return world[i]
skin=[bone(i)@matrix(bind) for i,bind in enumerate(data['inverseBind'])]
for part in data['parts']:
    if part['lod']!=1: continue
    vertices=[]
    for i in range(len(part['positions'])//3):
        p=Vector(part['positions'][i*3:i*3+3]+[1])
        v=Vector((0,0,0,0))
        for j in range(4):
            v+=(skin[int(part['joints'][i*4+j])]@p)*part['weights'][i*4+j]
        vertices.append((v.x,-v.z,v.y))
    indices=part['indices']
    mesh=bpy.data.meshes.new(part['name'])
    mesh.from_pydata(vertices,[],[indices[i:i+3] for i in range(0,len(indices),3)])
    obj=bpy.data.objects.new(part['name'],mesh);bpy.context.collection.objects.link(obj)
    uv=mesh.uv_layers.new()
    for loop in mesh.loops:
        i=loop.vertex_index;uv.data[loop.index].uv=part['uvs'][i*2:i*2+2]
    mat=bpy.data.materials.new(part['name']);mat.use_nodes=True
    bs=mat.node_tree.nodes.get('Principled BSDF');bs.inputs['Roughness'].default_value=.8
    tex=mat.node_tree.nodes.new('ShaderNodeTexImage')
    tex.image=bpy.data.images.load(str(root/'assets/characters'/part['albedo']),check_existing=True)
    mat.node_tree.links.new(tex.outputs['Color'],bs.inputs['Base Color']);mesh.materials.append(mat)
bpy.context.view_layer.update()
scene = bpy.context.scene
points = [obj.matrix_world @ Vector(c) for obj in scene.objects if obj.type == 'MESH' for c in obj.bound_box]
lo = Vector(tuple(min(p[i] for p in points) for i in range(3)))
hi = Vector(tuple(max(p[i] for p in points) for i in range(3)))
centre = (lo + hi) * .5
size = max(hi - lo)
bpy.ops.object.camera_add(location=centre + Vector((.8, 1.8, .6)) * size)
camera = bpy.context.object
camera.rotation_euler = (centre - camera.location).to_track_quat('-Z', 'Y').to_euler()
camera.data.type = 'ORTHO'
camera.data.ortho_scale = size * 1.4
scene.camera = camera
for offset, energy in [((1, 2, 3), 300), ((-2, 0, 1), 140)]:
    bpy.ops.object.light_add(type='AREA', location=centre + Vector(offset) * size)
    light = bpy.context.object
    light.data.energy = energy * size * size
    light.data.size = size * 2
    light.rotation_euler = (centre-light.location).to_track_quat('-Z', 'Y').to_euler()
scene.world = bpy.data.worlds.new('Thumbnail world')
scene.world.use_nodes = True
scene.world.node_tree.nodes['Background'].inputs[1].default_value = .5
scene.render.engine = 'CYCLES'
scene.cycles.samples = 24
scene.cycles.use_denoising = True
scene.render.resolution_x = 256
scene.render.resolution_y = 192
scene.render.resolution_percentage = 100
scene.render.film_transparent = True
scene.render.image_settings.file_format = 'PNG'
scene.view_settings.view_transform = 'AgX'
scene.render.filepath = str(root/'assets/gui/building/military-soldier.png')
bpy.ops.render.render(write_still=True)
