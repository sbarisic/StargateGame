"""Import the scoped Blender refinement without changing base LODs or collision.
python tools/import_tree_detail.py --source <refinement cache directory>
"""

import argparse, hashlib, json, struct
from pathlib import Path
import numpy as np
from import_realistic_trees import read_glb

ROOT = Path(__file__).resolve().parents[1]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", type=Path, required=True)
    args = ap.parse_args()
    name = "MoreRealisticTree_01"
    models = ROOT / "assets/models"
    source = args.source / (name + "_detail.glb")
    report = json.loads((args.source / "detail-report.json").read_text())
    assert hashlib.sha256(source.read_bytes()).hexdigest() == report["outputSha256"]
    baseline = json.loads((models / (name + ".json")).read_text())["parts"]
    previous = json.loads((models / "realistic-trees-provenance.json").read_text())
    origin = np.array(
        next(m["planting_origin"] for m in previous["models"] if m["name"] == name),
        np.float32,
    )
    bounds = json.loads((models / "realistic-trees-bounds.json").read_text())[name]
    parts = read_glb(source)
    output = []
    files = {}
    total = 0
    for index, old in enumerate(baseline):
        wood = old.get("alphaCutoff", 0) == 0
        part = next(p for p in parts if p["wood"] == wood)
        positions = part["positions"] - origin
        uv = part["uv"].copy()
        uv[:, 1] = 1 - uv[:, 1]
        assert np.all(positions.min(0) >= np.array(bounds["min"]) - 0.02)
        assert np.all(positions.max(0) <= np.array(bounds["max"]) + 0.02)
        vertices = np.concatenate([positions, part["normals"], uv], axis=1)[
            part["triangles"].flatten()
        ].astype("<f4")
        assert np.isfinite(vertices).all()
        filename = f"{name}_detail_{index}.mesh"
        data = b"SGM1" + struct.pack("<I", len(vertices)) + vertices.tobytes()
        files[filename] = data
        total += len(vertices) // 3
        output.append(dict(old, mesh=filename))
    assert 90000 <= total <= 105000, total
    for filename, data in files.items():
        (models / filename).write_bytes(data)
    (models / (name + "_detail.json")).write_text(
        json.dumps(dict(version=1, parts=output), indent=2) + "\n"
    )
    report.update(
        triangles=total,
        plantingOrigin=origin.tolist(),
        importer="tools/import_tree_detail.py",
        outputs={n: hashlib.sha256(b).hexdigest() for n, b in files.items()},
        scope="Near render only. Original LOD sources, bounds, collision and IDs unchanged.",
    )
    (ROOT / "assets/vegetation/tree-detail-provenance.json").write_text(
        json.dumps(report, indent=2) + "\n"
    )
    print(
        f"Imported {name}: {total:,} near triangles; retained all base assets and collision."
    )


if __name__ == "__main__":
    main()
