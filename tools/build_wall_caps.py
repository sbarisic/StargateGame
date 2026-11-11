"""Generate hidden, closed masonry end caps from the wall's endpoint geometry."""
import json
import struct
import numpy as np


def generate_caps(catalog, models):
    catalog = [p for p in catalog if not p['id'].startswith('kit-endcap-')]
    # The first wall part is timber trim, not masonry.
    stone = next(p for p in json.loads((models / 'kit-wall-unevenbrick-straight.json').read_text())['parts']
                 if p.get('texture') in ('build-9f1cc6de8499-color.png', 'masonry-Bricks095-color.png', 'masonry-rustic_stone_wall-color.png'))
    for wall in list(catalog):
        if not wall['id'].startswith('kit-') or wall['role'] not in ('wall', 'window', 'doorway'):
            continue
        manifest = json.loads((models / (wall['id'] + '.json')).read_text())
        vertices = np.concatenate([np.frombuffer((models / part['mesh']).read_bytes()[8:], dtype='<f4').reshape(-1, 8)[:, :3] for part in manifest['parts']])
        for sign, side in ((-1, 'left'), (1, 'right')):
            endpoint = sign * wall['width'] / 2
            # Only the section at the wall end: window frames and door furniture
            # projecting out of the middle must not enlarge the cap.
            section = vertices[np.abs(vertices[:, 0] - endpoint) < .025]
            if not len(section):
                raise ValueError(f"No endpoint geometry for {wall['id']} {side}")
            lo, hi = section.min(0), section.max(0)
            lo[0], hi[0] = sorted((-sign * .012, sign * .001))
            points = np.array([[hi[0] if i & 1 else lo[0], hi[1] if i & 2 else lo[1], hi[2] if i & 4 else lo[2]] for i in range(8)])
            faces = ((0, 4, 6, 2), (1, 3, 7, 5), (0, 1, 5, 4), (2, 6, 7, 3), (0, 2, 3, 1), (4, 5, 7, 6))
            rows, triangles = [], []
            for face in faces:
                a, b, c = points[list(face[:3])]
                normal = np.cross(b-a, c-a)
                normal /= np.linalg.norm(normal)
                for indices in ((0, 1, 2), (0, 2, 3)):
                    tri = points[[face[i] for i in indices]]
                    triangles.append(tri.tolist())
                    for p in tri:
                        uv = p[[2, 1]] if abs(normal[0]) > .5 else p[[0, 2]] if abs(normal[1]) > .5 else p[[0, 1]]
                        rows.append([*p, *normal, *uv])
            key = f"kit-endcap-{wall['id']}-{side}"
            mesh = key + '.mesh'
            (models / mesh).write_bytes(b'SGM1' + struct.pack('<I', len(rows)) + np.array(rows, dtype='<f4').tobytes())
            (models / (key + '.json')).write_text(json.dumps(dict(version=1, parts=[{**stone, 'mesh': mesh, 'repeat': True}]), indent=2) + '\n')
            catalog.append(dict(id=key, title='Wall end cap', group='Building kit', role='trim', width=float(hi[0]-lo[0]), depth=float(hi[2]-lo[2]), height=float(hi[1]-lo[1]), ground=False, min=lo.tolist(), max=hi.tolist(), triangles=triangles, selectable=False))
    return catalog
