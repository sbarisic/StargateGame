"""Publication and incremental-isolation checks using real cooked model fixtures."""
import json
from pathlib import Path
import struct
import tempfile
from PIL import Image
import asset_packs as packs


def load(path):
    return json.loads(path.read_text())


with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    source = root / 'assets'
    models = source / 'models'
    models.mkdir(parents=True)
    (source / 'shaders').mkdir()
    (source / 'shaders/basic.vert').write_text('void main() {}')
    Image.new('RGBA', (8, 8), (24, 180, 65, 255)).save(models / 'leaf.png')
    Image.new('RGBA', (8, 8), (100, 100, 100, 255)).save(models / 'metal.png')
    mesh = b'SGM1' + struct.pack('<i24f', 3, *([0.,0.,0.,0.,1.,0.,0.,0.] + [1.,0.,0.,0.,1.,0.,1.,0.] + [0.,1.,0.,0.,1.,0.,0.,1.]))
    for name, texture in [('tree', 'leaf.png'), ('rifle', 'metal.png')]:
        (models / (name + '.mesh')).write_bytes(mesh)
        (models / (name + '.json')).write_text(json.dumps(dict(parts=[dict(mesh=name+'.mesh', texture=texture)])))
    output, cache, override = root/'packs', root/'cache', root/'override'
    def build(dev=False):
        return packs.build(source, root/'vegetation', output, cache, override if dev else None)
    first = build()
    assert set(first['packsWritten']) == {'bootstrap', 'vegetation', 'weapons'}
    original = {p.name: p.read_bytes() for p in output.iterdir()}
    assert not build()['cooked'] and not build()['packsWritten']
    assert original == {p.name: p.read_bytes() for p in output.iterdir()}
    Image.new('RGBA', (8, 8), (180, 40, 20, 255)).save(models/'metal.png')
    changed = build(True)
    assert changed['cooked'] == ['models/rifle.json'] and not changed['packsWritten']
    assert original == {p.name: p.read_bytes() for p in output.iterdir()}
    manifest = load(override/'manifest.json')
    assert 'models/rifle.json' in manifest['assets'] and 'models/tree.json' not in manifest['assets']
    assert not any(k.startswith('models/tree') for k in manifest['entries'])
    working = (override/'manifest.json').read_bytes()
    (models/'rifle.json').write_text('{')
    try:
        build(True)
        raise AssertionError('Broken input accepted')
    except json.JSONDecodeError:
        pass
    assert (override/'manifest.json').read_bytes() == working
    (models/'rifle.json').unlink()
    build(True)
    assert 'models/rifle.json' in load(override/'manifest.json')['tombstones']
    assert load(override/'manifest.json')['assets']['models/rifle.json']['deleted']
    base = load(output/'catalog.json')
    (output/'catalog.json').write_text(json.dumps(dict(base, cooker='0'*64)))
    working = (override/'manifest.json').read_bytes()
    try:
        build(True)
        raise AssertionError('Incompatible base accepted')
    except ValueError as error:
        assert 'Rebuild base packs' in str(error)
    assert (override/'manifest.json').read_bytes() == working
    (output/'catalog.json').write_text(json.dumps(base))
    release = build()
    assert 'models/rifle.json' not in load(output/'catalog.json')['entries']
    assert not list(output.glob('**/manifest.json')) and source.exists()
    assert 'vegetation' not in release['packsWritten']
    for row in load(output/'catalog.json')['packs']:
        packs.read_pack(output/row['file'])
    (models/'rifle.json').write_text(json.dumps(dict(parts=[dict(mesh='rifle.mesh', texture='leaf.png')])))
    (models/'pistol.mesh').write_bytes(mesh)
    (models/'pistol.json').write_text(json.dumps(dict(parts=[dict(mesh='pistol.mesh', texture='metal.png')])))
    (source/'scenes').mkdir()
    scene = source/'scenes/outpost.json'
    scene.write_text(json.dumps(dict(instances=[dict(asset='models/tree.json', position=[0,0,0])])) )
    build()
    original = {p.name: p.read_bytes() for p in output.iterdir()}
    scene.write_text(json.dumps(dict(instances=[dict(asset='models/tree.json', position=[4,0,0])])) )
    assert build(True)['cooked'] == ['scenes/outpost.json']
    Image.new('RGBA', (8, 8), (30, 190, 70, 255)).save(models/'leaf.png')
    assert set(build(True)['cooked']) == {'models/rifle.json', 'models/tree.json'}
    assert original == {p.name: p.read_bytes() for p in output.iterdir()}
print('PASS: category isolation, unchanged reuse, shared-dependency invalidation, placement-only edits, override dependency reuse, tombstones, failed cook preservation, incompatible base, clean release and full payload verification')
