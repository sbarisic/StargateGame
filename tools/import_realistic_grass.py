"""Import the six non-wheat clumps from Nicholas-3D's grass pack.

Run after import_ground_foliage.py. Retains the existing ferns/dry bushes and
their notice; originals are read-only. Outputs use the existing foliage cooker.
"""

import gzip, hashlib, json, struct
from pathlib import Path
import numpy as np
from PIL import Image
from furniture_gltf import Scene
from import_terrain_pbr import linear, srgb, shrink, quantize

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "assets_itch/foliage/realistic_grass_pack"
OUT = ROOT / "assets/foliage"


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def material(name):
    paths = {
        k: SOURCE / "textures" / f"{name}_{k}.png" for k in ("baseColor", "normal")
    }
    arrays = {
        k: np.asarray(
            Image.open(p)
            .convert("RGBA")
            .resize((2048, 2048), Image.Resampling.LANCZOS),
            np.float32,
        )
        / 255
        for k, p in paths.items()
    }
    c = linear(arrays["baseColor"][:, :, :3]) * np.array([0.60, 0.64, 0.62])
    alpha = arrays["baseColor"][:, :, 3:4]
    n = arrays["normal"][:, :, :3] * 2 - 1
    n[:, :, 1] *= -1
    masks = np.stack(
        [
            np.ones(c.shape[:2]),
            np.full(c.shape[:2], 0.85),
            np.full(c.shape[:2], 0.5),
            np.zeros(c.shape[:2]),
        ],
        axis=-1,
    )
    coverage = float((alpha >= 0.4).mean())
    file = "realistic-" + name + ".tpb.gz"
    with (OUT / file).open("wb") as raw, gzip.GzipFile(
        filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=6
    ) as f:
        f.write(b"TPB1" + struct.pack("<II", 2048, 12))
        for level in range(12):
            norm = n / np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-8)
            adjusted = alpha
            if level and abs(float((alpha >= 0.4).mean()) - coverage) > 0.002:
                lo, hi = 0.25, 8.0
                for _ in range(16):
                    mid = (lo + hi) / 2
                    if (alpha * mid >= 0.4).mean() < coverage:
                        lo = mid
                    else:
                        hi = mid
                adjusted = np.clip(alpha * ((lo + hi) / 2), 0, 1)
            for a in [
                np.concatenate([srgb(c), adjusted], axis=-1),
                np.concatenate([norm * 0.5 + 0.5, np.ones_like(alpha)], axis=-1),
                masks,
            ]:
                f.write(np.flipud(quantize(a)).tobytes())
            if level < 11:
                c = shrink(c)
                alpha = shrink(alpha)
                n = shrink(n)
                masks = shrink(masks)
    return dict(
        name=name,
        file=file,
        alphaCutoff=0.4,
        linearColorGain=[0.60, 0.64, 0.62],
        sourceNormalConvention="OpenGL (glTF)",
        source={
            k: dict(file=str(p.relative_to(ROOT)).replace("\\", "/"), sha256=sha(p))
            for k, p in paths.items()
        },
        sha256=sha(OUT / file),
    )


def run():
    manifest = OUT / "manifest.json"
    doc = json.loads(manifest.read_text())
    # Slots 1 and 2 remain the original fern and dry-bush materials.
    old = doc["materials"]
    doc["materials"] = [
        material("Grass_A"),
        old[1],
        old[2],
        material("Grass_B"),
        material("Grass_C"),
    ]
    models = [m for m in doc["models"] if m["kind"] != "grass"]
    assert len(models) == 5
    scene = Scene(SOURCE / "scene.gltf")
    parts = scene.parts()
    for index in (5, 7, 6, 8, 3, 4):
        vertices, mat = parts[index]
        v = vertices.copy()
        lo = v[:, :3].min(0)
        hi = v[:, :3].max(0)
        height = hi[1] - lo[1]
        v[:, :3] = (
            v[:, :3] - np.array([(lo[0] + hi[0]) / 2, lo[1], (lo[2] + hi[2]) / 2])
        ) / height
        assert np.isfinite(v).all() and height > 0
        name = f"realistic-grass-{index}"
        entries = []
        tri = v.reshape(-1, 3, 8)
        # Reduce complete two-triangle cards only if each pair really is a quad.
        quads = len(tri) % 2 == 0 and all(
            len(np.unique(pair[:, :, :3].reshape(-1, 3), axis=0)) == 4
            for pair in tri.reshape(-1, 2, 3, 8)
        )
        for level in range(2):
            data = (
                tri.reshape(-1, 2, 3, 8)[::2].reshape(-1, 8) if level and quads else v
            )
            file = f"{name}-lod{level}.mesh"
            (OUT / file).write_bytes(
                b"SGM1"
                + struct.pack("<I", len(data))
                + np.asarray(data, dtype="<f4").tobytes()
            )
            entries.append(
                dict(file=file, triangles=len(data) // 3, sha256=sha(OUT / file))
            )
        models.append(
            dict(
                name=name,
                kind="grass",
                material={3: 0, 4: 3, 2: 4}[mat],
                sourceMesh=index,
                sourceHeightMetres=float(height),
                min=v[:, :3].min(0).tolist(),
                max=v[:, :3].max(0).tolist(),
                lods=entries,
            )
        )
        print(name, [e["triangles"] for e in entries], flush=True)
    doc["models"] = models
    doc["realisticGrass"] = dict(
        source=str(SOURCE.relative_to(ROOT)).replace("\\", "/"),
        sceneSha256=sha(SOURCE / "scene.gltf"),
        bufferSha256=sha(SOURCE / "scene.bin"),
        tool="tools/import_realistic_grass.py",
        license="CC-BY-4.0",
        author="Nicholas-3D",
        changes="Six non-wheat clumps; gallery transforms removed; unit-height Y-up; olive linear-space grade; alpha-tested, coverage-preserving mipmaps; whole-card distance LODs; matte nonmetallic surface",
    )
    manifest.write_text(json.dumps(doc, indent=2) + "\n")
    (OUT / "realistic-grass-license.txt").write_bytes(
        (SOURCE / "license.txt").read_bytes()
    )


if __name__ == "__main__":
    run()
