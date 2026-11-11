"""Sandbox comparison incline: same 1.35 m rise / 2.25 m run as one stair flight."""
import json
import struct
import numpy as np


def generate_reference(catalog, models):
    name = 'sandbox-stair-incline'
    points = np.array([[-.6, 0, 0], [.6, 0, 0], [-.6, 0, 2.25],
                       [.6, 0, 2.25], [-.6, 1.35, 2.25], [.6, 1.35, 2.25]])
    indices = [(0, 4, 5), (0, 5, 1), (0, 1, 3), (0, 3, 2),
               (0, 2, 4), (1, 5, 3), (2, 3, 5), (2, 5, 4)]
    rows, triangles = [], []
    for face in indices:
        triangle = points[list(face)]
        normal = np.cross(triangle[1]-triangle[0], triangle[2]-triangle[0])
        normal /= np.linalg.norm(normal)
        triangles.append(triangle.tolist())
        for p in triangle:
            uv = p[[0, 2]] / 2 if abs(normal[1]) > .5 else p[[2, 1]] / 2
            rows.append([*p, *normal, *uv])
    mesh = name + '.mesh'
    (models / mesh).write_bytes(b'SGM1' + struct.pack('<I', len(rows)) + np.array(rows, dtype='<f4').tobytes())
    stone = json.loads((models / 'kit-stone-foundation.json').read_text())['parts'][0]
    (models / (name + '.json')).write_text(json.dumps(dict(version=1, parts=[{**stone, 'mesh': mesh}]), indent=2) + '\n')
    return [p for p in catalog if p['id'] != name] + [dict(
        id=name, title='Stair comparison incline', group='Inspection', role='inspection',
        width=1.2, depth=2.25, height=1.35, ground=True, selectable=False,
        min=points.min(0).tolist(), max=points.max(0).tolist(), triangles=triangles)]


if __name__ == '__main__':
    from pathlib import Path
    root = Path(__file__).resolve().parents[1]
    path = root / 'assets/building/prefabs.json'
    catalog = generate_reference(json.loads(path.read_text()), root / 'assets/models')
    path.write_text(json.dumps(catalog, separators=(',', ':')) + '\n')
    print('Generated Sandbox stair comparison incline.')
