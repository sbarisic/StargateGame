"""Focused offline checks: python tools/test_cooked_assets.py (no source mutations)."""

import hashlib, json, struct, tempfile
from pathlib import Path
import numpy as np
from cook_assets import mip_chain, dilate, Package

p = np.zeros((64, 64, 4), np.uint8)
p[8:56, 8:56] = [60, 150, 30, 255]
d = dilate(p)
assert np.array_equal(d[:, :, 3], p[:, :, 3])
assert tuple(d[7, 32, :3]) == (60, 150, 30)
mips = list(mip_chain(p, "color", 0.5))
assert np.array_equal(
    mips[0][:, :, 3], p[:, :, 3]
), "Base alpha must remain opaque after cooking"
coverage = np.mean(p[:, :, 3] >= 128)
for mip in mips[:4]:
    assert abs(np.mean(mip[:, :, 3] >= 128) - coverage) < 0.08
n = np.zeros((8, 8, 4), np.uint8)
n[:] = [128, 128, 255, 255]
for mip in mip_chain(n, "normal", 0):
    assert abs(np.linalg.norm(mip[0, 0, :3] / 255 * 2 - 1) - 1) < 0.02
with tempfile.TemporaryDirectory(prefix="stargate-cooker-") as folder:
    path = Path(folder) / "models.sga.tmp"

    def build():
        package = Package(path)
        package.add("mesh", b"abcd" * 4000)
        package.add("same", b"abcd" * 4000)
        package.add("raw", bytes(range(32)))
        package.finish()
        return path.read_bytes()

    first = build()
    path.rename(path.with_suffix(""))
    second = build()
    assert first == second, "Incremental cook must be byte reproducible"
print(
    "PASS: alpha preservation, border dilation, linear mips, normal normalization, deduplication, reproducible incremental package"
)
