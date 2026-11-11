"""Normalize seven scanned blades onto the procedural curved geometry; original scans untouched."""

import argparse, hashlib, json
from pathlib import Path
import numpy as np
from PIL import Image
from import_terrain_pbr import linear, srgb, quantize

ROOT = Path(__file__).resolve().parents[1]


def run(source):
    prefix = source / "Foliage008_2K-PNG"
    paths = {
        k: Path(str(prefix) + "_" + v + ".png")
        for k, v in [
            ("color", "Color"),
            ("normal", "NormalGL"),
            ("roughness", "Roughness"),
            ("opacity", "Opacity"),
        ]
    }
    images = {
        k: np.array(Image.open(p).convert("RGB" if k in ("color", "normal") else "L"))
        for k, p in paths.items()
    }
    mask = images["opacity"] > 128
    h, w = mask.shape
    bands = [
        (30, 310),
        (310, 650),
        (650, 1000),
        (1000, 1260),
        (1260, 1510),
        (1510, 1790),
        (1790, 2048),
    ]
    color = np.zeros((512, 512, 4), np.uint8)
    normal = np.zeros_like(color)
    for blade, (top, bottom) in enumerate(bands):
        valid = np.where(mask[top:bottom].any(axis=0))[0]
        left, right = valid[0], valid[-1]
        centers = []
        radii = []
        for x in range(left, right + 1):
            ys = np.where(mask[top:bottom, x])[0] + top
            centers.append((ys[0] + ys[-1]) * 0.5 if len(ys) else (top + bottom) * 0.5)
            radii.append(max(1, (ys[-1] - ys[0]) * 0.5) if len(ys) else 1)
        # Root is at the left of each scanned blade; geometry supplies curvature and taper.
        x = np.linspace(left + 6, right - 4, 512)[::-1]
        centre = np.interp(x, np.arange(left, right + 1), centers)
        radius = np.interp(x, np.arange(left, right + 1), radii)
        u = np.linspace(-0.95, 0.95, 64)
        y = centre[:, None] + radius[:, None] * u[None, :]
        xx = np.broadcast_to(np.rint(x).astype(int)[:, None], y.shape)
        yy = np.clip(np.rint(y).astype(int), 0, h - 1)
        tile = slice(blade * 64, (blade + 1) * 64)
        c = images["color"][yy, xx]
        # Dark-green scan variant, graded in linear space. Retain veins and
        # photographic variation; source files and normal/roughness stay intact.
        color[:, tile, :3] = quantize(
            srgb(linear(c.astype(np.float32) / 255) * np.array([0.42, 0.62, 0.48]))
        )
        color[:, tile, 3] = 255
        n = images["normal"][yy, xx] / 255 * 2 - 1
        # Rotate source length-X to blade-V, and width-Y to blade-U.
        turned = np.stack([-n[:, :, 1], n[:, :, 0], n[:, :, 2]], axis=2)
        turned /= np.maximum(np.linalg.norm(turned, axis=2, keepdims=True), 1e-6)
        normal[:, tile, :3] = np.clip(np.rint((turned * 0.5 + 0.5) * 255), 0, 255)
        normal[:, tile, 3] = images["roughness"][yy, xx]
    color[:, 448:] = color[:, :64]
    normal[:, 448:] = normal[:, :64]
    output = ROOT / "assets/vegetation"
    output.mkdir(exist_ok=True)
    Image.fromarray(color).save(output / "grass-blades.png")
    Image.fromarray(normal).save(output / "grass-blades-normal.png")
    meta = dict(
        source="https://ambientcg.com/a/Foliage008",
        author="ambientCG / Lennart Demes",
        license="CC0-1.0",
        licenseUrl="https://docs.ambientcg.com/license/",
        technique="Photometric Stereo",
        tool="tools/import_grass_blades.py",
        version=1,
        changes="Seven centerline-normalized blade scans; rotated normal channels; original colour retained; geometry provides outline and curvature",
        files={
            p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in paths.values()
        },
        outputs={
            n: hashlib.sha256((output / n).read_bytes()).hexdigest()
            for n in ("grass-blades.png", "grass-blades-normal.png")
        },
    )
    meta.update(
        version=2,
        variant="dark-green",
        linearColorGain=[0.42, 0.62, 0.48],
        changes="Seven centerline-normalized photographic blade scans; darker green linear-space grade; rotated normal channels; geometry provides outline and curvature",
    )
    (output / "grass-blades.provenance.json").write_text(json.dumps(meta, indent=2))


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("source", type=Path)
    run(p.parse_args().source)
