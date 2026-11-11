"""Build terrain texture-array layers with explicit, colour-correct mip chains.
Sources are read-only; numpy and Pillow are required. Runtime reads gzip TPB1 files.
"""

from pathlib import Path
import argparse, gzip, hashlib, json, struct
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "assets_itch/textures/freepbr_ground"
OUT = ROOT / "assets/terrain/pbr"
ADDED_SOURCES = {name: ROOT / "assets_itch/textures" / name for name in
                 ("marble_rock", "dry_riverbed_rock", "ground_sand", "ground_snow")}
PALETTES = {
    "temperate": [
        "rocky-rugged-terrain-pbr",
        "ground_sand",
        "rocky-rugged-terrain-pbr",
        "rocky-rugged-terrain-pbr",
        "mud-with-vegetation",
        "dry_riverbed_rock",
        "dry-brown-dirt-pbr-material",
        "jagged-rocky-ground-pbr",
    ],
    "desert": [
        "ground_sand",
        "sand-dunes",
        "sandy-dry-soil-pbr-material",
        "dry_riverbed_rock",
        "rocky-dunes1",
        "desert-rock1",
        "winding-desert-rock",
        "wet-stones-sand",
    ],
    "icy": [
        "ground_snow",
        "snow-packed-12",
        "crusted-snow2",
        "rocky-snow-ice",
        "ice-field-pbr",
        "jagged-rocky-ground-pbr",
        "pine-forest-ground",
        "damp-rocky-ground",
    ],
}
SCALES = {
    "temperate": [2, 2, 2, 2, 2, 2, 2, 8],
    "desert": [4, 4, 2, 2, 4, 2, 6, 2],
    "icy": [2, 2, 2, 2, 2, 8, 2, 2],
}
# The unlabelled dry-dirt normal was checked against its height gradients:
# X opposes increasing image columns; Y follows increasing image rows (OpenGL).
NORMAL_CONVENTIONS = {"dry-brown-dirt-pbr-material": "OpenGL",
                      **{name: "OpenGL" for name in ADDED_SOURCES}}
# Apparent slab depth in metres; height white is the top. No mesh/collision displacement.
PARALLAX_DEPTHS = {
    "jagged-rocky-ground-pbr": 0.08,
    "marble_rock": 0.06,
    "dry_riverbed_rock": 0.035,
    "ground_sand": 0.015,
    "ground_snow": 0.02,
    "scanned-meadow": 0.018,
    "leafy-grass2": 0.015,
    "patchy-meadow": 0.025,
    "wispy-grass-meadow": 0.02,
    "forest-floor1": 0.035,
    "mossy-mud1": 0.025,
    "mud-with-vegetation": 0.035,
    "river-rock-1": 0.10,
    "dry-brown-dirt-pbr-material": 0.035,
    "barren-ground-rock": 0.08,
    "rocky-rugged-terrain-pbr": 0.16,
    "wavy-sand": 0.015,
    "sand-dunes": 0.045,
    "sandy-dry-soil-pbr-material": 0.025,
    "dusty-ground-gravel-1": 0.04,
    "rocky-dunes1": 0.08,
    "desert-rock1": 0.10,
    "winding-desert-rock": 0.10,
    "wet-stones-sand": 0.07,
    "snow-drift": 0.035,
    "snow-packed-12": 0.02,
    "crusted-snow2": 0.025,
    "rocky-snow-ice": 0.06,
    "ice-field-pbr": 0.01,
    "pine-forest-ground": 0.03,
    "damp-rocky-ground": 0.07,
}


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def linear(x):
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def srgb(x):
    return np.where(
        x <= 0.0031308, x * 12.92, 1.055 * np.maximum(x, 0) ** (1 / 2.4) - 0.055
    )


def shrink(a):
    return (a[::2, ::2] + a[1::2, ::2] + a[::2, 1::2] + a[1::2, 1::2]) * 0.25


