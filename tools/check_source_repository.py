"""Reject runtime artwork and generated output in the source repository's index."""
from pathlib import Path, PurePosixPath
import argparse
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
MEDIA = {'.glb', '.gltf', '.fbx', '.obj', '.mesh', '.blend', '.blend1', '.blend2',
         '.dae', '.stl', '.ply', '.sga', '.sgdi', '.sgc', '.gz', '.zip', '.7z', '.rar',
         '.png', '.jpg', '.jpeg', '.webp', '.dds', '.tga', '.bmp', '.gif', '.ico',
         '.wav', '.ogg', '.mp3', '.flac', '.mp4', '.webm', '.ttf', '.otf', '.woff',
         '.woff2', '.bin', '.exe', '.pdb', '.cache'}
METADATA = {'assets/settlements/templates.json', 'assets/furnishings/catalog.json',
            'assets/furnishings/interiors.json'}
NATIVE = {'native/runtimes/win-x64/native/joltc.dll',
          'native/runtimes/linux-x64/native/libjoltc.so',
          'thirdparty/FishGfx/FishGfx/glfw3.dll',
          'thirdparty/FishGfx/FishGfx_LiteTest/glfw3.dll'}


def allowed_asset(path):
    p = PurePosixPath(path)
    return (path == 'assets/.gitignore' or path.startswith('assets/shaders/')
            or p.suffix == '.md' or ('license' in path.lower() and p.suffix == '.txt')
            or ('provenance' in p.name and p.suffix == '.json')
            or p.name.endswith('manifest.json')
            or (path.startswith('assets/models/') and p.name.endswith('-bounds.json'))
            or path in METADATA)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        assert allowed_asset('assets/shaders/world.frag')
        assert allowed_asset('assets/models/rocks-bounds.json')
        assert not allowed_asset('assets/settlements/prefabs.json')
        assert not allowed_asset('assets/models/gate.json')
        print('Source policy checks passed.')
        return 0
    entries = subprocess.check_output(['git', '-C', str(ROOT), 'ls-files', '--stage', '-z']).decode().split('\0')
    blobs = [entry.split('\t', 1)[0].split()[1] for entry in entries if entry and not entry.startswith('160000 ')]
    sizes = subprocess.check_output(['git', '-C', str(ROOT), 'cat-file', '--batch-check=%(objectname) %(objectsize)'], input='\n'.join(blobs).encode()).decode().splitlines()
    sizes = {line.split()[0]: int(line.split()[1]) for line in sizes}
    bad = []; count = 0; total = 0
    for entry in filter(None, entries):
        metadata, path = entry.split('\t', 1)
        mode, blob, _ = metadata.split()
        if mode == '160000':
            continue  # Pinned upstream source dependencies; no asset blobs here.
        p = PurePosixPath(path); count += 1
        if (p.suffix.lower() in MEDIA
                or (p.suffix.lower() in {'.dll', '.so', '.dylib', '.lib', '.a'} and path not in NATIVE)
                or any(part in {'bin', 'obj', 'artifacts', 'assets_itch', 'saves', '.vs', '__pycache__'} for part in p.parts)
                or (path.startswith('assets/') and not allowed_asset(path))):
            bad.append(path)
        size = sizes[blob]
        total += size
        if size > 5 * 1024 * 1024:
            bad.append(path + ' (over 5 MiB)')
    if bad:
        print('Do not commit these runtime/generated assets:\n' + '\n'.join(bad), file=sys.stderr)
        return 1
    print(f'Source policy passed: {count} files, {total / 1024**2:.2f} MiB; no model/media payloads.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
