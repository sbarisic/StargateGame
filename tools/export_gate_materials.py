"""Bake rough gate metal and recessed engraving into the existing runtime UVs.

Run in Blender with the assembled Stargate_and_DHD.blend, then
-- --output assets/models. The source blend and runtime geometry are unchanged.
Depth is reconstructed from authored tangent normals, never albedo brightness.
"""
import argparse
import hashlib
import json
import sys
from pathlib import Path

import bpy
import numpy as np

args = argparse.ArgumentParser()
args.add_argument('--output', required=True)
root = Path(args.parse_args(sys.argv[sys.argv.index('--') + 1:]).output).resolve()
RELIEF_METRES = 0.012
manifest = json.loads((root / 'stargate.json').read_text())
scene = bpy.context.scene
scene.render.engine = 'CYCLES'
scene.cycles.samples = 1
scene.cycles.device = 'CPU'
scene.render.bake.margin = 8
scene.render.bake.use_clear = True
scene.render.bake.use_selected_to_active = False
scene.render.bake.normal_space = 'TANGENT'
scene.view_settings.view_transform = 'Standard'
for obj in scene.objects:
    obj.hide_render = True
height_images = {}
cache = {}
report = {'source_sha256': hashlib.sha256(Path(bpy.data.filepath).read_bytes()).hexdigest(),
          'method': 'screened Poisson recessed engraving; normals baked from the same metre-scaled height field with authored fine detail',
          'roughness': [0.88, 0.98], 'metalness': [0.7, 0.9], 'depth_metres': RELIEF_METRES,
          'height_profile_exponent': 0.65, 'authored_normal_strength': 0.35,
          'source_normals': {}, 'materials': []}


