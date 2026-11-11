"""Import recovered tree glTFs/materials. Python + numpy + Pillow; sources are read only.
python tools/import_trees.py --source "D:/Games/Stargate Network/extraction/terrain_library"
"""

import argparse, hashlib, json, struct
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", type=Path, required=True)
    src = ap.parse_args().source
    catalog = json.loads((src / "catalog.json").read_text())
    records = [
        m for m in catalog["models"] if m["category"] == "Trees" and m["lod"] == 0
    ]
    materials = {m["name"]: m for m in catalog["materials"]}
    textures = {t["name"]: src / t["file"] for t in catalog["textures"]}
    out = ROOT / "assets/models"
    collision = ROOT / "assets/collision"
    used = set()
    bounds = {}
    exports = []
    saved = set()

    def read(path):
        used.add(path)
        return path.read_bytes()

    def parts(record):
        path = src / record["file"]
        doc = json.loads(read(path))
        buffers = [read(path.parent / b["uri"]) for b in doc["buffers"]]
        assert all(
            not any(k in n for k in ["matrix", "translation", "rotation", "scale"])
            for n in doc["nodes"]
        )

        def acc(i):
            a = doc["accessors"][i]
            v = doc["bufferViews"][a["bufferView"]]
            dtype = np.dtype(
                {5123: "<u2", 5125: "<u4", 5126: "<f4"}[a["componentType"]]
            )
            width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a["type"]]
            return np.ndarray(
                (a["count"], width),
                dtype,
                buffer=buffers[v["buffer"]],
                offset=v.get("byteOffset", 0) + a.get("byteOffset", 0),
                strides=(v.get("byteStride", dtype.itemsize * width), dtype.itemsize),
            ).copy()

        result = []
        for mesh in doc["meshes"]:
            for p in mesh["primitives"]:
                a = p["attributes"]
                result.append(
                    (
                        acc(a["POSITION"]),
                        acc(a["NORMAL"]),
                        acc(a["TEXCOORD_0"]),
                        acc(p["indices"]).reshape(-1, 3),
                        doc["materials"][p["material"]]["name"],
                    )
                )
        return result

    def masked(name):
        return "Leaves" in name or "Fronds" in name or name == "Branch_Inst2"

    def export(record, shift):
        name = record["name"]
        entries = []
        for i, (p, n, uv, tri, mat) in enumerate(parts(record)):
            info = materials[mat]
            hints = info["exporter_texture_hints"]
            read(src / info["file"])
            read(src / info["properties_file"])
            diffuse = hints.get("Diffuse", hints.get("Other[0]"))
            normal = hints.get("Normal")
            # Match recovered material overrides, rather than UE exporter's fallback hints.
            if mat == "Branch_Inst2":
                diffuse = "Branch_D"
                normal = "Branch_N"
            if mat == "Trunk_Inst2":
                diffuse = "Tree_Bark_D"
            texture = "tree-" + diffuse + ".png"
            normalfile = "tree-" + normal + ".png" if normal else None
            for source, target, is_normal in [
                (diffuse, texture, False),
                (normal, normalfile, True),
            ]:
                if not source or target in saved:
                    continue
                read(textures[source])
                image = Image.open(textures[source]).convert(
                    "RGB" if is_normal else "RGBA"
                )
                # Preserve foliage atlas alpha. Opaque bark sometimes packs unrelated masks in alpha.
                if not is_normal and not masked(mat):
                    image.putalpha(255)
                image.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
                image.save(out / target)
                saved.add(target)
            uv[:, 1] = 1 - uv[:, 1]
            verts = np.concatenate([p - shift, n, uv], axis=1)[tri.flatten()].astype(
                "<f4"
            )
            assert np.isfinite(verts).all()
            filename = f"{name}_{i}.mesh"
            (out / filename).write_bytes(
                b"SGM1" + struct.pack("<I", len(verts)) + verts.tobytes()
            )
            entry = {
                "mesh": filename,
                "texture": texture,
                "repeat": True,
                "alphaCutoff": (
                    (0.5 if mat == "Branch_Inst2" else 0.333) if masked(mat) else 0
                ),
            }
            if normalfile:
                entry["normal"] = normalfile
            entries.append(entry)
        (out / (name + ".json")).write_text(
            json.dumps({"version": 1, "parts": entries}, indent=2) + "\n"
        )

    for record in records:
        name = record["name"]
        data = parts(record)
        positions = np.concatenate([p[0] for p in data])
        lo = positions.min(0)
        hi = positions.max(0)
        shift = np.array([(lo[0] + hi[0]) / 2, lo[1], (lo[2] + hi[2]) / 2], np.float32)
        lods = [
            m
            for m in catalog["models"]
            if m["name"].startswith(name + "_Lod") and m["lod"] in [1, 2]
        ]
        all_positions = np.concatenate(
            [positions] + [p[0] for lod in lods for p in parts(lod)]
        )
        bounds[name] = {
            "min": (all_positions.min(0) - shift).tolist(),
            "max": (all_positions.max(0) - shift).tolist(),
            "lods": [r["name"] for r in sorted(lods, key=lambda r: r["lod"])],
        }
        export(record, shift)
        for lod in lods:
            export(lod, shift)
        proxy = max(lods, key=lambda r: r["lod"]) if lods else record
        # Wood geometry is static collision; transparent fronds/leaves never obstruct players.
        vertices = []
        faces = []
        for p, _, _, tri, mat in parts(proxy):
            if masked(mat):
                continue
            faces.extend(tri + len(vertices))
            vertices.extend(p - shift)
        vertices = np.array(vertices, dtype="<f4")
        faces = np.array(faces, dtype="<i4")
        t = vertices[faces]
        faces = faces[
            np.linalg.norm(np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0]), axis=1)
            > 1e-9
        ]
        assert len(faces) > 0
        (collision / (name + ".sgc")).write_bytes(
            b"SGC1"
            + struct.pack("<II", len(vertices), len(faces))
            + vertices.tobytes()
            + faces.tobytes()
        )
        exports.append(
            {
                "model": name,
                "source": record["file"],
                "collision_source": proxy["file"],
                "wood_triangles": len(faces),
                "height_m": float(hi[1] - lo[1]),
                "lods": bounds[name]["lods"],
            }
        )
        print(
            name,
            round(float(hi[1] - lo[1]), 2),
            "m;",
            len(faces),
            "wood triangles",
            flush=True,
        )
    (out / "trees-bounds.json").write_text(json.dumps(bounds, indent=2) + "\n")
    (out / "trees-provenance.json").write_text(
        json.dumps(
            {
                "version": 1,
                "source": "Stargate Network extraction/terrain_library",
                "units": "metres, Y-up, bottom-centred",
                "models": exports,
                "sources": [
                    {
                        "path": p.relative_to(src).as_posix(),
                        "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
                    }
                    for p in sorted(used)
                ],
            },
            indent=2,
        )
        + "\n"
    )


if __name__ == "__main__":
    main()
