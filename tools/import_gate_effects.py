"""Copy verified UE Viewer exports without changing their channels or alpha."""

import argparse
import hashlib
import json
import shutil
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "exports", type=Path, help="UE Viewer -export -png output directory"
    )
    args = parser.parse_args()
    target = Path(__file__).resolve().parents[1] / "assets" / "gate-effects"
    manifest = json.loads((target / "provenance.json").read_text())
    copies = []
    for item in manifest["textures"]:
        candidates = list(args.exports.rglob(item["file"]))
        matching = [
            p
            for p in candidates
            if hashlib.sha256(p.read_bytes()).hexdigest() == item["sha256"]
        ]
        if not matching:
            raise SystemExit(f"Missing or changed export: {item['file']}")
        copies.append((matching[0], target / item["file"]))
    for source, destination in copies:
        if source.resolve() != destination.resolve():
            shutil.copyfile(source, destination)
        print(destination)


if __name__ == "__main__":
    main()
