"""Validate exported rig data without a GPU. Run after import_characters.py."""

import gzip, json, pathlib, sys
import numpy as np

root = pathlib.Path(__file__).resolve().parents[1]
directory = (
    pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else root / "assets/characters"
)
report = []
for file in sorted(directory.glob("*.sga.gz")):
    d = json.load(gzip.open(file))
    assert d["version"] == 2 and d["animationSpace"] == "local"
    bones = d["bones"]
    parents = d["parents"]
    count = len(bones)
    assert 0 < count <= 128 and len(parents) == count
    assert all(-1 <= p < i for i, p in enumerate(parents))
    inverse = np.array(d["inverseBind"]).reshape(count, 4, 4)
    assert np.isfinite(inverse).all() and np.all(np.abs(np.linalg.det(inverse)) > 1e-10)
    row = {"package": file.name, "bones": count, "triangles": {}, "clips": []}
    if d["name"] in ("military", "retro-rifle"):
        assert {"crouch-idle", "crouch-walk"} <= {c["name"] for c in d["clips"]}, 'Missing tactical crouch clips; reimport character sources'
    for part in d["parts"]:
        vertices = np.array(part["positions"]).reshape(-1, 3)
        normals = np.array(part["normals"]).reshape(-1, 3)
        weights = np.array(part["weights"]).reshape(-1, 4)
        joints = np.array(part["joints"]).reshape(-1, 4)
        assert (
            np.isfinite(vertices).all()
            and np.isfinite(normals).all()
            and np.isfinite(weights).all()
        )
        assert vertices.shape == normals.shape and len(vertices) == len(weights) == len(
            joints
        )
        assert np.max(np.abs(weights.sum(axis=1) - 1)) < 1e-5 and weights.min() >= 0
        assert (
            joints.min() >= 0
            and joints.max() < count
            and np.all(joints == np.floor(joints))
        )
        assert (
            len(part["indices"]) % 3 == 0
            and min(part["indices"]) >= 0
            and max(part["indices"]) < len(vertices)
        )
        assert np.max(np.abs(np.linalg.norm(normals, axis=1) - 1)) < 1e-4
        key = str(part["lod"])
        row["triangles"][key] = row["triangles"].get(key, 0) + len(part["indices"]) // 3
    for clip in d["clips"]:
        local = np.array(clip["matrices"]).reshape(clip["frames"], count, 4, 4)
        assert np.isfinite(local).all() and clip["duration"] > 0
        world = np.zeros_like(local)
        for i, parent in enumerate(parents):
            world[:, i] = local[:, i] if parent < 0 else world[:, parent] @ local[:, i]
        skin = world @ inverse
        jump = float(
            np.max(np.linalg.norm(np.diff(world[:, :, :3, 3], axis=0), axis=-1))
        )
        lengths = np.linalg.norm(local[:, :, :3, 3], axis=-1)
        drift = float(np.ptp(lengths[:, np.array(parents) >= 0], axis=0).max())
        assert (
            jump < 0.8
        ), f'{file.name}:{clip["name"]} discontinuous joint motion: {jump}m/frame'
        if d["name"] == "military":
            assert drift < 0.0001, f'{clip["name"]} changes bone lengths'
        if clip["name"] == "bind":
            assert np.max(np.abs(skin - np.eye(4))) < 0.0001
        # Bound every LOD at several poses, including both sides of the loop seam.
        bounds = []
        for frame in sorted(
            set([0, 1, clip["frames"] // 2, clip["frames"] - 2, clip["frames"] - 1])
        ):
            for part in d["parts"]:
                p = np.array(part["positions"]).reshape(-1, 3)
                v = np.column_stack([p, np.ones(len(p))])
                j = np.array(part["joints"]).reshape(-1, 4).astype(int)
                w = np.array(part["weights"]).reshape(-1, 4)
                posed = sum(
                    np.einsum("nij,nj->ni", skin[frame, j[:, k]], v) * w[:, k, None]
                    for k in range(4)
                )[:, :3]
                assert np.isfinite(posed).all() and np.max(np.abs(posed)) < 10
                bounds.append((posed.min(axis=0), posed.max(axis=0)))
        loop_delta = float(
            np.max(np.linalg.norm(world[0, :, :3, 3] - world[-1, :, :3, 3], axis=-1))
        )
        if d["name"] == "military" and clip["name"] not in ("jump", "land"):
            assert (
                loop_delta < 0.01
            ), f'{clip["name"]} has a visible loop discontinuity: {loop_delta}m'
        row["clips"].append(
            {
                "name": clip["name"],
                "frames": clip["frames"],
                "maxJointStepMetres": jump,
                "boneLengthDriftMetres": drift,
                "loopPositionDeltaMetres": loop_delta,
            }
        )
    report.append(row)
out = root / "artifacts/character-fixes/asset-validation.json"
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps(report, indent=2))
print(out)
for row in report:
    print(row["package"], row["bones"], "bones", row["triangles"])
