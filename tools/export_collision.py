"""Deterministic collision export from checked-in SGM1 meshes; no Blender required."""

from pathlib import Path
import hashlib, json, math, struct

root = Path(__file__).resolve().parents[1]
out = root / "assets" / "collision"
out.mkdir(parents=True, exist_ok=True)
selection = {
    "stargate": {"SM_Stargate_Segment", "SM_Stargate_GlyphTrack"},
    "dhd": {"SM_DHD_Base", "SM_DHD_Dome"},
}
report = {}
for model, names in selection.items():
    manifest = json.loads((root / "assets" / "models" / f"{model}.json").read_text())
    vertices, triangles, lookup, seen, sources = [], [], {}, set(), {}
    for part in manifest["parts"]:
        if part["name"] not in names:
            continue
        data = (root / "assets" / "models" / part["mesh"]).read_bytes()
        sources[part["mesh"]] = hashlib.sha256(data).hexdigest()
        magic, count = struct.unpack_from("<4sI", data)
        assert magic == b"SGM1" and len(data) == 8 + 32 * count and count % 3 == 0
        for start in range(0, count, 3):
            points = [
                struct.unpack_from("<3f", data, 8 + 32 * (start + i)) for i in range(3)
            ]
            assert all(math.isfinite(v) for p in points for v in p)
            # Weld within one micrometre; retain the first source position.
            ids = []
            for p in points:
                key = tuple(round(v, 6) for v in p)
                if key not in lookup:
                    lookup[key] = len(vertices)
                    vertices.append(p)
                ids.append(lookup[key])
            a, b, c = [vertices[i] for i in ids]
            u, v = [b[i] - a[i] for i in range(3)], [c[i] - a[i] for i in range(3)]
            cross = (
                u[1] * v[2] - u[2] * v[1],
                u[2] * v[0] - u[0] * v[2],
                u[0] * v[1] - u[1] * v[0],
            )
            key = tuple(sorted(ids))
            if (
                len(set(ids)) == 3
                and sum(x * x for x in cross) > 1e-14
                and key not in seen
            ):
                triangles.append(ids)
                seen.add(key)
    assert vertices and triangles
    data = struct.pack("<4sII", b"SGC1", len(vertices), len(triangles))
    data += b"".join(struct.pack("<3f", *p) for p in vertices)
    data += b"".join(struct.pack("<3I", *t) for t in triangles)
    (out / f"{model}.sgc").write_bytes(data)
    report[model] = {
        "parts": sorted(names),
        "vertices": len(vertices),
        "triangles": len(triangles),
        "sourceHashes": sources,
        "sha256": hashlib.sha256(data).hexdigest(),
    }
(out / "provenance.json").write_text(json.dumps(report, indent=2) + "\n")
print(json.dumps(report, indent=2))
