"""Wall-run base metadata and removal of faces buried by foundation/base solids.

Used by settlement export and the targeted refresh of already exported houses.
Clipping interpolates UVs/normals; exposed floor edges and door openings survive.
"""
import json
import struct
import sys
from pathlib import Path
import numpy as np


def exterior_depth(parts, collision):
    triangles = np.concatenate([v[:, :3].reshape(-1, 3, 3) for v, _ in parts])
    edge1, edge2 = triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0]
    depths = []
    for block in np.asarray(collision).reshape(-1, 12, 3, 3):
        lo, hi = block.min((0, 1)), block.max((0, 1))
        size = hi - lo
        if abs(lo[1]) > .025 or size[1] < 1 or min(size[0], size[2]) > .4:
            continue
        axis = 0 if size[0] < size[2] else 2
        center = (lo + hi) / 2
        sign = -1 if center[axis] < 0 else 1
        origin = center.copy()
        origin[1] = .5
        origin[axis] += sign * .8
        direction = np.zeros(3)
        direction[axis] = -sign
        h = np.cross(direction, edge2)
        determinant = np.sum(edge1 * h, axis=1)
        valid = np.abs(determinant) > 1e-8
        inverse = np.divide(1., determinant, out=np.zeros_like(determinant), where=valid)
        delta = origin - triangles[:, 0]
        u = np.sum(delta * h, axis=1) * inverse
        q = np.cross(delta, edge1)
        v = np.sum(direction * q, axis=1) * inverse
        distance = np.sum(edge2 * q, axis=1) * inverse
        valid &= (u >= -1e-5) & (v >= -1e-5) & (u + v <= 1.00001) & (distance > .2) & (distance < .8)
        if valid.any():
            depths.append(float(.8 - distance[valid].min()))
    if not depths:
        raise ValueError('House has no exterior wall-base surface samples')
    return max(depths)


def base_runs(collision, exterior):
    blocks = np.asarray(collision).reshape(-1, 12, 3, 3)
    floor_lo, floor_hi = blocks[0].min((0, 1)), blocks[0].max((0, 1))
    runs = []
    for block in blocks:
        lo, hi = block.min((0, 1)), block.max((0, 1))
        size = hi - lo
        if abs(lo[1]) > .025 or size[1] < 1 or min(size[0], size[2]) > .4:
            continue
        axis = 0 if size[0] < size[2] else 2
        along = 2 if axis == 0 else 0
        center = (lo[axis] + hi[axis]) / 2
        if center < 0:
            lo[axis] = center - exterior
        else:
            hi[axis] = center + exterior
        # Extend only at the actual outside corners, never across doorway jambs.
        if abs(lo[along] - floor_lo[along]) < .025:
            lo[along] -= exterior
        if abs(hi[along] - floor_hi[along]) < .025:
            hi[along] += exterior
        # The generated base includes the floor support down to the stone
        # footing bottom; its walkable top remains exactly at the floor datum.
        if center < 0:
            hi[axis] = center
        else:
            lo[axis] = center
        lo[1], hi[1] = -1.12, 0
        runs.append([lo.tolist(), hi.tolist()])
    return extend_thresholds(runs, collision)


def extend_thresholds(runs, collision):
    """Project an authored doorway header down between its supported jambs.

    Use neighboring footing extents, keeping the original top/bottom/material.
    This also supports refreshing existing generated-house metadata without
    re-exporting their unchanged render meshes.
    """
    result = list(runs)
    for block in np.asarray(collision).reshape(-1, 12, 3, 3):
        lo, hi = block.min((0, 1)), block.max((0, 1))
        size = hi-lo
        if lo[1] < 1.8 or min(size[0],size[2]) > .4:
            continue
        axis = 0 if size[0] < size[2] else 2
        along = 2 if axis == 0 else 0
        center = (lo[axis]+hi[axis])/2
        neighbors = [(np.array(a),np.array(b)) for a,b in runs
                     if a[axis]-.001 <= center <= b[axis]+.001
                     and (abs(b[along]-lo[along])<.025 or abs(a[along]-hi[along])<.025)]
        left = [r for r in neighbors if abs(r[1][along]-lo[along])<.025]
        right = [r for r in neighbors if abs(r[0][along]-hi[along])<.025]
        if not left or not right: continue
        a,b = left[0]
        start,end = a.copy(),b.copy()
        start[along],end[along] = lo[along],hi[along]
        box = [start.tolist(),end.tolist()]
        if box not in result: result.append(box)
    return result


