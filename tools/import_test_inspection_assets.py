"""Import the existing local GLB technology models for render-only Sandbox stations.

No source models are modified. Requires numpy/Pillow, like the furniture importer.
"""
import io, json, struct, hashlib, shutil
from pathlib import Path
import numpy as np
from PIL import Image
from furniture_gltf import Scene, matrix, centre, bounds

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "assets/models"
SOURCES = {
    "inspection-zpm-hub": "zpm_hub/zpm_hub_loaded.glb",
    "inspection-transport-rings": "transport_rings_stacked/transport_rings_compact.glb",
}


def import_model(name, source, body_centred=False):
    raw = source.read_bytes()
    magic, version, length = struct.unpack_from("<4sII", raw)
    assert magic == b"glTF" and version == 2 and length == len(raw)
    chunks = {}; offset = 12
    while offset < length:
        size, kind = struct.unpack_from("<II", raw, offset); offset += 8
        chunks[kind] = raw[offset:offset+size]; offset += size
    scene = Scene.__new__(Scene)
    scene.path = source; scene.doc = json.loads(chunks[0x4e4f534a]); scene.buffers = [chunks[0x004e4942]]; scene.world = {}
    def visit(i, parent):
        node = scene.doc['nodes'][i]; scene.world[i] = parent @ matrix(node)
        for child in node.get('children', []): visit(child, scene.world[i])
    for i in scene.doc['scenes'][scene.doc.get('scene', 0)]['nodes']: visit(i, np.eye(4))
    parts = centre(scene.parts()); entries = []; textures = {}
    if body_centred:
        lo, hi = bounds(parts)
        for vertices, _ in parts: vertices[:, 1] -= hi[1] / 2
    def texture(index, channel, factor):
        key = (index, channel, tuple(factor))
        if key in textures: return textures[key]
        if index is not None:
            image = scene.doc['images'][scene.doc['textures'][index]['source']]
            view = scene.doc['bufferViews'][image['bufferView']]
            start = view.get('byteOffset', 0)
            im = Image.open(io.BytesIO(scene.buffers[view['buffer']][start:start+view['byteLength']])).convert('RGBA')
        else: im = Image.new('RGBA', (4, 4), (255, 255, 255, 255))
        im.thumbnail((1024, 1024)); pixels = np.array(im)
        if channel == 'texture':
            pixels[:,:,:3] = np.clip(pixels[:,:,:3] * np.power(factor[:3], 1/2.2), 0, 255).astype('uint8')
            pixels[:,:,3] = (pixels[:,:,3] * factor[3]).astype('uint8')
        elif channel == 'normal': pixels[:,:,1] = 255-pixels[:,:,1]
        else:
            pixels[:,:,1] = (pixels[:,:,1] * factor[0]).astype('uint8')
            pixels[:,:,2] = (pixels[:,:,2] * factor[1]).astype('uint8')
        file = name+'-'+hashlib.sha256(pixels.tobytes()).hexdigest()[:16]+'.png'
        Image.fromarray(pixels).save(OUT/file); textures[key] = file; return file
    for i, (vertices, material) in enumerate(parts):
        mat = scene.doc.get('materials', [{}])[material]; pbr = mat.get('pbrMetallicRoughness', {})
        mesh = f'{name}-{i}.mesh'; v = vertices.astype('<f4')
        (OUT/mesh).write_bytes(b'SGM1'+struct.pack('<I',len(v))+v.tobytes())
        part = dict(mesh=mesh, texture=texture(pbr.get('baseColorTexture',{}).get('index'),'texture',pbr.get('baseColorFactor',[1,1,1,1])), repeat=True)
        if 'normalTexture' in mat: part['normal'] = texture(mat['normalTexture']['index'],'normal',[1])
        part['metallicRoughness'] = texture(pbr.get('metallicRoughnessTexture',{}).get('index'),'metallicRoughness',[pbr.get('roughnessFactor',1),pbr.get('metallicFactor',1)])
        entries.append(part)
    (OUT/(name+'.json')).write_text(json.dumps(dict(version=1,parts=entries),indent=2)+'\n')
    lo, hi = bounds(parts)
    return dict(model=name,source=str(source.relative_to(ROOT)),sha256=hashlib.sha256(raw).hexdigest(),min=lo.tolist(),max=hi.tolist(),renderOnly=not body_centred)


if __name__ == '__main__':
    records = [import_model(name, ROOT/'assets_itch/custom_models'/source) for name,source in SOURCES.items()]
    provenance = ROOT/'assets/testing'; provenance.mkdir(exist_ok=True)
    (provenance/'inspection-assets.json').write_text(json.dumps(records,indent=2)+'\n')
    for source in ['zpm_hub/CAP-LICENSE.md','zpm_hub/CAP-CREDITS.md','transport_rings_stacked/LICENSE-source.txt']:
        shutil.copyfile(ROOT/'assets_itch/custom_models'/source, provenance/Path(source).name)
    print(json.dumps(records,indent=2))
