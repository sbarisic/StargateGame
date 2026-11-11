"""Focused asset checks; no renderer launch or performance loop."""

import gzip
import json
import struct
from pathlib import Path

import numpy as np
from PIL import Image
from cook_assets import mip_chain
from tree_volume_data import downsample_canopy, pad_attributes

root = Path(__file__).resolve().parents[1]
descriptor = json.loads((root / "assets/vegetation/prototype/canopy.json").read_text())
assert descriptor["inspectionOnly"] and descriptor["version"] == 3
assert descriptor["densityEncoding"] == "opacity-at-reference-distance"
assert 0 < descriptor["densityReferenceDistance"] <= descriptor["cellSize"]
assert np.all(np.array(descriptor["maximum"]) > descriptor["minimum"])
size = descriptor["levels"][0]["size"]
assert len(size) == 3 and all(0 < axis <= 256 for axis in size)
total = 0
for level, mip in enumerate(descriptor["levels"]):
    expected = [max(1, axis >> level) for axis in size]
    assert mip["size"] == expected and len(mip["slices"]) == expected[2]
    for slab in mip["slices"]:
        for key in slab:
            assert key.startswith("vegetation/prototype/") and ".." not in key
            data = (root / "assets" / key).read_bytes()
            assert len(data) == expected[0] * expected[1] * 4
            total += len(data)
assert total == descriptor["bytes"] <= 160 * 1024 * 1024
for level, maximum in enumerate([40000, 10000, 3000]):
    mesh = (root / f"assets/models/prototype-trunk-{level}.mesh").read_bytes()
    magic, vertices = struct.unpack_from("<4si", mesh)
    assert magic == b"SGM1" and vertices <= maximum * 3
    assert len(mesh) == 8 + vertices * 32
    assert np.isfinite(np.frombuffer(mesh, dtype="<f4", offset=8)).all()
texture = root / "assets/models/realistic-tree-775785478a929947-base.png"
sidecar = gzip.decompress(
    texture.with_name(texture.name + ".alpha-333.mips.gz").read_bytes()
)
magic, width, height, count = struct.unpack_from("<4siii", sidecar)
pixels = np.array(Image.open(texture).convert("RGBA"))
expected_mips = list(mip_chain(pixels, "color", 0.333))
assert magic == b"SGAM" and (width, height) == (pixels.shape[1], pixels.shape[0])
assert count == len(expected_mips)
assert sidecar[48:] == b"".join(np.flipud(mip).tobytes() for mip in expected_mips)
print(
    "PASS: canopy dimensions/budget, source trunk LODs, finite geometry, exact loose/cooked leaf-mip parity"
)

# One occupied child must conserve average optical depth, not become a full block.
density = np.zeros((2, 2, 2), dtype=np.float32)
density[0, 0, 0] = 2
occupancy = -np.expm1(-density)
colors = np.zeros((2, 2, 2, 3), dtype=np.float32)
colors[0, 0, 0] = [0.2, 0.4, 0.1]
normals = np.zeros_like(colors)
normals[0, 0, 0] = [0, 1, 0]
padded, padded_normals = pad_attributes(colors, normals, density)
assert np.count_nonzero(density) == 1
assert np.allclose(padded, [0.2, 0.4, 0.1])
c, n, d, o = downsample_canopy(colors, normals, density, occupancy)
assert np.allclose(c, [0.2, 0.4, 0.1]) and np.allclose(n, [0, 1, 0])
assert np.allclose(d, 0.25) and np.allclose(o, occupancy.max())
# Attribute padding is not permitted to affect filtered lighting or occupancy.
cp, np_, dp, op = downsample_canopy(padded, padded_normals, density, occupancy)
assert all(np.allclose(a, b) for a, b in zip((c, n, d, o), (cp, np_, dp, op)))
# Two contributors: density-weighted linear colour and normalized normals.
density[1, 1, 1] = 6
colors[1, 1, 1] = [0.8, 0.2, 0.5]
normals[1, 1, 1] = [1, 0, 0]
c, n, d, o = downsample_canopy(colors, normals, density, -np.expm1(-density))
assert np.allclose(c, [0.65, 0.25, 0.4]) and np.allclose(d, 1)
assert np.allclose(np.linalg.norm(n, axis=-1), 1)
assert np.allclose(n.flatten(), np.array([6, 2, 0]) / np.sqrt(40))
print(
    "PASS: sparse density conservation, conservative occupancy, empty padding, linear weighted colour and normalized normals"
)

# Alternative species keep independent payload paths and per-species material roles.
for identity, model, levels in [
    ("broadleaf-2", "MoreRealisticTree_02", 3),
    ("birch", "European_White_Birch_Hero_Winter-summer", 3),
    ("spruce", "BlueSpruce_Desktop_Winter", 1),
]:
    folder = root / "assets/vegetation/prototype" / identity
    descriptor = json.loads((folder / "canopy.json").read_text())
    assert descriptor["model"] == model and descriptor["trunkLevels"] == levels
    size = descriptor["levels"][0]["size"]
    total = 0
    occupied = 0
    for level, mip in enumerate(descriptor["levels"]):
        dimensions = [max(1, axis >> level) for axis in size]
        assert dimensions == mip["size"] and len(mip["slices"]) == dimensions[2]
        for slab in mip["slices"]:
            assert len(slab) == 2
            for channel, key in enumerate(slab):
                assert (
                    key.startswith(f"vegetation/prototype/{identity}/")
                    and ".." not in key
                )
                data = (root / "assets" / key).read_bytes()
                assert len(data) == dimensions[0] * dimensions[1] * 4
                total += len(data)
                if level == 0 and channel == 1:
                    occupied += np.count_nonzero(
                        np.frombuffer(data, dtype=np.uint8)[3::4]
                    )
    assert occupied > 0 and total == descriptor["bytes"] <= 160 * 1024 * 1024
    for level in range(levels):
        trunk = json.loads(
            (
                root / f"assets/models/prototype-{identity}-trunk-{level}.json"
            ).read_text()
        )
        assert trunk["parts"] and all(
            part["surfaceRole"] == "wood" for part in trunk["parts"]
        )
print(
    "PASS: alternate species payloads, density, mip dimensions, budgets and wood material roles"
)
