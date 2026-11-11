"""Prepare the species used by ordinary worlds; preserve placements and collision."""

import argparse
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]

# Existing source-derived trunks are reused. Other species retain prepared wood.
SPECIES = {
    "MoreRealisticTree_01": ("broadleaf", "", 1.0),
    "MoreRealisticTree_02": ("broadleaf-2", "broadleaf-2", 1.0),
    "European_White_Birch_Hero_Winter-summer": ("birch", "birch", .55),
    "BlueSpruce_Desktop_Winter": ("spruce", "spruce", .35),
    "Leave-Tree-01a": ("leave-tree", "leave-tree", .55),
    "BlueSpruce_Desktop_Winter2": ("spruce-2", "spruce-2", .35),
    "BlueSpruce_Desktop_Winter3": ("spruce-3", "spruce-3", .35),
    "European_White_Birch_Hero_Winter": ("winter-birch", "winter-birch", .55),
    "RealisticTree_01": ("realistic-1", "realistic-1", 1.0),
    "RealisticTree_02": ("realistic-2", "realistic-2", 1.0),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepared", type=Path, default=ROOT / "artifacts/cooked/models")
    parser.add_argument("--rebuild", action="store_true")
    args = parser.parse_args()
    manifest = {}
    for model, (identity, folder, density) in SPECIES.items():
        relative = Path("vegetation/prototype") / folder / "canopy.json"
        descriptor = ROOT / "assets" / relative
        if not descriptor.exists() or (args.rebuild and identity not in ("broadleaf", "broadleaf-2", "birch", "spruce")):
            print(f"Preparing world canopy: {model}", flush=True)
            subprocess.run([sys.executable, str(ROOT / "tools/cook_tree_prototype.py"),
                            "--model", model, "--id", folder,
                            "--prepared", str(args.prepared)], check=True)
        data = json.loads(descriptor.read_text())
        prefix = "prototype-" + (folder + "-" if folder else "") + "trunk-"
        levels = sum((ROOT / "assets/models" / f"{prefix}{i}.json").exists() for i in range(3))
        if data["version"] != 3 or not levels:
            raise ValueError(f"Incomplete canopy: {model}")
        manifest[model] = dict(Identity=identity, LeafModel="veg-" + model,
                              TrunkPrefix=prefix, TrunkLevels=levels, Scale=1,
                              Height=data["maximum"][1], Canopy=relative.as_posix(),
                              DensityScale=density)
    target = ROOT / "assets/vegetation/world-canopies.json"
    temporary = target.with_suffix(".tmp")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n")
    temporary.replace(target)
    print(f"World canopy registry: {len(manifest)} variants", flush=True)


if __name__ == "__main__":
    main()
