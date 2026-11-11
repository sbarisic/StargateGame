"""Project source DHD glyph triangles into a padded GUI atlas; source files are read-only."""

import argparse, hashlib, json
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument(
    "--source",
    type=Path,
    default=Path(
        "D:/Games/Stargate Network/extraction/models/Technology/Ancient/DHD/Milkyway/Buttons"
    ),
)
args = parser.parse_args()
cell = 128
supersample = 4
atlas = Image.new("RGBA", (cell * 8, cell * 5), (255, 255, 255, 0))
records = []
for key in range(1, 40):
    path = args.source / f"SM_Key_{key:02}.gltf"
    doc = json.loads(path.read_text())
    buffers = [(path.parent / b["uri"]).read_bytes() for b in doc["buffers"]]

    def read(index):
        a = doc["accessors"][index]
        v = doc["bufferViews"][a["bufferView"]]
        dtype = {5126: "<f4", 5125: "<u4", 5123: "<u2"}[a["componentType"]]
        width = {"SCALAR": 1, "VEC3": 3}[a["type"]]
        return np.ndarray(
            (a["count"], width),
            dtype=dtype,
            buffer=buffers[v["buffer"]],
            offset=v.get("byteOffset", 0) + a.get("byteOffset", 0),
            strides=(
                v.get("byteStride", np.dtype(dtype).itemsize * width),
                np.dtype(dtype).itemsize,
            ),
        ).copy()

    primitive = next(
        p
        for p in doc["meshes"][0]["primitives"]
        if doc["materials"][p["material"]]["name"] == "MI_Glyph_NoGlow"
    )
    positions = read(primitive["attributes"]["POSITION"])
    indices = read(primitive["indices"]).reshape(-1, 3)
    centre = positions.mean(0)
    _, _, axes = np.linalg.svd(positions - centre, full_matrices=False)
    normal = axes[-1]
    normal *= 1 if normal[1] > 0 else -1
    # Inward on the sloping key face is up in the address strip. This removes
    # the key's radial rotation without mirroring the source artwork.
    up = np.array([0.0, 1.11, -0.16]) - centre
    up -= normal * np.dot(up, normal)
    up /= np.linalg.norm(up)
    right = np.cross(up, normal)
    right /= np.linalg.norm(right)
    points = np.stack(
        ((positions - centre) @ right, -(positions - centre) @ up), axis=1
    )
    lo = points.min(0)
    hi = points.max(0)
    points = (points - (lo + hi) / 2) / max(hi - lo) * 104 + cell / 2
    mask = Image.new("L", (cell * supersample, cell * supersample))
    draw = ImageDraw.Draw(mask)
    for face in indices:
        draw.polygon([tuple(p * supersample) for p in points[face]], fill=255)
    mask = mask.resize((cell, cell), Image.Resampling.LANCZOS)
    tile = Image.new("RGBA", (cell, cell), (255, 255, 255, 0))
    tile.putalpha(mask)
    atlas.paste(tile, (((key - 1) % 8) * cell, ((key - 1) // 8) * cell))
    records.append(
        dict(
            id=key,
            part=f"SM_Key_{key:02}",
            sourceSha256=hashlib.sha256(
                path.read_bytes() + b"".join(buffers)
            ).hexdigest(),
            triangles=len(indices),
        )
    )
out = root / "assets/gui"
out.mkdir(exist_ok=True)
atlas.save(out / "stargate-glyphs.png")
(out / "glyph-provenance.json").write_text(
    json.dumps(
        dict(
            version=1,
            method=__doc__,
            cell=cell,
            columns=8,
            rows=5,
            orientation="key-local inward-up, unmirrored",
            glyphs=records,
            sha256=hashlib.sha256(
                (out / "stargate-glyphs.png").read_bytes()
            ).hexdigest(),
        ),
        indent=2,
    )
    + "\n"
)
print(
    "Exported 39 glyphs, preserving DHD key IDs (including hidden key 13 and local origin 1)."
)
