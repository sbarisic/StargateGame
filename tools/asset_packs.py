"""Incremental runtime packs and atomic development overlays. Never edits sources.

Base: python tools/asset_packs.py --assets assets --vegetation artifacts/cooked/models --output artifacts/cooked/packs
Edit: same command plus --dev-override artifacts/cooked/dev-override
"""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import struct
import time
import brotli
from cook_assets import cook, encode
from build_assets import cache_lock

VERSION = 1
COOKER = hashlib.sha256(Path(__file__).read_bytes() + Path(__file__).with_name('cook_assets.py').read_bytes() + Path(__file__).with_name('texture_compression.py').read_bytes() + Path(__file__).with_name('cook_terrain_blocks.py').read_bytes()).hexdigest()


def digest(data):
    return hashlib.sha256(data).hexdigest()


def atomic(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and path.read_bytes() == data:
        return False
    temp = path.with_name(path.name + '.tmp')
    with temp.open('wb') as file:
        file.write(data)
        file.flush()
        os.fsync(file.fileno())
    os.replace(temp, path)
    return True


def valid_key(key):
    if not key or '\\' in key or ':' in key or key.startswith('/') or any(p in ('', '.', '..') for p in key.split('/')):
        raise ValueError('Invalid asset key: ' + key)
    return key


class Cache:
    def __init__(self, root):
        self.root = root
        self.state_file = root / 'state.json'
        self.state = json.loads(self.state_file.read_text()) if self.state_file.exists() else {}
        self.fingerprints = self.state.get('files', {})
        self.jobs = self.state.get('jobs', {}) if self.state.get('cooker') == COOKER else {}
        self.records = {}
        for job in self.jobs.values():
            self.records.update(job['outputs'])
        missing = {k.split('.json')[0] for k,r in self.records.items() if not self.blob(r['hash']).is_file()}
        self.records = {k:r for k,r in self.records.items() if k.split('.json')[0] not in missing}
        self.next_jobs = {}
        self.cooked = []
        self.reused = []

    def blob(self, hash):
        return self.root / 'blobs' / (hash + '.bin')

    def hash_file(self, path):
        path = path.resolve()
        stat = path.stat()
        stamp = [stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns]
        old = self.fingerprints.get(str(path))
        if old and old['stamp'] == stamp:
            return old['hash']
        value = digest(path.read_bytes())
        self.fingerprints[str(path)] = dict(stamp=stamp, hash=value)
        return value

    def run(self, key, category, inputs, action, reload='visual'):
        inputs = sorted(set(Path(p).resolve() for p in inputs))
        fingerprint = digest(encode([COOKER, key, category, [(str(p), self.hash_file(p)) for p in inputs]]))
        old = self.jobs.get(key)
        if old and old['fingerprint'] == fingerprint and all(self.blob(r['hash']).is_file() for r in old['outputs'].values()):
            job = old
            self.reused.append(key)
        else:
            sink = Sink(self)
            action(sink)
            job = dict(fingerprint=fingerprint, category=category, reload=reload,
                       inputs=[str(p) for p in inputs], outputs=sink.outputs)
            self.cooked.append(key)
        self.next_jobs[key] = job
        self.records.update(job['outputs'])

    def save(self):
        atomic(self.state_file, encode(dict(cooker=COOKER, files=self.fingerprints, jobs=self.next_jobs)))


class Sink:
    def __init__(self, cache):
        self.cache = cache
        self.old = cache.records
        self.outputs = {}

    def add(self, key, data):
        valid_key(key)
        hash = digest(data)
        file = self.cache.blob(hash)
        if not file.exists():
            atomic(file, data)
        self.outputs[key] = dict(hash=hash, size=len(data))

    def reuse(self, key):
        row = self.old[key]
        if not self.cache.blob(row['hash']).is_file():
            raise ValueError('Missing cached payload; rebuild cache: ' + key)
        self.outputs[key] = row


def category_for(name):
    text = name.lower()
    if any(s in text for s in ('rifle', 'weapon', 'pistol')):
        return 'weapons'
    if any(s in text for s in ('veg-', 'tree', 'grass', 'foliage', 'canopy')):
        return 'vegetation'
    if any(s in text for s in ('ancient', 'atlantis', 'outpost')):
        return 'environment-outpost'
    return 'environment-world'


def collect(cache, root, vegetation):
    from cook_terrain_blocks import collect as terrain
    consumed = terrain(cache, root)
    for path in sorted((root / 'models').glob('*.json')) + sorted(vegetation.glob('*.json')):
        doc = json.loads(path.read_text())
        if 'parts' not in doc:
            cache.run('models/' + path.name, category_for(path.stem), [path], lambda sink, p=path: sink.add('models/' + p.name, p.read_bytes()))
            continue
        inputs = [path]
        for part in doc['parts']:
            for field in ('mesh', 'texture', 'normal', 'metallicRoughness', 'emissionTexture'):
                if part.get(field):
                    key = valid_key(part[field])
                    file = vegetation / key
                    inputs.append(file if file.exists() else root / 'models' / key)
        cache.run('models/' + path.name, category_for(path.stem), inputs,
                  lambda sink, p=path: cook(root, cache.root/'unused', vegetation, sink=sink, model_paths=[p], character_paths=[], include_foliage=False, include_prototypes=False))
    for path in sorted((root / 'characters').glob('*.gz')):
        key = 'characters/' + path.name
        previous = cache.jobs.get(key)
        # Unchanged character descriptors need not be decompressed just to discover dependencies.
        old_file = cache.fingerprints.get(str(path.resolve()))
        old_hash = old_file['hash'] if old_file else None
        same = cache.hash_file(path) == old_hash
        if previous and same:
            inputs = [Path(p) for p in previous['inputs']]
        else:
            try:
                doc = json.loads(gzip.decompress(path.read_bytes()))
            except (OSError, ValueError) as error:
                raise ValueError('Invalid character source: ' + str(path)) from error
            if not isinstance(doc, dict) or 'parts' not in doc:
                continue
            inputs = [path] + [path.parent / valid_key(p[field]) for p in doc['parts']
                               for field in ('albedo', 'normal', 'metallicRoughness') if p.get(field)]
        cache.run(key, 'weapons' if category_for(path.name) == 'weapons' else 'characters', inputs,
                  lambda sink, p=path: cook(root, cache.root/'unused', vegetation, sink=sink, model_paths=[], character_paths=[p], include_foliage=False, include_prototypes=False), reload='session')
    foliage = root / 'foliage' / 'manifest.json'
    if foliage.exists():
        doc = json.loads(foliage.read_text())
        inputs = [foliage] + [foliage.parent / valid_key(m['file']) for m in doc['materials']] + list(foliage.parent.glob('*.mesh'))
        cache.run('foliage/manifest.json', 'vegetation', inputs,
                  lambda sink: cook(root, cache.root/'unused', vegetation, sink=sink, model_paths=[], character_paths=[], include_prototypes=False))
    for path in sorted(root.rglob('*')):
        if not path.is_file():
            continue
        key = path.relative_to(root).as_posix()
        if key in consumed: continue
        if key.split('/')[0] in ('models', 'foliage'):
            continue
        if key.startswith('characters/') and path.suffix != '.json':
            continue
        if key in ('models.sga', 'cooked.required') or key.startswith('packs/'):
            continue
        category = 'vegetation' if key.startswith('vegetation/') else 'bootstrap' if key.startswith(('gui/', 'fonts/', 'shaders/')) else 'shared'
        reload = 'session' if key.endswith(('.sgc',)) or key.startswith(('collision/', 'building/')) else 'visual'
        cache.run(key, category, [path], lambda sink, p=path, k=key: sink.add(k, p.read_bytes()), reload)


def records(cache):
    entries, owners, assets = {}, {}, {}
    for key, job in sorted(cache.next_jobs.items()):
        for name, row in job['outputs'].items():
            if name in entries and entries[name] != row:
                raise ValueError('Conflicting asset key: ' + name)
            entries[name] = row
            owners.setdefault(name, set()).add(job['category'])
        dependencies = set(job['outputs'])
        metadata = {}
        if key.endswith('.json') and key in job['outputs']:
            doc = json.loads(cache.blob(job['outputs'][key]['hash']).read_bytes())
            if isinstance(doc, dict):
                dependencies.update(doc.get('dependencies', []))
                parts = doc.get('parts', [])
                bounded = [p for p in parts if 'boundsMin' in p]
                if bounded:
                    metadata['boundsMin'] = [min(p['boundsMin'][i] for p in bounded) for i in range(3)]
                    metadata['boundsMax'] = [max(p['boundsMax'][i] for p in bounded) for i in range(3)]
        assets[key] = dict(category=job['category'], dependencies=sorted(dependencies), reload=job['reload'], **metadata)
    categories = {k: next(iter(v)) if len(v) == 1 else 'shared' for k, v in owners.items()}
    return entries, categories, assets


def read_pack(path, verify=True):
    with path.open('rb') as f:
        header = f.read(48)
        magic, offset, length = struct.unpack('<4sqi', header[:16])
        if magic != b'SGA1' or offset < 48 or not 2 <= length <= 32*1024*1024:
            raise ValueError('Invalid pack header')
        f.seek(offset)
        raw = f.read(length)
        if digest(raw) != header[16:].hex():
            raise ValueError('Invalid pack index')
        index = json.loads(raw)
        if verify:
            checked = set()
            for key, row in index.items():
                valid_key(key)
                if row['Offset'] < 48 or row['Stored'] < 0 or row['Offset'] + row['Stored'] > offset:
                    raise ValueError('Invalid block bounds')
                if row['Offset'] in checked:
                    continue
                checked.add(row['Offset'])
                f.seek(row['Offset']); data = f.read(row['Stored'])
                if row['Brotli']:
                    data = brotli.decompress(data)
                if len(data) != row['Size'] or digest(data) != row['Hash']:
                    raise ValueError('Invalid block: ' + key)
        return index, digest(raw)


def write_pack(path, entries, cache, raw=False):
    temp = path.with_suffix('.tmp')
    index, dedup = {}, {}
    with temp.open('wb') as f:
        f.write(bytes(48))
        for key, row in sorted(entries.items()):
            if row['hash'] in dedup:
                index[key] = dedup[row['hash']]
                continue
            data = cache.blob(row['hash']).read_bytes()
            if digest(data) != row['hash'] or len(data) != row['size']:
                raise ValueError('Corrupt cook cache: ' + key)
            compressed = brotli.compress(data, quality=4) if not raw and not key.endswith(('.bc7', '.bc5')) else data
            use = len(compressed) < len(data)
            stored = compressed if use else data
            index[key] = dict(Offset=f.tell(), Stored=len(stored), Size=len(data), Hash=row['hash'], Brotli=use)
            dedup[row['hash']] = index[key]
            f.write(stored)
        offset = f.tell(); encoded = encode(index); f.write(encoded)
        f.seek(0); f.write(struct.pack('<4sqi', b'SGA1', offset, len(encoded)) + hashlib.sha256(encoded).digest())
        f.flush(); os.fsync(f.fileno())
    read_pack(temp)
    os.replace(temp, path)
    return digest(encoded)


def publish_base(output, cache, entries, owners, assets):
    output.mkdir(parents=True, exist_ok=True)
    packs, written = [], []
    for category in sorted(set(owners.values())):
        contents = {k: r for k, r in entries.items() if owners[k] == category}
        identity = digest(encode([VERSION, category, contents]))
        file = category + '-' + identity[:20] + '.sga'
        path = output / file
        if not path.exists():
            index_hash = write_pack(path, contents, cache, raw=category == 'bootstrap')
            written.append(category)
        else:
            _, index_hash = read_pack(path, verify=False)
        packs.append(dict(name=category, file=file, indexHash=index_hash))
    base = dict(version=VERSION, cooker=COOKER, entries=entries, assets=assets, packs=packs, patches=[])
    base['id'] = digest(encode([COOKER, entries, assets]))
    atomic(output/'catalog.json', encode(base))
    return written


def publish_override(output, override, cache, entries, assets):
    base = json.loads((output/'catalog.json').read_text())
    if base['version'] != VERSION or base['cooker'] != COOKER:
        raise ValueError('Base pack/cooker mismatch. Rebuild base packs before creating overrides.')
    changed = {k: v for k, v in entries.items() if base['entries'].get(k) != v}
    deleted = sorted(set(base['entries']) - set(entries))
    override.mkdir(parents=True, exist_ok=True)
    for row in changed.values():
        path = override/'blobs'/(row['hash']+'.bin')
        if not path.exists():
            atomic(path, cache.blob(row['hash']).read_bytes())
        if path.stat().st_size != row['size'] or digest(path.read_bytes()) != row['hash']:
            raise ValueError('Corrupt overlay payload')
    affected = {k: a for k, a in assets.items() if a != base['assets'].get(k) or any(d in changed or d in deleted for d in a['dependencies'])}
    for key in set(base['assets']) - set(assets):
        affected[key] = dict(base['assets'][key], deleted=True)
    manifest = dict(version=VERSION, cooker=COOKER, baseId=base['id'], entries=changed, tombstones=deleted, assets=affected)
    manifest['generation'] = digest(encode(manifest))
    # No online deletion: older generations may still be in use by a client.
    atomic(override/'manifest.json', encode(manifest))
    return len(changed)


def build(root, vegetation, output, cache_root, override=None):
    cache_root.mkdir(parents=True, exist_ok=True)
    with cache_lock(cache_root/'build.lock'):
        started = time.perf_counter()
        cache = Cache(cache_root)
        collect(cache, root.resolve(), vegetation.resolve())
        entries, owners, assets = records(cache)
        packs = publish_base(output, cache, entries, owners, assets) if override is None else []
        changed = publish_override(output, override, cache, entries, assets) if override else 0
        cache.save()
        result = dict(cooked=cache.cooked, reused=len(cache.reused), packsWritten=packs,
                      overrideBlocks=changed, seconds=round(time.perf_counter()-started, 3))
        atomic(cache_root/'last-build.json', encode(result))
        print(json.dumps(dict(result, cookedCount=len(result['cooked']), cooked=result['cooked'][:12])), flush=True)
        return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assets', type=Path, default=Path('assets'))
    parser.add_argument('--vegetation', type=Path, default=Path('artifacts/cooked/models'))
    parser.add_argument('--output', type=Path, default=Path('artifacts/cooked/packs'))
    parser.add_argument('--cache', type=Path, default=Path('artifacts/cooked/asset-cache'))
    parser.add_argument('--dev-override', type=Path)
    args = parser.parse_args()
    build(args.assets, args.vegetation, args.output, args.cache, args.dev_override)
