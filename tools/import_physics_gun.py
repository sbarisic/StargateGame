"""Blender asset export for the supplied CC-BY Chrome ray gun."""
import bpy,json,struct,math
from pathlib import Path
from mathutils import Matrix,Vector
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'assets/models'
source=ROOT/'assets_itch/weapons/raygun'
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(source/'assets/models/chromeRayGun.glb'))
objects=[o for o in bpy.context.scene.objects if o.type=='MESH' and 'LOD' not in o.name.upper()]
if not objects: objects=[o for o in bpy.context.scene.objects if o.type=='MESH']
# Convert Blender Z-up to game Y-up, then rotate the authored sideways gun
# to point down local -Z. Normalize to a compact 38 cm first-person tool.
convert=Matrix(((0,1,0),(0,0,1),(1,0,0)))
points=[convert@(o.matrix_world@v.co) for o in objects for v in o.data.vertices]
lo=Vector(tuple(min(v[i] for v in points) for i in range(3)))
hi=Vector(tuple(max(v[i] for v in points) for i in range(3)))
scale=.38/(hi.z-lo.z)
center=(lo+hi)*.5
parts=[]
for obj in objects:
    mesh=obj.data; mesh.calc_loop_triangles()
    uv=mesh.uv_layers.active
    for slot,material in enumerate(mesh.materials):
        rows=[]
        for tri in mesh.loop_triangles:
            if tri.material_index!=slot:continue
            for li in tri.loops:
                pos=(convert@(obj.matrix_world@mesh.vertices[mesh.loops[li].vertex_index].co)-center)*scale
                normal=(convert@(obj.matrix_world.to_3x3().inverted().transposed()@mesh.corner_normals[li].vector)).normalized()
                t=uv.data[li].uv if uv else Vector((0,0))
                rows.append((*pos,*normal,float(t[0]),float(t[1])))
        if not rows:continue
        name=f'physics-gun-{len(parts)}'
        with (OUT/(name+'.mesh')).open('wb') as f:
            f.write(b'SGM1'+struct.pack('<I',len(rows)))
            for v in rows:f.write(struct.pack('<8f',*v))
        part=dict(mesh=name+'.mesh',repeat=True)
        bs=next(n for n in material.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
        def image_for(socket):
            seen=set()
            def walk(node):
                if node in seen:return None
                seen.add(node)
                if node.type=='TEX_IMAGE':return node.image
                for inp in node.inputs:
                    for link in inp.links:
                        image=walk(link.from_node)
                        if image:return image
                return None
            for link in bs.inputs[socket].links:
                result=walk(link.from_node)
                if result:return result
        for field,socket in [('texture','Base Color'),('normal','Normal'),('metallicRoughness','Roughness')]:
            image=image_for(socket)
            if not image:continue
            pixels=list(image.pixels[:]); w,h=image.size
            if field=='normal':
                for i in range(1,len(pixels),4):pixels[i]=1-pixels[i]
            target=bpy.data.images.new(name+'-'+field,width=w,height=h,alpha=True)
            if field!='texture':target.colorspace_settings.name='Non-Color'
            target.pixels[:]=pixels
            target.filepath_raw=str(OUT/(name+'-'+field+'.png'));target.file_format='PNG';target.save()
            part[field]=name+'-'+field+'.png'
        parts.append(part)
(OUT/'physics-gun.json').write_text(json.dumps(dict(version=1,parts=parts),indent=2)+'\n')
(OUT/'physics-gun-provenance.json').write_text(json.dumps(dict(source=str(source.relative_to(ROOT)),credits=(source/'CREDITS.md').read_text(),license='CC-BY-4.0',tool='tools/import_physics_gun.py'),indent=2)+'\n')
print('Exported physics gun:',len(parts),'parts',flush=True)
