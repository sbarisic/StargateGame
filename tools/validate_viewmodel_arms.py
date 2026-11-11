"""Focused arms replacement checks. --baseline compares the pre-import rifle."""
import argparse
import gzip
import json
from pathlib import Path

import numpy as np
from PIL import Image
from import_viewmodel_arms import fit, OUTPUT

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--baseline", type=Path)
args = parser.parse_args()
data = json.loads(gzip.decompress((OUTPUT / "retro-rifle.sga.gz").read_bytes()))
part = next(p for p in data["parts"] if p["name"] == "FPS_Arms_Mesh")
assert len(part["indices"]) == 14852 * 3
assert part["preserveNormals"] is True
assert {c["name"] for c in data["clips"]} == {
    "idle", "aim", "fire", "fire-aim", "reload", "draw", "holster", "walk", "run", "crouch-idle", "crouch-walk"}
if args.baseline:
    before = json.loads(gzip.decompress(args.baseline.read_bytes()))
    for key in ("bones", "parents", "inverseBind", "clips"):
        assert before[key] == data[key], "Replacement changed " + key
    weapons = lambda d: [p for p in d["parts"] if p["name"] != "FPS_Arms_Mesh"]
    assert weapons(before) == weapons(data), "Replacement changed weapon geometry/materials"
assert fit(json.loads(json.dumps(data))) == data, "Arm fitting is not deterministic"
for field in ("albedo", "normal", "metallicRoughness"):
    with Image.open(OUTPUT / part[field]) as image:
        assert image.size == (2048, 2048)
        if field == "metallicRoughness":
            pixels = np.asarray(image)
            assert not pixels[:, :, 2].any(), "Gloves/sleeves must be nonmetallic"
            assert np.ptp(pixels[:, :, 0]) > 10 and np.ptp(pixels[:, :, 1]) > 10

positions = np.asarray(part["positions"]).reshape(-1, 3)
joints = np.asarray(part["joints"], dtype=int).reshape(-1, 4)
weights = np.asarray(part["weights"]).reshape(-1, 4)
inverse = np.asarray(data["inverseBind"]).reshape(-1, 4, 4)
vertices = np.column_stack((positions, np.ones(len(positions))))
for clip in data["clips"]:
    local = np.asarray(clip["matrices"]).reshape(clip["frames"], -1, 4, 4)
    world = np.empty_like(local)
    for i, parent in enumerate(data["parents"]):
        world[:, i] = local[:, i] if parent < 0 else world[:, parent] @ local[:, i]
    skin = world @ inverse
    last = None
    max_step = 0
    for pose in skin:
        posed = sum(np.einsum("nij,nj->ni", pose[joints[:, slot]], vertices)
                    * weights[:, slot, None] for slot in range(4))[:, :3]
        assert np.isfinite(posed).all() and np.max(np.abs(posed)) < 3, clip["name"]
        if last is not None:
            max_step = max(max_step, np.linalg.norm(posed - last, axis=1).max())
        last = posed
    assert max_step < .5, f'{clip["name"]}: discontinuous arm movement {max_step}'
    print(f'{clip["name"]}: {clip["frames"]} frames, maximum vertex step {max_step:.4f}m')
print("PASS: deterministic fitted arms, material maps, complete clips and unchanged rifle")
