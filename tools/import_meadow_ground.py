"""Import a meadow PBR set into the existing terrain array; no terrain regeneration."""

import argparse, gzip, hashlib, json, struct
from pathlib import Path
import numpy as np
from PIL import Image
from import_terrain_pbr import linear, srgb, shrink, quantize, PARALLAX_DEPTHS

ROOT = Path(__file__).resolve().parents[1]


def run(source, material="rocky-rugged-terrain-pbr", slot="both"):
    out = ROOT / "assets/terrain/pbr"
    prefix = source / "Grass005_2K-PNG"
    paths = {
        k: Path(str(prefix) + "_" + v + ".png")
        for k, v in [
            ("albedo", "Color"),
            ("normal", "NormalDX"),
            ("ao", "AmbientOcclusion"),
            ("roughness", "Roughness"),
            ("height", "Displacement"),
        ]
    }
    if material != "scanned-meadow":
        prefix = source / (
            "leafy-grass2" if material == "leafy-grass2" else "rocky-rugged-terrain_1"
        )
        separator = "-" if material == "leafy-grass2" else "_"
        paths = {
            k: Path(str(prefix) + separator + v + ".png")
            for k, v in [
                ("albedo", "albedo"),
                ("normal", "normal-ogl"),
                ("ao", "ao"),
                ("roughness", "roughness"),
                ("height", "height"),
            ]
        }

    def read(k, mode):
        return (
            np.asarray(
                Image.open(paths[k])
                .convert(mode)
                .resize((2048, 2048), Image.Resampling.LANCZOS),
                np.float32,
            )
            / 255
        )

    color = linear(read("albedo", "RGB"))
    normal = read("normal", "RGB") * 2 - 1
    # Small, fixed colour adjustment towards the blade scan; retains natural soil variation.
    if material == "scanned-meadow":
        color *= np.array([0.82, 0.90, 0.90])
    else:
        normal[
            :, :, 1
        ] *= -1  # Explicitly labelled OpenGL source to runtime DirectX, once.
    masks = np.stack(
        [
            read("ao", "L"),
            read("roughness", "L"),
            read("height", "L"),
            np.zeros((2048, 2048)),
        ],
        axis=-1,
    )
    target = out / (material + ".tpb.gz")
    with target.open("wb") as raw, gzip.GzipFile(
        filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=6
    ) as f:
        f.write(b"TPB1" + struct.pack("<II", 2048, 12))
        for level in range(12):
            norm = normal / np.maximum(
                np.linalg.norm(normal, axis=-1, keepdims=True), 1e-8
            )
            for a in (
                np.concatenate([srgb(color), np.ones((*color.shape[:2], 1))], axis=-1),
                np.concatenate(
                    [norm * 0.5 + 0.5, np.ones((*norm.shape[:2], 1))], axis=-1
                ),
                masks,
            ):
                f.write(np.flipud(quantize(a)).tobytes())
            if level < 11:
                color = shrink(color)
                normal = shrink(normal)
                masks = shrink(masks)
    scanned = material == "scanned-meadow"
    manifest = out / "manifest.json"
    doc = json.loads(manifest.read_text())
    doc["materials"][material] = dict(
        file=target.name,
        normalConvention="DirectX",
        sourceNormalConvention="DirectX" if scanned else "OpenGL",
        normalStrength=0.55 if scanned else 0.7,
        albedoGain=0.40 if scanned else 0.65,
        heightBlendStrength=0.10 if scanned else 0.18,
        grassCoverage=1.0,
        sourceUrl=(
            "https://ambientcg.com/a/Grass005" if scanned else "https://freepbr.com/"
        ),
        license="CC0-1.0" if scanned else "See FreePBR-notice.txt",
        tool="tools/import_meadow_ground.py",
        source={
            k: dict(path=str(p), sha256=hashlib.sha256(p.read_bytes()).hexdigest())
            for k, p in paths.items()
        },
        sha256=hashlib.sha256(target.read_bytes()).hexdigest(),
    )
    doc["materials"][material]["parallaxDepthMetres"] = (
        0.035 if scanned else PARALLAX_DEPTHS[material]
    )
    meadow = [e for e in doc["palettes"]["temperate"] if e["role"] in ("meadow", "meadow-understory")]
    selected = (
        meadow
        if slot == "both"
        else (
            meadow[:1]
            if slot == "primary"
            else meadow[1:] if slot == "secondary" else []
        )
    )
    for entry in selected:
        entry["material"] = material
        entry["role"] = "meadow"
        entry["metres"] = 2.0
    manifest.write_text(json.dumps(doc, indent=2) + "\n")


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("source", type=Path)
    p.add_argument(
        "--material",
        choices=["scanned-meadow", "leafy-grass2", "rocky-rugged-terrain-pbr"],
        default="rocky-rugged-terrain-pbr",
    )
    p.add_argument(
        "--slot", choices=["both", "primary", "secondary", "none"], default="both"
    )
    args = p.parse_args()
    run(args.source, args.material, args.slot)