def relief(image):
    if image.name in height_images:
        return height_images[image.name]
    image.colorspace_settings.name = 'Non-Color'
    w, h = image.size
    pixels = np.empty(w*h*4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    n = pixels.reshape(h, w, 4)[..., :2]*2-1
    # Source is DirectX. Blender image rows run bottom-to-top.
    n[..., 1] *= -1
    nz = np.sqrt(np.maximum(1-np.sum(n*n, axis=2), 0.01))
    slopes = -n / np.maximum(nz[..., None], 0.35)
    slopes[np.sum(n*n, axis=2) > 0.8] = 0
    kx = 2*np.pi*np.fft.rfftfreq(w)[None, :]
    ky = 2*np.pi*np.fft.fftfreq(h)[:, None]
    # Screen out broad bevels/UV-island orientation: those already exist in geometry.
    screen = (2*np.pi/64)**2
    spectrum = (-1j*kx*np.fft.rfft2(slopes[..., 0])-1j*ky*np.fft.rfft2(slopes[..., 1]))/(kx*kx+ky*ky+screen)
    height = np.fft.irfft2(spectrum, s=(h, w)).real
    # Only recess the surface. The top plateau stays at the original mesh depth.
    depth = np.maximum(0, -height)
    scale = max(float(np.percentile(depth, 99)), 0.1)
    rgba = np.ones((h, w, 4), dtype=np.float32)
    # Retain weaker cuts as visible recesses instead of leaving nearly all
    # texels on the plateau. Apply the same profile to depth and normal baking.
    rgba[..., :3] = (1-np.power(np.clip(depth/scale, 0, 1), .65))[..., None]
    result = bpy.data.images.new('GateRelief_' + image.name, width=w, height=h, float_buffer=True)
    result.colorspace_settings.name = 'Non-Color'
    result.pixels.foreach_set(rgba.ravel())
    height_images[image.name] = result
    path = Path(bpy.path.abspath(image.filepath))
    report['source_normals'][path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def save(name, pixels):
    h, w, _ = pixels.shape
    image = bpy.data.images.new(name, width=w, height=h, alpha=True)
    image.colorspace_settings.name = 'Non-Color'
    image.alpha_mode = 'STRAIGHT'
    image.pixels.foreach_set(pixels.astype(np.float32).ravel())
    image.filepath_raw = str(root / name)
    image.file_format = 'PNG'
    image.save()
    bpy.data.images.remove(image)


for part in manifest['parts']:
    signature = (part['texture'], tuple(part['materials']))
    if signature not in cache:
        source = bpy.data.objects[part['name']]
        obj = source.copy()
        obj.data = source.data.copy()
        scene.collection.objects.link(obj)
        obj.hide_render = False
        mesh = obj.data
        mesh.calc_loop_triangles()
        raw = (root / part['mesh']).read_bytes()
        runtime = np.frombuffer(raw, dtype='<f4', offset=8).reshape(-1, 8)
        if len(runtime) != len(mesh.loop_triangles)*3:
            raise RuntimeError('Runtime/source topology mismatch: ' + part['name'])
        source_uv = mesh.uv_layers.active.name
        atlas = mesh.uv_layers.new(name='GateMaterialUV')
        for i, tri in enumerate(mesh.loop_triangles):
            for j, li in enumerate(tri.loops):
                atlas.data[li].uv = runtime[i*3+j, 6:8]
        mesh.uv_layers.active = atlas
        atlas.active_render = True
        materials = []
        for slot in obj.material_slots:
            mat = slot.material.copy()
            slot.material = mat
            nodes, links = mat.node_tree.nodes, mat.node_tree.links
            uv = nodes.new('ShaderNodeUVMap')
            uv.uv_map = source_uv
            normal_image = None
            for node in list(nodes):
                if node.type == 'TEX_COORD':
                    for link in list(node.outputs['UV'].links):
                        links.new(uv.outputs['UV'], link.to_socket)
                if node.type == 'TEX_IMAGE':
                    if not node.inputs['Vector'].is_linked:
                        links.new(uv.outputs['UV'], node.inputs['Vector'])
                    if node.image and Path(bpy.path.abspath(node.image.filepath)).stem.endswith('_N'):
                        normal_image = node.image
                if node.type == 'NORMAL_MAP':
                    node.uv_map = source_uv
            if normal_image and 'Light' not in mat.name:
                # The old bake kept the original normal independently of the
                # reconstructed relief. Derive the groove slopes from the same
                # height and physical depth used by runtime parallax, so local
                # lights respond to their sides as well as sunlight does.
                bs = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
                height = nodes.new('ShaderNodeTexImage')
                height.image = relief(normal_image)
                links.new(uv.outputs['UV'], height.inputs['Vector'])
                bump = nodes.new('ShaderNodeBump')
                bump.inputs['Distance'].default_value = RELIEF_METRES
                bump.inputs['Strength'].default_value = 1
                links.new(height.outputs['Color'], bump.inputs['Height'])
                if bs.inputs['Normal'].is_linked:
                    links.new(bs.inputs['Normal'].links[0].from_socket, bump.inputs['Normal'])
                for node in nodes:
                    if node.type == 'NORMAL_MAP':
                        node.inputs['Strength'].default_value *= .35
                links.new(bump.outputs['Normal'], bs.inputs['Normal'])
            materials.append((mat, uv, normal_image))
        size = 4096 if 'GlyphTrack' in part['name'] or 'Segment' in part['name'] else 512
        target = bpy.data.images.new('GateBake', width=size, height=size, alpha=True, float_buffer=True)
        target.colorspace_settings.name = 'Non-Color'
        for mat, _, _ in materials:
            node = mat.node_tree.nodes.new('ShaderNodeTexImage')
            node.image = target
            mat.node_tree.nodes.active = node
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.bake(type='NORMAL')
        pixels = np.empty(size*size*4, dtype=np.float32)
        target.pixels.foreach_get(pixels)
        normal = pixels.reshape(size, size, 4).copy()
        normal[..., 1] = 1-normal[..., 1]  # runtime DirectX convention
        normal[normal[..., 3] < .5] = [.5, .5, 1, 1]
        normal[..., 3] = 1
        stem = Path(part['texture']).stem
        for mat, uv, normal_image in materials:
            nodes, links = mat.node_tree.nodes, mat.node_tree.links
            bs = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
            output = next(n for n in nodes if n.type == 'OUTPUT_MATERIAL')
            packed = nodes.new('ShaderNodeCombineXYZ')
            metal = 'Light' not in mat.name
            for channel, source_input, multiplier, minimum in (
                    ('X', 'Metallic', .2, .7), ('Y', 'Roughness', .1, .88)):
                value = bs.inputs[source_input]
                scale = nodes.new('ShaderNodeMath')
                scale.operation = 'MULTIPLY_ADD'
                scale.inputs[1].default_value = multiplier if metal else 0
                scale.inputs[2].default_value = minimum if metal else (0 if channel == 'X' else .9)
                if value.is_linked:
                    links.new(value.links[0].from_socket, scale.inputs[0])
                else:
                    scale.inputs[0].default_value = value.default_value
                links.new(scale.outputs[0], packed.inputs[channel])
            packed.inputs['Z'].default_value = 1
            if metal and normal_image:
                height = nodes.new('ShaderNodeTexImage')
                height.image = relief(normal_image)
                links.new(uv.outputs[0], height.inputs['Vector'])
                links.new(height.outputs['Color'], packed.inputs['Z'])
            emission = nodes.new('ShaderNodeEmission')
            links.new(packed.outputs[0], emission.inputs['Color'])
            links.new(emission.outputs[0], output.inputs['Surface'])
        bpy.ops.object.bake(type='EMIT')
        target.pixels.foreach_get(pixels)
        baked = pixels.reshape(size, size, 4)
        packed = np.ones_like(baked)
        packed[..., 1] = np.clip(baked[..., 1], .88, 1)
        packed[..., 2] = np.clip(baked[..., 0], 0, 1)
        packed[..., 3] = np.clip(baked[..., 2], 0, 1)
        # Empty atlas space is a neutral top plateau, never a deep black pit.
        # Bake alpha is not a coverage mask in every Blender version. Authored
        # roughness is always >= .88, so black empty texels are unambiguous.
        empty = baked[..., 1] < .5
        packed[empty] = [1, .95, .7, 1]
        normal[empty] = [.5, .5, 1, 1]
        save(stem+'-normal.png', normal)
        save(stem+'-material.png', packed)
        cache[signature] = {'normal': stem+'-normal.png', 'metallicRoughness': stem+'-material.png',
                            'heightScale': RELIEF_METRES if any('Light' not in m for m in part['materials']) else 0}
        bpy.data.images.remove(target)
        bpy.data.objects.remove(obj, do_unlink=True)
        for mat, _, _ in materials:
            bpy.data.materials.remove(mat)
    part.update(cache[signature])
    report['materials'].append({'name': part['name'], **cache[signature]})
    print('GATE MATERIAL', part['name'], flush=True)
(root / 'stargate.json').write_text(json.dumps(manifest, indent=2)+'\n')
(root / 'gate-material-provenance.json').write_text(json.dumps(report, indent=2)+'\n')
