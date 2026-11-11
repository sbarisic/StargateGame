"""Import curated furniture independently of the settlement building sources.

Run explicitly: python tools/import_furnishings.py
No Blender or game processes are started. Originals are never changed.
"""

import hashlib, json, math, shutil, struct
from pathlib import Path
from urllib.parse import unquote
import numpy as np
from PIL import Image
from furniture_gltf import Scene, bounds, centre, crop, transform

ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / "assets/furnishings"
MODELS = ROOT / "assets/models"
MANIFEST = DEST / "import-manifest.json"


def box(lo, hi):
    x, y, z = lo
    X, Y, Z = hi
    v = np.array(
        [
            [x, y, z],
            [X, y, z],
            [X, y, Z],
            [x, y, Z],
            [x, Y, z],
            [X, Y, z],
            [X, Y, Z],
            [x, Y, Z],
        ]
    )
    return v[
        np.array(
            [
                [0, 2, 1],
                [0, 3, 2],
                [4, 5, 6],
                [4, 6, 7],
                [0, 1, 5],
                [0, 5, 4],
                [1, 2, 6],
                [1, 6, 5],
                [2, 3, 7],
                [2, 7, 6],
                [3, 0, 4],
                [3, 4, 7],
            ]
        )
    ]


def collision(a, lo, hi):
    kind = a["collision"]
    x, y, z = lo
    X, Y, Z = hi
    if kind == "box":
        return box(lo, hi)
    seat = Y if kind in ("table", "bench") else 0.45
    thickness = 0.075
    leg = 0.065
    shapes = [box((x, seat - thickness, z), (X, seat, Z))]
    for xx in [x, X - leg]:
        for zz in [z, Z - leg]:
            shapes.append(box((xx, 0, zz), (xx + leg, seat - thickness, zz + leg)))
    if kind == "chair":
        shapes.append(box((x, seat, Z - 0.07), (X, Y, Z)))
    return np.concatenate(shapes)


