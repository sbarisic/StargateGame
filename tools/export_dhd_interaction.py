"""Export picking surfaces from retained runtime triangles/UVs, without changing source assets."""

from pathlib import Path
import hashlib, json, struct
from PIL import Image

root = Path(__file__).resolve().parents[1]
models = root / "assets/models"
parts = json.loads((models / "dhd.json").read_text())["parts"]
out = bytearray(b"SGDI1")
triangles = []
provenance = []
for part in parts:
    if part.get("hidden", False):
        continue
    source = models / part["mesh"]
    data = source.read_bytes()
    assert data[:4] == b"SGM1"
    count = struct.unpack_from("<I", data, 4)[0]
    vertices = list(struct.iter_unpack("<8f", data[8:]))
    assert count == len(vertices) and count % 3 == 0
    mask = (
        Image.open(models / part["emissionTexture"]).convert("RGB")
        if part.get("dhdOrb")
        else None
    )
    for i in range(0, count, 3):
        v = vertices[i : i + 3]
        key = part.get("dhdKeyId", -1)
        if mask:
            u = sum(p[6] for p in v) / 3
            w = sum(p[7] for p in v) / 3
            # FishGfx TextureLoadOptions.FlipY defaults to true. Runtime UV v=0
            # therefore samples the bottom PNG row, not the top.
            rgb = mask.getpixel(
                (
                    min(mask.width - 1, max(0, int(u * mask.width))),
                    min(mask.height - 1, max(0, int((1 - w) * mask.height))),
                )
            )
            if max(rgb) > 12 and sum(p[4] for p in v) > 0:
                key = 0
        triangles.append(struct.pack("<i9f", key, *(n for p in v for n in p[:3])))
    provenance.append(
        dict(
            part=part["name"],
            sha256=hashlib.sha256(data).hexdigest(),
            triangles=count // 3,
        )
    )
    if mask:
        provenance[-1].update(
            emissionMask=part["emissionTexture"],
            emissionMaskSha256=hashlib.sha256(
                (models / part["emissionTexture"]).read_bytes()
            ).hexdigest(),
            textureFlipY=True,
        )
out += struct.pack("<I", len(triangles)) + b"".join(triangles)
destination = root / "assets/interaction"
destination.mkdir(exist_ok=True)
(destination / "dhd.sgdi").write_bytes(out)
(destination / "dhd-provenance.json").write_text(
    json.dumps(
        dict(
            version=1,
            method=__doc__,
            parts=provenance,
            sha256=hashlib.sha256(out).hexdigest(),
        ),
        indent=2,
    )
    + "\n"
)
print(f"{len(triangles)} picking triangles, {len(out)} bytes")
