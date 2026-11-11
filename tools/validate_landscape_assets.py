"""Validate the imported landscape assets without modifying sources or outputs."""

from pathlib import Path
import gzip, hashlib, json, struct
import numpy as np

ROOT = Path(__file__).resolve().parents[1]


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def texture(path, expected, foliage=False):
    assert sha(path) == expected, path
    coverage = []
    with gzip.open(path, "rb") as f:
        assert f.read(4) == b"TPB1" and struct.unpack("<II", f.read(8)) == (2048, 12)
        for level in range(12):
            side = 2048 >> level
            c, n, m = [
                np.frombuffer(f.read(side * side * 4), dtype=np.uint8).reshape(
                    side, side, 4
                )
                for _ in range(3)
            ]
            assert not m[:, :, 3].any(), (path, "metallic channel lost")
            length = np.linalg.norm(n[:, :, :3].astype(np.float32) / 127.5 - 1, axis=-1)
            assert np.max(np.abs(length - 1)) < 0.02, (
                path,
                "normal mip not unit length",
            )
            if not foliage:
                assert (c[:, :, 3] == 255).all()
            coverage.append(float((c[:, :, 3] >= 102).mean()))
        assert f.read() == b""
    if foliage:
        assert abs(coverage[0] - coverage[1]) < 0.003, (path, "alpha coverage lost")
    return coverage


def main():
    terrain = ROOT / "assets/terrain/pbr"
    foliage = ROOT / "assets/foliage"
    t = json.loads((terrain / "manifest.json").read_text())
    f = json.loads((foliage / "manifest.json").read_text())
    assert sha(ROOT / f["source"] / "scene.gltf") == f["sceneSha256"]
    assert sha(ROOT / f["source"] / "scene.bin") == f["bufferSha256"]
    assert (
        all(len(p) == 8 for p in t["palettes"].values())
        and all(e["material"] == "rocky-rugged-terrain-pbr" and e["metres"] == 2 for e in t["palettes"]["temperate"][:2])
    )
    for name, m in t["materials"].items():
        for source in m["source"].values():
            if "path" in source:
                assert sha(ROOT / source["path"]) == source["sha256"], source
        texture(terrain / m["file"], m["sha256"])
    assert len(f["models"]) == 11
    counts = {
        k: sum(m["kind"] == k for m in f["models"]) for k in ("grass", "fern", "bush")
    }
    assert counts == {"grass": 6, "fern": 2, "bush": 3}
    for m in f["models"]:
        assert 0 < m["lods"][1]["triangles"] <= m["lods"][0]["triangles"]
        for lod in m["lods"]:
            p = foliage / lod["file"]
            assert sha(p) == lod["sha256"]
            raw = p.read_bytes()
            assert raw[:4] == b"SGM1"
            count = struct.unpack("<I", raw[4:8])[0]
            v = np.frombuffer(raw[8:], dtype="<f4").reshape(count, 8)
            assert count == lod["triangles"] * 3 and np.isfinite(v).all()
            assert v[:, 1].min() >= -0.001 and v[:, 1].max() <= 1.001
            assert np.max(np.abs(np.linalg.norm(v[:, 3:6], axis=1) - 1)) < 0.02
    coverage = {}
    for m in f["materials"]:
        for source in m["source"].values():
            assert sha(ROOT / source["file"]) == source["sha256"]
        coverage[m["name"]] = texture(foliage / m["file"], m["sha256"], True)
    result = {
        "passed": True,
        "terrainMaterials": len(t["materials"]),
        "palettes": 3,
        "layersPerPalette": 8,
        "species": counts,
        "meshLods": sum(len(m["lods"]) for m in f["models"]),
        "alphaCoverage": coverage,
        "checks": [
            "source and output hashes",
            "finite normalized geometry",
            "nonincreasing mesh LODs",
            "unit normal mipmaps",
            "metallic alpha preserved as zero",
            "albedo alpha coverage",
            "complete mip chains",
        ],
    }
    out = ROOT / "artifacts/landscape-validation/assets.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))


if __name__ == "__main__":
    main()
