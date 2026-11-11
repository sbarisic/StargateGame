"""Prepare loose-development leaf mips with the same filtering as cooked releases."""

import gzip
import argparse
import hashlib
import json
import struct
from pathlib import Path

import numpy as np
from PIL import Image
from cook_assets import mip_chain

ROOT = Path(__file__).resolve().parents[1]


def main():
    models = ROOT / "assets/models"
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--vegetation", type=Path, default=ROOT / "artifacts/cooked/models"
    )
    vegetation = parser.parse_args().vegetation
    seen = set()
    for manifest in sorted(models.glob("*.json")) + sorted(vegetation.glob("*.json")):
        for part in json.loads(manifest.read_text()).get("parts", []):
            cutoff = part.get("alphaCutoff", 0)
            if cutoff <= 0 or "texture" not in part:
                continue
            name = part["texture"]
            key = (name, round(cutoff * 1000))
            if key in seen:
                continue
            seen.add(key)
            source = (
                vegetation / name if (vegetation / name).exists() else models / name
            )
            destination = (
                vegetation if name.startswith("veg-") else models
            ) / f"{name}.alpha-{key[1]}.mips.gz"
            digest = hashlib.sha256(
                source.read_bytes() + struct.pack("<f", cutoff) + b"alpha-v3"
            ).digest()
            if destination.exists():
                with gzip.open(destination, "rb") as existing:
                    if existing.read(48)[16:] == digest:
                        continue
            pixels = np.array(Image.open(source).convert("RGBA"))
            mips = list(mip_chain(pixels, "color", cutoff))
            data = (
                struct.pack(
                    "<4siii", b"SGAM", pixels.shape[1], pixels.shape[0], len(mips)
                )
                + digest
            )
            data += b"".join(np.flipud(mip).tobytes() for mip in mips)
            temporary = destination.with_suffix(".tmp")
            temporary.write_bytes(gzip.compress(data, mtime=0))
            temporary.replace(destination)
            print(f"Prepared {destination.name}", flush=True)
    print(f"{len(seen)} leaf texture variants available for loose loading")


if __name__ == "__main__":
    main()
