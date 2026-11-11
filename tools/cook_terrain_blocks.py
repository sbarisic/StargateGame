"""Split existing terrain bake containers into independently readable mip payloads."""
import gzip
import json
import struct


def collect(cache, root):
    manifest = root/'terrain/pbr/manifest.json'
    if not manifest.exists(): return set()
    doc = json.loads(manifest.read_text())
    consumed = {'terrain/pbr/manifest.json'}
    dependencies = []
    for name, material in doc['materials'].items():
        source = manifest.parent/material['file']
        descriptor = 'terrain/pbr/'+material['file']+'.json'
        consumed.add('terrain/pbr/'+material['file'])
        def cook(sink, source=source, descriptor=descriptor):
            levels = []
            with gzip.open(source, 'rb') as file:
                magic, size, count = struct.unpack('<4sii', file.read(12))
                if magic != b'TPB1' or size != 2048 or count != 12: raise ValueError('Invalid terrain bake: '+str(source))
                for mip in range(count):
                    maps = []
                    side = max(1, size >> mip)
                    for channel in range(3):
                        data = file.read(side*side*4)
                        if len(data) != side*side*4: raise ValueError('Truncated terrain bake')
                        key = descriptor+f'.{mip}.{channel}'
                        sink.add(key, data); maps.append(key)
                    levels.append(maps)
            from cook_assets import encode
            sink.add(descriptor, encode(dict(size=size, levels=levels)))
        cache.run(descriptor, 'environment-terrain', [source], cook)
        material['cookedDescriptor'] = descriptor
        dependencies.append(descriptor)
    doc['dependencies'] = dependencies
    from cook_assets import encode
    cache.run('terrain/pbr/manifest.json', 'environment-terrain', [manifest],
              lambda sink: sink.add('terrain/pbr/manifest.json', encode(doc)))
    return consumed
