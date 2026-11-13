"""Check fitted kit openings against the actual runtime geometry."""
import json
import unittest
from pathlib import Path
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
MODELS = ROOT / 'assets/models'
CATALOG = {p['id']: p for p in json.loads((ROOT/'assets/building/prefabs.json').read_text())}


def covered(points, triangles):
    """Orthographic silhouette coverage, ignoring edge-on triangles."""
    result = np.zeros(len(points), dtype=bool)
    for tri in triangles[:, :, :2]:
        a, b = tri[1]-tri[0], tri[2]-tri[0]
        determinant = a[0]*b[1]-a[1]*b[0]
        if abs(determinant) < 1e-10:
            continue
        p = points-tri[0]
        u = (p[:, 0]*b[1]-p[:, 1]*b[0])/determinant
        v = (a[0]*p[:, 1]-a[1]*p[:, 0])/determinant
        result |= (u >= 0) & (v >= 0) & (u+v <= 1)
    return result


class OpeningChecks(unittest.TestCase):
    def test_closed_leaf_clears_real_stone_silhouette(self):
        leaf = np.array(CATALOG['kit-door-1-flat']['triangles']) + [-.53, 0, 0]
        lo, hi = leaf.min((0, 1)), leaf.max((0, 1))
        x, y = np.linspace(lo[0], hi[0], 65), np.linspace(lo[1], hi[1], 129)
        perimeter = np.concatenate([np.column_stack([x, np.full_like(x, h)]) for h in [lo[1], hi[1]]] +
                                   [np.column_stack([np.full_like(y, w), y]) for w in [lo[0], hi[0]]])
        frame = np.array(CATALOG['kit-doorframe-flat-brick']['triangles'])
        self.assertFalse(covered(perimeter, frame).any(), 'leaf overlaps the jamb, lintel or threshold')
        self.assertGreater(hi[0]-lo[0], 1.05, 'door should fill the opening, not leave a large gap')
        sections = np.array(CATALOG['kit-door-1-flat']['doorSections'])
        self.assertTrue(np.all(sections[:, 0] >= leaf.min((0, 1)) + [.53, 0, 0] - 1e-5))
        self.assertTrue(np.all(sections[:, 1] <= leaf.max((0, 1)) + [.53, 0, 0] + 1e-5))

    def test_window_has_no_masonry_band_inside_frame(self):
        x, y = np.meshgrid(np.linspace(-.44, .44, 25), np.linspace(1.15, 2.375, 50))
        points = np.column_stack([x.ravel(), y.ravel()])
        wall = np.array(CATALOG['kit-wall-unevenbrick-window-wide-flat']['triangles'])
        self.assertFalse(covered(points, wall).any(), 'masonry protrudes into the casement')

    def test_casement_fits_inside_outer_wood_frame(self):
        model = json.loads((MODELS/'kit-window-wide-flat1.json').read_text())
        wood = np.frombuffer((MODELS/model['parts'][0]['mesh']).read_bytes()[8:],
                             dtype='<f4').reshape(-1, 3, 8)[:, :, :3]
        thin = np.max(np.abs(wood[:, :, 2]), axis=1) < .06
        lo, hi = wood[thin].min((0, 1)), wood[thin].max((0, 1))
        trim = wood[~thin]
        left = trim[np.max(trim[:, :, 0], axis=1) < -.50, :, 0].max()
        right = trim[np.min(trim[:, :, 0], axis=1) > .50, :, 0].min()
        top = trim[np.min(trim[:, :, 1], axis=1) > 2.38, :, 1].min()
        bottom = trim[np.max(trim[:, :, 1], axis=1) < 1.09, :, 1].max()
        self.assertTrue(lo[0] > left and hi[0] < right and lo[1] > bottom and hi[1] < top,
                        'casement overlaps the surrounding trim')
        self.assertLess(max(lo[0]-left, right-hi[0], lo[1]-bottom, top-hi[1]), .01,
                        'casement leaves an excessive gap inside the trim')

    def test_joinery_relief_does_not_sink_bars_into_glass(self):
        for name in ['kit-window-wide-flat1', 'kit-door-1-flat']:
            model = json.loads((MODELS/(name+'.json')).read_text())
            wood = [p for p in model['parts'] if p.get('normal')]
            self.assertTrue(wood)
            self.assertTrue(all(p.get('heightScale', 0) <= .002 for p in wood))
            self.assertTrue(all(p.get('heightScaleLimit', 1) <= .002 for p in wood))


if __name__ == '__main__':
    unittest.main()
