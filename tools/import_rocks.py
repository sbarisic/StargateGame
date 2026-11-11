"""Offline import of recovered UE rock glTFs. Requires Python, numpy and Pillow.
python tools/import_rocks.py --source "D:/Games/Stargate Network/extraction/terrain_library"
Original files are read only. glTF metres/Y-up are retained; pivots move to bottom centre.
"""

import argparse, hashlib, json, re, struct
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
        m
        for m in catalog["models"]
        if m["lod"] == 0
        and m["category"] in ["Rocks and stones", "Rocky ground pieces"]
    ]
    materials = {m["name"]: m for m in catalog["materials"]}
    textures = {t["name"]: src / t["file"] for t in catalog["textures"]}
    out = ROOT / "assets/models"
    collision = ROOT / "assets/collision"
    used = set()
    bounds = {}
    exported = []

    def read(path):
        used.add(path)
        return path.read_bytes()

    def props(name):
        m = materials[name]
        text = read(src / m["properties_file"]).decode()
        parent = re.search(r"Parent = \w+'[^']*\.([^'.]+)'", text)
        result = props(parent[1]) if parent and parent[1] in materials else {}
        return result | m["parameters"]

    def mesh(record):
        path = src / record["file"]
        doc = json.loads(read(path))
        buffers = [read(path.parent / b["uri"]) for b in doc["buffers"]]
        assert all(
            not any(k in n for k in ["matrix", "translation", "rotation", "scale"])
            for n in doc["nodes"]
        ), "Unexpected glTF transform"

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

        parts = []
        for m in doc["meshes"]:
            for p in m["primitives"]:
                a = p["attributes"]
                parts.append(
                    (
                        acc(a["POSITION"]),
                        acc(a["NORMAL"]),
                        acc(a["TEXCOORD_0"]),
                        acc(p["indices"]).reshape(-1, 3),
                        doc["materials"][p["material"]]["name"],
                    )
                )
        return parts

    for record in records:
        name = record["name"]
        parts = mesh(record)
        positions = np.concatenate([p[0] for p in parts])
        lo = positions.min(0)
        hi = positions.max(0)
        shift = np.array([(lo[0] + hi[0]) / 2, lo[1], (lo[2] + hi[2]) / 2], np.float32)
        bounds[name] = {"min": (lo - shift).tolist(), "max": (hi - shift).tolist()}
        entries = []
        for i, (p, n, uv, indices, mat) in enumerate(parts):
            values = props(mat)
            hints = materials[mat]["exporter_texture_hints"]

            def ref(key, fallback):
                value = values.get(key, "")
                return (
                    value.split(".")[-1].rstrip("'")
                    if value.startswith("Texture2D")
                    else fallback
                )

            albedo = ref("Albedo", hints.get("Diffuse"))
            normal = ref("Normal", hints.get("Normal"))
            texture = f"rock-{mat}.png"
            normalfile = f"rock-{mat}-normal.png"
            image = Image.open(textures[albedo]).convert("RGB")
            read(textures[albedo])
            tint = re.findall(r"[RGB]=([\d.eE+-]+)", values.get("Color", ""))
            if len(tint) == 3:
                rgb = np.asarray(image).astype(np.float32) / 255
                linear = np.where(
                    rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4
                ) * np.array(list(map(float, tint)))
                rgb = np.where(
                    linear <= 0.0031308,
                    linear * 12.92,
                    1.055 * linear ** (1 / 2.4) - 0.055,
                )
                image = Image.fromarray(np.uint8(np.clip(rgb * 255 + 0.5, 0, 255)))
            image.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
            image.save(out / texture)
            read(textures[normal])
            image = Image.open(textures[normal]).convert("RGB")
            image.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
            image.save(out / normalfile)
            # FishGfx texture upload uses bottom-left UVs; glTF uses top-left.
            uv[:, 1] = 1 - uv[:, 1]
            vertices = np.concatenate([p - shift, n, uv], axis=1)[
                indices.flatten()
            ].astype("<f4")
            filename = f"{name}_{i}.mesh"
            (out / filename).write_bytes(
                b"SGM1" + struct.pack("<I", len(vertices)) + vertices.tobytes()
            )
            entries.append({"mesh": filename, "texture": texture, "normal": normalfile})
        (out / (name + ".json")).write_text(
            json.dumps({"version": 1, "parts": entries}, indent=2) + "\n"
        )
        # Recovered lower LODs are suitable static collision proxies. Others retain exact geometry.
        lods = [
            r
            for r in catalog["models"]
            if r["name"].startswith(name + "_Lod") and r["lod"] in [1, 2]
        ]
        proxy = max(lods, key=lambda r: r["lod"]) if lods else record
        verts = []
        faces = []
        for p, _, _, tri, _ in mesh(proxy):
            faces.extend(tri + len(verts))
            verts.extend(p - shift)
        verts = np.asarray(verts, dtype="<f4")
        faces = np.asarray(faces, dtype="<i4")
        tri = verts[faces]
        faces = faces[
            np.linalg.norm(
                np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0]), axis=1
            )
            > 1e-9
        ]
        (collision / (name + ".sgc")).write_bytes(
            b"SGC1"
            + struct.pack("<II", len(verts), len(faces))
            + verts.tobytes()
            + faces.tobytes()
        )
        exported.append(
            {
                "model": name,
                "source": record["file"],
                "collision_source": proxy["file"],
                "collision_triangles": len(faces),
                "size_metres": (hi - lo).tolist(),
            }
        )
        print(
            name,
            len(vertices) // 3,
            "visual triangles;",
            len(faces),
            "collision triangles",
        )
    (out / "rocks-bounds.json").write_text(json.dumps(bounds, indent=2) + "\n")
    sources = [
        {
            "path": p.relative_to(src).as_posix(),
            "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
        }
        for p in sorted(used)
    ]
    (out / "rocks-provenance.json").write_text(
        json.dumps(
            {
                "version": 1,
                "source": "Stargate Network extraction/terrain_library",
                "units": "metres, Y-up, bottom-centred",
                "textures": "Recovered albedo with inherited linear material tint; DX normal RGB; opaque, capped at 2048px",
                "models": exported,
                "sources": sources,
            },
            indent=2,
        )
        + "\n"
    )


if __name__ == "__main__":
    main()
