"""Copy the selected extracted terrain textures without modifying source assets."""

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
destination = Path(__file__).resolve().parents[1] / "assets" / "terrain"
textures = {
    "grass-normal.png": "LandscapeAutoMaterial/Textures/Tiles/Grass01/Grass0072_N.png",
    "cliff-normal.png": "LandscapeAutoMaterial/Textures/Tiles/Cliff01/Cliff01_Normal___id_normal_.png",
    "sand-normal.png": "LandscapeAutoMaterial/Textures/Tiles/Sand01/T_Sand_N2.png",
    "snow-normal.png": "PhotoR_Landscape_4/Textures/snow_c_n.png",
    "grass.png": "LandscapeAutoMaterial/Textures/Tiles/Grass01/Grass0072_D.png",
    "cliff.png": "LandscapeAutoMaterial/Textures/Tiles/Cliff01/Cliff01_D_H.png",
    "sand.png": "LandscapeAutoMaterial/Textures/Tiles/Sand01/T_Sand_D.png",
    "snow.png": "PhotoR_Landscape_4/Textures/snow_c_d.png",
}
for source in textures.values():
    if not (args.source / source).is_file():
        raise FileNotFoundError(args.source / source)
destination.mkdir(parents=True, exist_ok=True)
records = []
for name, relative in textures.items():
    source = args.source / relative
    output = destination / name
    shutil.copyfile(source, output)
    records.append(
        {
            "file": name,
            "source_relative_to_source_exports": relative,
            "sha256": hashlib.sha256(output.read_bytes()).hexdigest(),
            "conversion": (
                "byte-for-byte copy; linear DirectX normal XY, green flipped and Z reconstructed in shader"
                if "normal" in name
                else "byte-for-byte copy; RGB used for albedo; source alpha is not opacity"
            ),
        }
    )
    print(f"Imported {name}: {output.stat().st_size:,} bytes")
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
