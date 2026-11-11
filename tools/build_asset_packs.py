"""Dependency-aware bake orchestration; normal development only publishes an overlay."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import time
from asset_packs import Cache, atomic, build, encode
from build_assets import cache_lock


class BakeStages:
    """Checkpoint each successful bake, including its output contents."""
    def __init__(self, cache):
        self.file = cache/'bake-stages.json'
        self.state = json.loads(self.file.read_text()) if self.file.exists() else {}
        self.hashes = Cache(cache/'bake-hashes')

    def run(self, key, inputs, outputs, action):
        identity = hashlib.sha256(encode([(str(p.resolve()), self.hashes.hash_file(p)) for p in sorted(set(inputs))])).hexdigest()
        old = self.state.get(key, {})
        try:
            expected = {str(p.resolve()) for p in outputs()} if old else set()
        except (OSError, ValueError, KeyError):
            expected = set()
        if old.get('input') == identity and expected and expected == set(old.get('outputs', {})) and all(
            Path(p).is_file() and self.hashes.hash_file(Path(p)) == h for p, h in old['outputs'].items()
        ):
            print(f'Assets: reuse {key}', flush=True)
            return False
        started = time.monotonic()
        print(f'Assets: baking {key}...', flush=True)
        action()
        generated = sorted(set(outputs()))
        if not generated: raise ValueError('Bake produced no outputs: ' + key)
        record = dict(input=identity, outputs={str(p.resolve()): self.hashes.hash_file(p) for p in generated})
        self.state[key] = record
        self.hashes.save()
        atomic(self.file, encode(self.state))
        print(f'Assets: checkpointed {key} ({time.monotonic()-started:.1f}s)', flush=True)
        return True


def run_bake(command, root):
    # Keep VS output alive during large raster/voxel stages without buffering the child's log.
    started = time.monotonic()
    with subprocess.Popen(command, cwd=root) as process:
        while True:
            try:
                result = process.wait(timeout=15)
                break
            except subprocess.TimeoutExpired:
                print(f'Assets: bake still running ({time.monotonic()-started:.0f}s)', flush=True)
        if result: raise subprocess.CalledProcessError(result, command)


def prepared_outputs(root, generated, bases):
    mapping = json.loads((root/'assets/vegetation/trees.json').read_text())
    paths = set()
    for base in bases:
        if base not in mapping: raise ValueError('Missing prepared tree mapping: ' + base)
        for identity in (base, base+'-summer'):
            for variant in mapping.get(identity, []):
                path = generated/(variant+'.json'); paths.add(path)
                for part in json.loads(path.read_text())['parts']:
                    for field in ('mesh', 'texture', 'normal', 'metallicRoughness'):
                        if part.get(field):
                            candidate = generated/part[field]
                            paths.add(candidate if candidate.exists() else root/'assets/models'/part[field])
    return paths


def canopy_outputs(root, descriptor, folder):
    data = json.loads(descriptor.read_text())
    if data['version'] != 3: raise ValueError('Incomplete canopy ambient bake: ' + str(descriptor))
    paths = {descriptor}
    for level in data['levels']:
        for pair in level['slices']:
            for name in pair: paths.add(root/'assets'/name)
    for level in data['ambient']['levels']: paths.add(root/'assets'/level['path'])
    prefix = 'prototype-' + (folder+'-' if folder else '') + 'trunk-'
    trunks = list((root/'assets/models').glob(prefix+'*.json'))
    if not trunks: raise ValueError('Missing canopy trunks: ' + str(descriptor))
    for path in trunks:
        paths.add(path)
        for part in json.loads(path.read_text())['parts']:
            for field in ('mesh', 'texture', 'normal', 'metallicRoughness'):
                if part.get(field): paths.add(root/'assets/models'/part[field])
    return paths


def canopy_command(root, cache, model, folder, generated):
    descriptor = root/'assets/vegetation/prototype'/folder/'canopy.json'
    source = json.loads(descriptor.read_text()).get('source', {}) if descriptor.exists() else {}
    config = cache/'canopy-sources.json'
    configured = json.loads(config.read_text()) if config.exists() else {}
    extraction = Path(configured.get(model, str(cache/'canopy-sources'/model)))
    command = [sys.executable, '-u', str(root/'tools/cook_tree_prototype.py'), '--model', model, '--id', folder]
    inputs = []
    if source.get('lods') or not folder or (extraction/'extraction.json').exists():
        report_path = extraction/'extraction.json'
        if not report_path.exists():
            raise ValueError(f'{model} needs its source extraction. Set its directory in {config}, '
                             'or run tools/extract_tree_prototype.py into ' + str(extraction))
        report = json.loads(report_path.read_text())
        if report.get('model', 'MoreRealisticTree_01') != model:
            raise ValueError('Extraction belongs to a different model: ' + model)
        inputs = [report_path, *[extraction/lod['file'] for lod in report['lods']]]
        # An extraction cannot conceal edits to its original GLTF inputs.
        for name, expected in report.get('sources', {}).items():
            original = root/'assets_itch/trees/more_realistic_trees_free'/name
            if original.exists() and hashlib.sha256(original.read_bytes()).hexdigest() != expected:
                raise ValueError('Original tree source changed; rerun tools/extract_tree_prototype.py for ' + model)
        command += ['--source', str(extraction)]
    else:
        inputs = list(prepared_outputs(root, generated, [model.removesuffix('-summer')]))
        command += ['--prepared', str(generated)]
    return command, inputs


def prepare(root, cache):
    from cook_world_canopies import SPECIES
    models = root / 'assets/models'
    generated = cache / 'models'
    stages = BakeStages(cache)
    water = root/'assets/terrain/water'
    def water_outputs():
        descriptor = water/'ripples.json'
        return [descriptor, *(water/name for name in json.loads(descriptor.read_text())['mips'])]
    stages.run('water-normals', [root/'tools/generate_water_normals.py'], water_outputs,
               lambda: run_bake([sys.executable, str(root/'tools/generate_water_normals.py')], root))
    grass_inputs = [root/'tools/bake_grass_volume.py', root/'tools/tree_volume_data.py', root/'tools/bake_canopy_ambient.py', root/'assets/vegetation/grass-blades.png']
    stages.run('grass-volume', grass_inputs, lambda: list((root/'assets/vegetation/grass-volume').glob('*')),
               lambda: run_bake([sys.executable, str(root/'tools/bake_grass_volume.py')], root))
    definitions = {}
    for file in ('trees-bounds.json', 'realistic-trees-bounds.json'):
        definitions.update(json.loads((models/file).read_text()))
    changed = []
    # Validate external source routes before spending time on any tree preparation.
    for model, (_, folder, _) in SPECIES.items():
        descriptor = root/'assets/vegetation/prototype'/folder/'canopy.json'
        if not folder or descriptor.exists() and json.loads(descriptor.read_text()).get('source', {}).get('lods'):
            canopy_command(root, cache, model, folder, generated)
    for base, bounds in definitions.items():
        names = [base, *bounds['lods']]
        if (models/(base+'_detail.json')).exists(): names.append(base+'_detail')
        # The summer birch borrows the source leaf material and geometry.
        if base == 'European_White_Birch_Hero_Winter': names.append('Leave-Tree-01a')
        dependencies = {root/'tools/prepare_vegetation.py', models/'trees-bounds.json', models/'realistic-trees-bounds.json'}
        for name in names:
            file = models/(name+'.json'); dependencies.add(file)
            for part in json.loads(file.read_text())['parts']:
                for field in ('mesh', 'texture', 'normal', 'metallicRoughness'):
                    if part.get(field): dependencies.add(models/part[field])
        command = [sys.executable, '-u', str(root/'tools/prepare_vegetation.py'), '--output', str(generated)]
        if (root/'assets/vegetation/trees.json').exists(): command += ['--only', base]
        if stages.run('tree/'+base, dependencies, lambda: prepared_outputs(root, generated, [base]),
                      lambda: run_bake(command, root)): changed.append(base)
    canopy_tools = [root/'tools'/name for name in ('cook_tree_prototype.py','tree_volume_sources.py','tree_volume_data.py','bake_canopy_ambient.py')]
    canopy_tools += [root/'tools/import_realistic_trees.py', root/'tools/cook_assets.py']
    for model, (_, folder, _) in SPECIES.items():
        descriptor = root/'assets/vegetation/prototype'/folder/'canopy.json'
        command, inputs = canopy_command(root, cache, model, folder, generated)
        stages.run('canopy/'+model, inputs+canopy_tools,
                   lambda: canopy_outputs(root, descriptor, folder), lambda: run_bake(command, root))
    subprocess.run([sys.executable, str(root/'tools/cook_world_canopies.py'), '--prepared', str(generated)], cwd=root, check=True)
    return changed


def main(root, cache, release=False, skip_bakes=False):
    root, cache = root.resolve(), cache.resolve()
    cache.mkdir(parents=True, exist_ok=True)
    with cache_lock(cache/'pipeline.lock'):
        from import_tree_bark import apply_materials
        apply_materials(root, cache/'models')
        if not skip_bakes: prepare(root, cache)
        apply_materials(root, cache/'models')
        output = cache/'packs'
        # First installation creates the immutable baseline. Ordinary edits never rewrite it.
        if release or not (output/'catalog.json').exists():
            build(root/'assets', cache/'models', output, cache/'asset-cache')
        catalog = json.loads((output/'catalog.json').read_text())
        atomic(output/'pack-files.txt', ('\n'.join(p['file'] for p in catalog['packs'] + catalog['patches']) + '\n').encode())
        if not release:
            build(root/'assets', cache/'models', output, cache/'asset-cache', cache/'dev-override')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--cache', type=Path, default=Path('artifacts/cooked'))
    parser.add_argument('--release', action='store_true')
    parser.add_argument('--skip-bakes', action='store_true', help='Use already prepared vegetation for isolated validation.')
    args = parser.parse_args()
    main(args.root, args.cache, args.release, args.skip_bakes)
