"""Fit the kit casement and wall cutout to the existing outer wooden frame.

Called before assemblies are baked. Versioned in the source prefab catalog so
repeated builds do not progressively scale geometry. Render and collision
vertices receive the same transform.
"""
import json
import numpy as np


def fit_openings(catalog, models):
    by_id = {p['id']: p for p in catalog}
    window = by_id['kit-window-wide-flat1']
    wall = by_id['kit-wall-unevenbrick-window-wide-flat']

    def apply(prefab, transform, joinery=False):
        model_path = models / (prefab['id'] + '.json')
        model = json.loads(model_path.read_text())
        for part in model['parts']:
            path = models / part['mesh']; raw = path.read_bytes()
            vertices = np.frombuffer(raw[8:], dtype='<f4').reshape(-1, 3, 8).copy()
            transform(vertices)
            path.write_bytes(raw[:8] + vertices.astype('<f4').tobytes())
            if joinery and part.get('normal'):
                # Three centimetres of POM submerged the thin bars in the glass.
                part['heightScaleLimit'] = .002
                part['heightScale'] = min(part.get('heightScale', .002), .002)
        model_path.write_text(json.dumps(model, indent=2) + '\n')
        triangles = np.array(prefab['triangles'], dtype=float)
        vertices = np.zeros((*triangles.shape[:2], 8))
        vertices[:, :, :3] = triangles
        transform(vertices)
        triangles = vertices[:, :, :3]
        prefab.update(triangles=triangles.tolist(), min=triangles.min((0, 1)).tolist(),
                      max=triangles.max((0, 1)).tolist(), openingFitVersion=1)

    if window.get('openingFitVersion') != 1:
        # Thin casement and glazing are separate from the deep outer trim/sill.
        triangles = np.array(window['triangles'])
        thin = np.max(np.abs(triangles[:, :, 2]), axis=1) < .06
        lo, hi = triangles[thin].min((0, 1)), triangles[thin].max((0, 1))
        target_lo = np.array([-.5256, 1.089, lo[2]])
        target_hi = np.array([.5256, 2.3795, hi[2]])
        scale = (target_hi-target_lo)/(hi-lo)

        def casement(v):
            chosen = np.max(np.abs(v[:, :, 2]), axis=1) < .06
            v[chosen, :, :3] = target_lo + (v[chosen, :, :3]-lo)*scale
            n = v[chosen, :, 3:6] / scale
            v[chosen, :, 3:6] = n/np.maximum(np.linalg.norm(n, axis=2)[:, :, None], 1e-12)
        apply(window, casement, joinery=True)

    if wall.get('openingFitVersion') != 1:
        # The original cutout stopped below the top of the casement, leaving a
        # visible strip of masonry inside the wooden frame. Trim hides this seam.
        def opening(v):
            x, y = v[:, :, 0], v[:, :, 1]
            x[np.abs(np.abs(x)-.5985) < .004] = np.sign(x[np.abs(np.abs(x)-.5985) < .004])*.54
            y[np.abs(y-1.046) < .002] = 1.08
            y[np.abs(y-2.314) < .002] = 2.39
        apply(wall, opening)
    return catalog
