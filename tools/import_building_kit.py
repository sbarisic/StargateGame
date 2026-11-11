"""Reproduce the supplied building pieces, textures, collision and thumbnails.
Run with --export to execute the Blender stage first. Source assets are read only.
"""

import argparse, hashlib, json, shutil, subprocess, struct
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
RAW = ROOT / "artifacts/building-kit"
OUT = ROOT / "assets/models"
ap = argparse.ArgumentParser()
ap.add_argument("--export", action="store_true")
ap.add_argument("--previews", default="D:/Games/Stargate Network/extraction")
ap.add_argument(
    "--blender", default="C:/Program Files/Blender Foundation/Blender 5.2/blender.exe"
)
args = ap.parse_args()
if args.export:
    subprocess.run(
        [
            args.blender,
            "-b",
            "--python",
            str(ROOT / "tools/export_building_kit.py"),
            "--",
            args.previews,
        ],
        check=True,
    )
raw = json.loads((RAW / "export.json").read_text())
catalog = []
material_cache = {}
titles = [
    "Stone wall",
    "Stone foundation",
    "Stone doorway",
    "Stone window wall",
    "Plaster wall",
    "Brick floor",
    "Wooden floor",
    "Wooden door",
    "Door frame",
    "Interior stairs",
    "Entrance steps",
    "Roof tiles",
    "Tiled roof 6 x 6 m",
    "Roof gable",
    "Corner trim",
    "Window insert",
    "Entrance platform",
    "Fort wall",
    "Firing window wall",
    "Fort corner",
    "Parapet",
    "Raised landing",
    "Fort stairs",
    "Swing gate (fixed)",
    "Sliding gate (fixed)",
    "Road barrier",
    "Bollard",
    "Gate control (decor)",
]
roles = [
    "wall",
    "foundation",
    "doorway",
    "window",
    "wall",
    "floor",
    "floor",
    "door",
    "frame",
    "stairs",
    "stairs",
    "roof",
    "roof",
    "trim",
    "trim",
    "insert",
    "landing",
    "wall",
    "window",
    "corner",
    "parapet",
    "landing",
    "stairs",
    "barrier",
    "barrier",
    "barrier",
    "decoration",
    "decoration",
]


def texture(ref):
    return Image.open(RAW / raw["images"][ref]["raw"]).convert("RGB")


def materials(m):
    key = hashlib.sha256(json.dumps(m, sort_keys=True).encode()).hexdigest()[:12]
    if key in material_cache:
        return material_cache[key]
    result = {}
    base = (
        texture(m["base"])
        if m.get("base")
        else Image.new(
            "RGB",
            (4, 4),
            tuple(round(pow(max(0, c), 1 / 2.2) * 255) for c in m["color"][:3]),
        )
    )
    base.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
    name = f"build-{key}-color.png"
    base.save(OUT / name)
    result["texture"] = name
    if m.get("normal"):
        img = texture(m["normal"])
        img.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
        a = np.array(img)
        a[:, :, 1] = 255 - a[:, :, 1]
        name = f"build-{key}-normal.png"
        Image.fromarray(a).save(OUT / name)
        result["normal"] = name
    if m.get("rough") or m.get("metal"):
        ref = m.get("rough") or m["metal"]
        img = texture(ref)
        img.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
        a = np.array(img)
        # glTF imports pack roughness in G and metal in B. Unlinked factors override channels.
        if not m.get("rough"):
            a[:, :, 1] = round(m["roughness"] * 255)
        if not m.get("metal"):
            a[:, :, 2] = round(m["metallic"] * 255)
        name = f"build-{key}-mr.png"
        Image.fromarray(a).save(OUT / name)
        result["metallicRoughness"] = name
    material_cache[key] = result
    return result


