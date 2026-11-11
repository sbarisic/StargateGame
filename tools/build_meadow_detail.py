"""Deterministic periodic normal bake from four tapered blades per procedural tuft."""

from pathlib import Path
import json, hashlib
import numpy as np
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
rng = np.random.default_rng(7319)
size = 512
height = Image.new("F", (size, size))
draw = ImageDraw.Draw(height)
for tuft in range(192):
    x, y = rng.random(2) * size
    angle = rng.random() * np.pi * 2
    length = rng.uniform(0.18, 0.45) * size / 2
    for blade in range(4):
        a = angle + blade * 2.39996
        d = np.array([np.cos(a), np.sin(a)])
        side = np.array([-d[1], d[0]])
        base = np.array([x, y]) + d * 0.055 * size / 2
        points = [base - side * 2, base + side * 2, base + d * length]
        value = float(rng.uniform(0.4, 1))
        for ox in [-size, 0, size]:
            for oy in [-size, 0, size]:
                draw.polygon([tuple(p + [ox, oy]) for p in points], fill=value)
h = np.asarray(height)
dx = (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * 0.7
dy = (np.roll(h, -1, axis=0) - np.roll(h, 1, axis=0)) * 0.7
n = np.stack([-dx, -dy, np.ones_like(h)], axis=-1)
n /= np.linalg.norm(n, axis=-1)[..., None]
Image.fromarray(np.uint8(np.clip((n * 0.5 + 0.5) * 255, 0, 255))).save(
    root / "assets/terrain/meadow-normal.png"
)
print("Baked 512-square periodic meadow normal map.")

out = root / "assets/terrain/meadow-normal.png"
(root / "assets/terrain/meadow-provenance.json").write_text(
    json.dumps(
        {
            "version": 1,
            "source": "Original procedural four-blade tufts; no external imagery",
            "seed": 7319,
            "patchMetres": 2,
            "tufts": 192,
            "size": size,
            "generator": "tools/build_meadow_detail.py",
            "sha256": hashlib.sha256(out.read_bytes()).hexdigest(),
        },
        indent=2,
    )
    + "\n"
)