def remove_buried(parts, solids):
    result = []
    seen = set()
    solids = [(np.asarray(lo), np.asarray(hi)) for lo, hi in solids]
    for vertices, material in parts:
        output = []
        for tri in vertices.reshape(-1, 3, 8):
            # Restrict this repair to the wall/foundation junction.
            if tri[:, 1].min() >= .00001:
                output.extend(tri)
                continue
            fragments = [tri]
            for lo, hi in solids:
                next_fragments = []
                for polygon in fragments:
                    if np.any(polygon[:, :3].max(0) < lo - 1e-6) or np.any(polygon[:, :3].min(0) > hi + 1e-6):
                        next_fragments.append(polygon)
                        continue
                    inside = polygon
                    for axis in range(3):
                        for upper in (False, True):
                            if len(inside) < 3:
                                break
                            d = inside[:, axis] - hi[axis] if upper else lo[axis] - inside[:, axis]
                            keep, outside = [], []
                            for i, a in enumerate(inside):
                                j = (i + 1) % len(inside)
                                b, da, db = inside[j], d[i], d[j]
                                (keep if da <= 1e-6 else outside).append(a)
                                if (da > 1e-6) != (db > 1e-6):
                                    point = a + (b - a) * da / (da - db)
                                    keep.append(point)
                                    outside.append(point)
                            if len(outside) >= 3:
                                next_fragments.append(np.asarray(outside))
                            inside = np.asarray(keep)
                fragments = next_fragments
            for polygon in fragments:
                for i in range(1, len(polygon) - 1):
                    face = polygon[[0, i, i + 1]]
                    if np.linalg.norm(np.cross(face[1, :3]-face[0, :3], face[2, :3]-face[0, :3])) < 1e-9:
                        continue
                    key = tuple(sorted(tuple(v) for v in np.round(face[:, :3], 5)))
                    if key in seen:
                        continue
                    seen.add(key)
                    face = face.copy()
                    lengths = np.linalg.norm(face[:, 3:6], axis=1)
                    face[:, 3:6] /= np.maximum(lengths[:, None], 1e-8)
                    output.extend(face)
        if output:
            result.append((np.asarray(output, dtype='<f4'), material))
    return result


def repair_house(parts, collision, exterior):
    runs = base_runs(collision, exterior)
    # Inset the floor solid: its visible top/edge must not be deleted.
    floor = np.asarray(collision[:12])
    lo, hi = floor.min((0, 1)) + .0001, floor.max((0, 1)) - .0001
    return remove_buried(parts, runs + [[lo, hi]]), runs


if __name__ == '__main__' and '--thresholds-only' in sys.argv:
    path = Path(__file__).resolve().parents[1] / 'assets/settlements/prefabs.json'
    catalog = json.loads(path.read_text())
    for prefab in catalog:
        if prefab.get('baseCourses'):
            prefab['baseCourses'] = extend_thresholds(prefab['baseCourses'], prefab['triangles'])
    path.write_text(json.dumps(catalog, separators=(',', ':'))+'\n')
    print('Extended generated-house footings beneath doorway thresholds.')
elif __name__ == '__main__':
    raise SystemExit('Restore from source with: python tools/import_settlements.py --houses-only; then regenerate house LODs. In-place repair cannot restore previously removed faces.')