for p, title, role in zip(raw["pieces"], titles, roles, strict=True):
    key = p["id"]
    group = "Fortification" if key.startswith("fort-") else "Village"
    groups = {}
    offset = np.zeros(3)
    rotation = np.eye(3)
    if key in ("fort-parapet", "fort-landing"):
        offset[1] = -p["min"][1] if role == "parapet" else -p["max"][1]
    if role == "stairs":
        offset[0] = -(p["min"][0] + p["max"][0]) / 2
        offset[2] = -(p["min"][2] + p["max"][2]) / 2
        # Export all stairs rising toward +Z, so their landing sockets agree.
        pts = np.array(p["triangles"]).reshape(-1, 3)
        high = pts[pts[:, 1] > max(pts[:, 1]) - 0.4].mean(0) + offset
        if abs(high[0]) > abs(high[2]):
            rotation = (
                np.array([[0, 0, -1], [0, 1, 0], [1, 0, 0]])
                if high[0] > 0
                else np.array([[0, 0, 1], [0, 1, 0], [-1, 0, 0]])
            )
        elif high[2] < 0:
            rotation = np.diag([-1, 1, -1])
    for part in p["parts"]:
        source = RAW / part["mesh"]
        b = source.read_bytes()
        vertices = np.frombuffer(b[8:], dtype="<f4").reshape(-1, 8).copy()
        vertices[:, :3] = (vertices[:, :3] + offset) @ rotation.T
        vertices[:, 3:6] = vertices[:, 3:6] @ rotation.T
        material = materials(part["material"])
        signature = json.dumps(material, sort_keys=True)
        groups.setdefault(signature, []).append(vertices)
    parts = []
    for i, (signature, arrays) in enumerate(groups.items()):
        vertices = np.concatenate(arrays)
        filename = f"{key}-runtime-{i}.mesh"
        (OUT / filename).write_bytes(
            b"SGM1"
            + struct.pack("<I", len(vertices))
            + vertices.astype("<f4").tobytes()
        )
        parts.append({"mesh": filename, "repeat": True, **json.loads(signature)})
    (OUT / (key + ".json")).write_text(
        json.dumps({"version": 1, "parts": parts}, indent=2) + "\n"
    )
    tris = (np.array(p["triangles"]) + offset) @ rotation.T
    lo = tris.reshape(-1, 3).min(0)
    hi = tris.reshape(-1, 3).max(0)
    w, d = hi[0] - lo[0], hi[2] - lo[2]
    h = 3 if group == "Village" else 3.0169868
    if role in ("wall", "window", "doorway"):
        w = 2 if group == "Village" else 1.5
    if role in ("floor", "foundation"):
        w = d = 2
        h = 0
    if key == "kit-roof-roundtiles-6x6":
        w = d = 6
    if key == "kit-stairs-exterior-platform":
        w = d = 2
        h = 1
    if key == "kit-stairs-exterior-straight":
        h = 1
    if key == "fort-landing":
        w = 1.5
        h = 0
    if role == "parapet":
        w = 1.5
        h = 1
    ground = role == "foundation" or key in (
        "kit-stairs-exterior-platform",
        "kit-stairs-exterior-straight",
        "fort-solid-wall",
        "fort-firing-window-wall",
        "fort-corner-wall",
        "fort-stairs",
        "fort-swing-gate",
        "fort-sliding-gate",
        "fort-road-barrier",
        "fort-bollard",
        "fort-gate-control",
    )
    catalog.append(
        {
            "id": key,
            "title": title,
            "group": group,
            "role": role,
            "width": float(w),
            "depth": float(d),
            "height": h,
            "ground": ground,
            "min": lo.tolist(),
            "max": hi.tolist(),
            "triangles": np.round(tris, 6).tolist(),
        }
    )
building = ROOT / "assets/building"
building.mkdir(exist_ok=True)
(building / "prefabs.json").write_text(
    json.dumps(catalog, separators=(",", ":")) + "\n"
)
for source, target in [
    (
        ROOT / "assets_itch/Medieval Village MegaKit[Standard]/License_Standard.txt",
        "megakit-license.txt",
    ),
    (ROOT / "assets_itch/kit_fortification/license.txt", "fortification-license.txt"),
]:
    shutil.copyfile(source, building / target)
inputs = [
    ROOT / "assets_itch/Medieval Village MegaKit[Standard]/glTF",
    ROOT / "assets_itch/Medieval Village MegaKit[Standard]/License_Standard.txt",
    Path(args.previews) / "fortification_preview/Kit_Fortification_components.blend",
    ROOT / "assets_itch/kit_fortification/license.txt",
]
files = [
    q
    for p in inputs
    for q in (sorted(p.rglob("*")) if p.is_dir() else [p])
    if q.is_file()
]
(building / "provenance.json").write_text(
    json.dumps(
        {
            "version": 1,
            "conversion": "Native metres; Y up; original UVs; DirectX normal green; textures capped at 2048; material batching; authored triangle collision; stair rise +Z; separate generated 2m foundation; Cycles 12-sample thumbnails",
            "tools": ["tools/export_building_kit.py", "tools/import_building_kit.py"],
            "pieces": [p["id"] for p in catalog],
            "sources": [
                {"path": str(p), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
                for p in files
            ],
        },
        indent=2,
    )
    + "\n"
)
print(
    f'Imported {len(catalog)} pieces, {len(material_cache)} materials, {sum(len(p["triangles"]) for p in catalog)} collision triangles.'
)
import sys

subprocess.run(
    [sys.executable, str(ROOT / "tools/assemble_building_prefabs.py")], check=True
)
subprocess.run(
    [args.blender, "-b", "--python", str(ROOT / "tools/assemble_building_prefabs.py")],
    check=True,
)
