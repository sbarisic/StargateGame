"""Import the user-supplied BarkPoplar001 PBR set for opaque broadleaf wood.
Original textures/meshes and leaf materials are kept. No network access.
"""
from pathlib import Path
import hashlib
import json
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCES = {
    'realistic-tree-35cdae9b1d31909e-base.png': 'bark-poplar-001',
    'tree-Tree_Bark_D.png': 'bark-poplar-001',
    'tree-BroadleafBark_Tex.png': 'bark-poplar-001',
}

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def apply_materials(root, vegetation):
    """Also called after vegetation preparation so regenerated LODs agree."""
    models = root / 'assets/models'
    manifest = models / 'tree-bark-materials.json'
    if not manifest.exists():
        return 0
    assets = json.loads(manifest.read_text())['materials']
    changed = 0
    for folder in (models, vegetation):
        for path in sorted(folder.glob('*.json')):
            doc = json.loads(path.read_text())
            dirty = False
            for part in doc.get('parts', []):
                if part.get('alphaCutoff', 0) > 0 or part.get('surfaceRole') == 'leaf':
                    continue
                original = part.get('barkSource', {}).get('texture', part.get('texture'))
                material = SOURCES.get(original)
                if material is None:
                    continue
                if 'barkSource' not in part:
                    part['barkSource'] = {k: part[k] for k in ('texture', 'normal', 'metallicRoughness') if k in part}
                    dirty = True
                for key, value in dict(assets[material]['runtime'], surfaceRole='wood').items():
                    if part.get(key) != value:
                        part[key] = value
                        dirty = True
            if dirty:
                path.write_text(json.dumps(doc, indent=2) + '\n')
                changed += 1
    return changed

def main():
    models = ROOT / 'assets/models'
    name = 'bark-poplar-001'
    source = ROOT / 'assets_itch/textures/bark'
    inputs, images = {}, {}
    for key in ('COL', 'NRM', 'GLOSS', 'AO', 'REFL', 'DISP'):
        path = source / f'BarkPoplar001_{key}_1K.jpg'
        inputs[key] = dict(path=path.relative_to(ROOT).as_posix(), sha256=digest(path))
        images[key] = Image.open(path)
        assert images[key].size == (512, 1024)
    runtime = dict(texture=f'tree-{name}-base.png', normal=f'tree-{name}-normal.png', metallicRoughness=f'tree-{name}-mr.png')
    # Preserve native dimensions and color; do not stretch to a square or brighten.
    images['COL'].convert('RGBA').save(models / runtime['texture'])
    normal = np.asarray(images['NRM'].convert('RGB')).copy()
    # Verified against the supplied displacement gradient: source X opposes image
    # columns, Y follows image rows (OpenGL). Runtime requires DirectX green.
    normal[:, :, 1] = 255 - normal[:, :, 1]
    Image.fromarray(normal).save(models / runtime['normal'])
    ao = np.asarray(images['AO'].convert('L'))
    rough = 255 - np.asarray(images['GLOSS'].convert('L'))
    Image.fromarray(np.stack([ao, rough, np.zeros_like(rough)], axis=-1)).save(models / runtime['metallicRoughness'])
    materials = {name: dict(source='assets_itch/textures/bark', license='User-supplied asset; original license not included in source folder',
        sourceNormalConvention='OpenGL (verified against displacement gradients)', normalConvention='DirectX',
        glossConversion='roughness = 1 - gloss', colorAdjustment='none', dimensions=[512, 1024],
        unusedMaps=['REFL (dielectric F0 supplied by renderer)', 'DISP (tree renderer uses normal relief)'],
        runtime=runtime, inputs=inputs, outputs={file: digest(models / file) for file in runtime.values()})}
    (models / 'tree-bark-materials.json').write_text(json.dumps(dict(version=1, tool='tools/import_tree_bark.py', materials=materials), indent=2) + '\n')
    print(f'Imported local BarkPoplar001; updated {apply_materials(ROOT, ROOT / "artifacts/cooked/models")} model descriptors.')

if __name__ == '__main__':
    main()