def main():
    manifest = json.loads(MANIFEST.read_text())
    scenes = {}
    textures = {}
    sources = {}
    assets = {}
    exported = []
    prefabs = []
    report = []
    DEST.mkdir(exist_ok=True)
    MODELS.mkdir(exist_ok=True)
    (DEST / "licenses").mkdir(exist_ok=True)

    def track(p):
        sources[str(p.relative_to(ROOT)).replace("\\", "/")] = hashlib.sha256(
            p.read_bytes()
        ).hexdigest()

    track(MANIFEST)
    for pack in sorted({a["pack"] for a in manifest["assets"]}):
        folder = ROOT / "assets_itch/furniture" / pack
        scenes[pack] = Scene(folder / "scene.gltf")
        for p in [folder / "scene.gltf", folder / "scene.bin", folder / "license.txt"]:
            track(p)
        shutil.copyfile(folder / "license.txt", DEST / "licenses" / (pack + ".txt"))

    def material(pack, index):
        key = (pack, index)
        if key in textures:
            return textures[key]
        scene = scenes[pack]
        mat = scene.doc["materials"][index]
        pbr = mat.get("pbrMetallicRoughness", {})
        result = {"repeat": True}
        for name, ref in [
            ("texture", pbr.get("baseColorTexture")),
            ("normal", mat.get("normalTexture")),
            ("metallicRoughness", pbr.get("metallicRoughnessTexture")),
        ]:
            if name == "normal" and ref is None:
                continue
            if ref is not None:
                source = scene.doc["images"][
                    scene.doc["textures"][ref["index"]]["source"]
                ]
                path = scene.path.parent / unquote(source["uri"])
                track(path)
                im = Image.open(path).convert("RGBA")
            elif name == "texture":
                im = Image.new("RGBA", (4, 4), (255, 255, 255, 255))
            else:
                im = Image.new("RGBA", (4, 4), (255, 255, 255, 255))
            im.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
            px = np.array(im)
            if name == "texture":
                f = pbr.get("baseColorFactor", [1, 1, 1, 1])
                px[:, :, :3] = (
                    (px[:, :, :3] * np.power(f[:3], 1 / 2.2))
                    .clip(0, 255)
                    .astype("uint8")
                )
                px[:, :, 3] = (px[:, :, 3] * f[3]).astype("uint8")
            elif name == "normal":
                px[:, :, 1] = 255 - px[:, :, 1]
            else:
                px[:, :, 1] = (px[:, :, 1] * pbr.get("roughnessFactor", 1)).astype(
                    "uint8"
                )
                px[:, :, 2] = (px[:, :, 2] * pbr.get("metallicFactor", 1)).astype(
                    "uint8"
                )
            filename = (
                "furnishing-"
                + hashlib.sha256(px.tobytes()).hexdigest()[:16]
                + "-"
                + name
                + ".png"
            )
            if not (MODELS / filename).exists():
                Image.fromarray(px).save(MODELS / filename)
            result[name] = filename
        if mat.get("alphaMode") == "MASK":
            result["alphaCutoff"] = mat.get("alphaCutoff", 0.5)
        textures[key] = result
        return result

    for a in manifest["assets"]:
        scene = scenes[a["pack"]]
        for i, name in zip(a.get("nodes", []), a.get("nodeNames", [])):
            if scene.doc["nodes"][i].get("name") != name:
                raise ValueError(f"Source node changed: {a['id']} / {i}")
        parts = scene.parts(a.get("nodes"))
        if "crop" in a:
            parts = crop(parts, a["crop"])
        parts = transform(parts, yaw=math.radians(a.get("yaw", 0)))
        lo, hi = bounds(parts)
        scale = a["size"] / (hi - lo)[a["sizeAxis"]]
        parts = centre(transform(parts, scale=scale))
        parts = [(v, material(a["pack"], m)) for v, m in parts]
        lo, hi = bounds(parts)
        if not np.isfinite(np.concatenate([v for v, m in parts])).all():
            raise ValueError("Nonfinite mesh")
        assets[a["id"]] = (
            a,
            parts,
            collision(a, lo, hi) if a["category"] != "clutter" else None,
        )
        report.append(
            dict(
                id=a["id"],
                sourcePack=a["pack"],
                scale=float(scale),
                dimensions=(hi - lo).tolist(),
                triangles=sum(len(v) // 3 for v, m in parts),
            )
        )

    for variant in manifest["variants"]:
        base, parts, coll = assets[variant["base"]]
        parts = list(parts)
        blo, bhi = bounds(parts)
        for item in variant["items"]:
            _, clutter, _ = assets[item["asset"]]
            placed = transform(clutter, offset=item["offset"])
            lo, hi = bounds(placed)
            if (
                item["offset"][1] != base["surface"]
                or np.any(lo[[0, 2]] < blo[[0, 2]] + 0.03)
                or np.any(hi[[0, 2]] > bhi[[0, 2]] - 0.03)
            ):
                raise ValueError(
                    "Clutter must fit its support surface: " + variant["id"]
                )
            parts += placed
        assets[variant["id"]] = (
            {**base, "id": variant["id"], "title": base["title"] + " with tableware"},
            parts,
            coll,
        )

    for key, (a, parts, coll) in assets.items():
        if a["category"] == "clutter":
            continue  # Only baked variants enter the runtime catalog.
        name = "furnishing-" + key
        groups = {}
        for v, meta in parts:
            groups.setdefault(json.dumps(meta, sort_keys=True), []).append(v)
        entries = []
        for i, (signature, arrays) in enumerate(sorted(groups.items())):
            v = np.concatenate(arrays).astype("<f4")
            filename = f"{name}-{i}.mesh"
            (MODELS / filename).write_bytes(
                b"SGM1" + struct.pack("<I", len(v)) + v.tobytes()
            )
            entries.append(dict(mesh=filename, **json.loads(signature)))
        (MODELS / (name + ".json")).write_text(
            json.dumps(dict(version=1, parts=entries), indent=2) + "\n"
        )
        lo, hi = bounds(parts)
        cmin = coll.reshape(-1, 3).min(0)
        cmax = coll.reshape(-1, 3).max(0)
        surfaces = (
            []
            if "surface" not in a
            else [
                dict(
                    height=a["surface"],
                    bounds=[
                        float(lo[0] + 0.03),
                        float(lo[2] + 0.03),
                        float(hi[0] - 0.03),
                        float(hi[2] - 0.03),
                    ],
                )
            ]
        )
        exported.append(
            dict(
                id=name,
                category=a["category"],
                outdoor=a["outdoor"],
                width=float(hi[0] - lo[0]),
                depth=float(hi[2] - lo[2]),
                height=float(hi[1]),
                facing=[0, 0, -1],
                access=a["access"],
                surfaces=surfaces,
            )
        )
        prefabs.append(
            dict(
                id=name,
                title=a["title"],
                group="Generated",
                role="furnishing",
                width=float(hi[0] - lo[0]),
                depth=float(hi[2] - lo[2]),
                height=float(hi[1]),
                ground=False,
                min=lo.tolist(),
                max=hi.tolist(),
                triangles=np.round(coll, 6).tolist(),
                selectable=False,
            )
        )
        if cmin[1] < -0.001 or cmax[1] > hi[1] + 0.001:
            raise ValueError("Collision outside model")

    templates = {
        t["id"]: t
        for t in json.loads((ROOT / "assets/settlements/templates.json").read_text())
    }
    building_prefabs = {
        p["id"]: p
        for folder in ["building", "settlements"]
        for p in json.loads((ROOT / f"assets/{folder}/prefabs.json").read_text())
    }
    for path in [
        ROOT / "assets/settlements/templates.json",
        ROOT / "assets/settlements/prefabs.json",
        ROOT / "assets/building/prefabs.json",
    ]:
        track(path)
    interiors = []
    for source in manifest["interiors"]:
        t = templates[source["template"]]
        floor = source["floor"]
        exclusions = []
        openings = []
        if source["outdoor"]:
            # import_settlements emits 12 triangles per perimeter wall box,
            # followed by the tower mesh. Include its base, with a metre for access.
            w = floor[2] - floor[0] + 4
            d = floor[3] - floor[1] + 4
            walls = (
                int(w / 8)
                + sum(abs(x) > 4 for x in np.arange(-w / 2 + 4, w / 2, 8))
                + 2 * int(d / 8)
            )
            tris = np.array(building_prefabs[t["id"]]["triangles"])
            tower = tris[walls * 12 :].reshape(-1, 3)
            if len(tower):
                lo = tower.min(0)
                hi = tower.max(0)
                exclusions.append(
                    [
                        float(lo[0] - 1),
                        float(lo[2] - 1),
                        float(hi[0] + 1),
                        float(hi[2] + 1),
                    ]
                )
        else:
            # Front windows flank the doorway. Tall furniture stays off this wall.
            openings.append([floor[0], floor[1], floor[2], floor[1] + 0.55])
        door_sweeps = []
        for d in t["doors"]:
            door = building_prefabs[d["model"]]
            lo, hi = door["min"], door["max"]
            corners = np.array(
                [
                    [x, y, z]
                    for x in [lo[0], hi[0]]
                    for y in [lo[1], hi[1]]
                    for z in [lo[2], hi[2]]
                ]
            )
            swept = []
            for angle in np.linspace(0, math.pi / 2, 19):
                angle -= d["rotation"] * math.pi / 6
                c, s = math.cos(angle), math.sin(angle)
                r = np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])
                swept.append(corners @ r.T + d["position"])
            swept = np.concatenate(swept)
            lo = swept.min(0)
            hi = swept.max(0)
            door_sweeps.append(
                [
                    float(lo[0] - 0.15),
                    float(lo[2] - 0.15),
                    float(hi[0] + 0.15),
                    float(hi[2] + 0.15),
                ]
            )
        interiors.append(
            dict(
                template=t["id"],
                floor=floor,
                floorHeight=t["floorHeight"],
                outdoor=source["outdoor"],
                entrances=t["entrances"],
                doorSweeps=door_sweeps,
                openings=openings,
                exclusions=exclusions,
            )
        )
    for filename, data in [
        ("catalog.json", exported),
        ("prefabs.json", prefabs),
        ("interiors.json", interiors),
        ("provenance.json", dict(version=1, sources=sources, assets=report)),
    ]:
        (DEST / filename).write_text(json.dumps(data, indent=2) + "\n")
    print(
        f"Imported {len(prefabs)} furnishing prefabs, {len(interiors)} interiors. Source assets unchanged."
    )


if __name__ == "__main__":
    main()
