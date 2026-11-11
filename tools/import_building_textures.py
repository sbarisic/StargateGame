"""Copy extracted construction plaster maps; originals remain unchanged."""

import argparse
import hashlib
import json
from pathlib import Path
import shutil

parser = argparse.ArgumentParser()
parser.add_argument(
    "--source",
    type=Path,
    default=Path(
        r"D:\Games\Stargate Network\extraction\terrain_library\source_exports"
    ),
)
args = parser.parse_args()
destination = Path(__file__).resolve().parents[1] / "assets" / "building"
textures = {
    "plaster.png": "T_Construction_Plaster_D.png",
    "plaster-normal.png": "T_Construction_Plaster_N.png",
}
source_dir = Path("Master/temp/Megascans/Textures")
for name in textures.values():
    if not (args.source / source_dir / name).is_file():
        raise FileNotFoundError(args.source / source_dir / name)
destination.mkdir(parents=True, exist_ok=True)
records = []
for output, name in textures.items():
    target = destination / output
    shutil.copyfile(args.source / source_dir / name, target)
    records.append(
        {
            "file": output,
            "source_relative_to_source_exports": str(source_dir / name),
            "sha256": hashlib.sha256(target.read_bytes()).hexdigest(),
            "conversion": "byte-for-byte copy; RGB albedo decoded to linear; normal data stays linear with DirectX green flip in shader",
        }
    )
    print(f"Imported {output}: {target.stat().st_size:,} bytes")
(destination / "provenance.json").write_text(
    json.dumps(
        {
            "version": 1,
            "source": "Local Stargate Network terrain_library extraction",
            "textures": records,
        },
        indent=2,
    )
    + "\n",
    encoding="utf-8",
)