def quantize(a):
    return np.round(np.clip(a, 0, 1) * 255).astype("uint8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--only",
        nargs="+",
        help="Reimport selected materials; verify and reuse other manifest entries",
    )
    args = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    records = {}
    dedup = {}
    previous = (
        json.loads((OUT / "manifest.json").read_text())
        if (OUT / "manifest.json").exists()
        else None
    )
    for name in sorted(set(sum(PALETTES.values(), []))):
        if args.only and name not in args.only:
            record = previous["materials"][name]
            assert sha(OUT / record["file"]) == record["sha256"], name
            for source in record["source"].values():
                if "path" in source:
                    assert sha(ROOT / source["path"]) == source["sha256"], source[
                        "path"
                    ]
            records[name] = record
            continue
        source_root = ADDED_SOURCES.get(name, SOURCE / name)
        files = sorted(p for p in source_root.rglob("*") if p.suffix.lower() in (".png", ".jpg"))

        def pick(words):
            return next(
                (p for p in files if any(w in p.stem.lower() for w in words)), None
            )

        inputs = {
            "albedo": pick(["albedo", "base_color", "basecolor"]),
            "normal": pick(["normal"]),
            "ao": pick(["_ao", "-ao", "ambient_occlusion"]),
            "roughness": pick(["rough"]),
            "height": pick(["height", "displacement"]),
        }
        convention = NORMAL_CONVENTIONS.get(
            name, "OpenGL" if "ogl" in inputs["normal"].name.lower() else None
        )
        assert convention in ("OpenGL", "DirectX"), inputs["normal"]

        def read(p, mode):
            if p is None:
                return np.ones((2048, 2048), np.float32)
            with Image.open(p) as im:
                return (
                    np.asarray(
                        im.convert(mode).resize((2048, 2048), Image.Resampling.LANCZOS),
                        dtype=np.float32,
                    )
                    / 255
                )

        color = linear(read(inputs["albedo"], "RGB"))
        normal = read(inputs["normal"], "RGB") * 2 - 1
        if convention == "OpenGL":
            normal[:, :, 1] *= -1
        masks = np.stack(
            [
                read(inputs["ao"], "L"),
                read(inputs["roughness"], "L"),
                read(inputs["height"], "L"),
                np.zeros((2048, 2048), np.float32),
            ],
            axis=-1,
        )
        key = hashlib.sha256(
            "".join(sha(p) if p else "constant-one" for p in inputs.values()).encode()
        ).hexdigest()
        output = dedup.setdefault(key, name + ".tpb.gz")
        path = OUT / output
        if output == name + ".tpb.gz":
            with path.open("wb") as raw, gzip.GzipFile(
                filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=6
            ) as f:
                f.write(b"TPB1" + struct.pack("<II", 2048, 12))
                for level in range(12):
                    norm = normal / np.maximum(
                        np.linalg.norm(normal, axis=-1, keepdims=True), 1e-8
                    )
                    for a in [
                        np.concatenate(
                            [srgb(color), np.ones((*color.shape[:2], 1))], axis=-1
                        ),
                        np.concatenate(
                            [norm * 0.5 + 0.5, np.ones((*norm.shape[:2], 1))], axis=-1
                        ),
                        masks,
                    ]:
                        f.write(np.flipud(quantize(a)).tobytes())
                    if level < 11:
                        color = shrink(color)
                        normal = shrink(normal)
                        masks = shrink(masks)
        records[name] = {
            "file": output,
            "normalConvention": "DirectX",
            "normalStrength": 0.7,
            "albedoGain": {
                "rocky-rugged-terrain-pbr": 0.65,
                "patchy-meadow": 2.5,
                "wispy-grass-meadow": 1.3,
                "forest-floor1": 1.3,
                "mossy-mud1": 1.15,
            }.get(name, 1),
            "heightBlendStrength": 0.18,
            "source": {
                k: (
                    {
                        "path": str(p.relative_to(ROOT)).replace("\\", "/"),
                        "sha256": sha(p),
                    }
                    if p
                    else {"constant": 1}
                )
                for k, p in inputs.items()
            },
            "sha256": sha(path),
        }
        records[name]["sourceNormalConvention"] = convention
        if name in ADDED_SOURCES:
            records[name]["license"] = "CC0-1.0"
            records[name]["attribution"] = (source_root / "CREDITS.md").read_text().strip()
            records[name]["sourceResolution"] = list(Image.open(inputs["albedo"]).size)
        if name in NORMAL_CONVENTIONS:
            records[name][
                "sourceNormalConventionEvidence"
            ] = "Height-gradient correlation: normal X opposes image-column gradient; normal Y follows image-row gradient."
        print(name, flush=True)
    manifest = {
        "version": 1,
        "format": "TPB1: uint32 side, levels; each mip has RGBA8 albedo (sRGB), normal (DX), AO/roughness/height/metallic; bottom-up rows",
        "materials": records,
        "palettes": {
            k: [
                {"material": n, "metres": SCALES[k][i], "role": r}
                for i, (n, r) in enumerate(
                    zip(
                        v,
                        {
                            "temperate": [
                                "meadow",
                                "beach",
                                "woodland",
                                "moss",
                                "bank-mud",
                                "bank-stones",
                                "bare",
                                "cliff",
                            ],
                            "desert": [
                                "sand",
                                "sand",
                                "soil",
                                "gravel",
                                "rock-sand",
                                "rock",
                                "cliff",
                                "wet-bank",
                            ],
                            "icy": [
                                "snow",
                                "snow",
                                "crust",
                                "snow-rock",
                                "ice-ground",
                                "cliff",
                                "woodland",
                                "wet-bank",
                            ],
                        }[k],
                    )
                )
            ]
            for k, v in PALETTES.items()
        },
    }
    # Preserve source records, but the canonical palette above owns runtime roles.
    if previous:
        for name, record in previous["materials"].items():
            if record.get("tool") == "tools/import_meadow_ground.py":
                assert sha(OUT / record["file"]) == record["sha256"], name
                manifest["materials"][name] = record
    for name, record in manifest["materials"].items():
        # Vegetation follows the image, independent of which biome/role uses it.
        record["grassCoverage"] = {
            "rocky-rugged-terrain-pbr": 1.0, "leafy-grass2": 1.0,
            "patchy-meadow": 1.0, "wispy-grass-meadow": 1.0,
            "forest-floor1": 1.0, "mossy-mud1": 1.0,
            "mud-with-vegetation": 0.9, "pine-forest-ground": 0.75,
            "damp-rocky-ground": 0.5,
        }.get(name, 0.0)
        record.setdefault("parallaxDepthMetres", PARALLAX_DEPTHS.get(name, 0))
    (OUT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    notice = (SOURCE / "crusted-snow2/About these PBR files.txt").read_text()
    (OUT / "FreePBR-notice.txt").write_text(
        notice
        + "\nSource: https://freepbr.com/\nChanges: map packing, DirectX normal conversion, explicit mipmaps.\n"
    )
    # Inventory duplicate source maps without modifying unused source packs.
    groups = {}
    for p in SOURCE.rglob("*.png"):
        groups.setdefault(sha(p), []).append(str(p.relative_to(SOURCE)))
    (OUT / "source-duplicates.json").write_text(
        json.dumps([v for v in groups.values() if len(v) > 1], indent=2) + "\n"
    )


if __name__ == "__main__":
    main()
    from import_paving_materials import install as install_paving
    install_paving()
