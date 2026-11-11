"""Import approved optimized tree GLBs into runtime SGM1 meshes and wood collision.
python tools/import_realistic_trees.py --source PATH_TO_tree_optimization
Sources are read-only. Requires numpy and Pillow; no Blender at import time.
"""

import argparse, hashlib, io, json, shutil, struct
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
NAMES = [
    "RealisticTree_01",
    "RealisticTree_02",
    "MoreRealisticTree_01",
    "MoreRealisticTree_02",
]


def read_glb(path):
    b = path.read_bytes()
    assert b[:4] == b"glTF" and struct.unpack_from("<I", b, 4)[0] == 2
    size = struct.unpack_from("<I", b, 12)[0]
    doc = json.loads(b[20 : 20 + size])
    data = b[28 + size :]

    def acc(i):
        a = doc["accessors"][i]
        v = doc["bufferViews"][a["bufferView"]]
        dtype = np.dtype({5123: "<u2", 5125: "<u4", 5126: "<f4"}[a["componentType"]])
        width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a["type"]]
        return np.ndarray(
            (a["count"], width),
            dtype,
            buffer=data,
            offset=v.get("byteOffset", 0) + a.get("byteOffset", 0),
            strides=(v.get("byteStride", dtype.itemsize * width), dtype.itemsize),
        ).copy()

    def image(ref):
        entry = doc["images"][doc["textures"][ref["index"]]["source"]]
        v = doc["bufferViews"][entry["bufferView"]]
        return data[v.get("byteOffset", 0) : v.get("byteOffset", 0) + v["byteLength"]]

    parts = []
    for node in doc["nodes"]:
        if "mesh" not in node:
            continue
        assert not any(
            k in node for k in ["matrix", "rotation", "scale", "children"]
        ), "Unexpected transformed GLB node"
        for prim in doc["meshes"][node["mesh"]]["primitives"]:
            a = prim["attributes"]
            mat = doc["materials"][prim["material"]]
            uv = acc(a["TEXCOORD_0"])
            pbr = mat["pbrMetallicRoughness"]
            for ref in [
                pbr.get("baseColorTexture"),
                pbr.get("metallicRoughnessTexture"),
                mat.get("normalTexture"),
            ]:
                if ref:
                    assert np.allclose(
                        uv, acc(a["TEXCOORD_" + str(ref.get("texCoord", 0))]), atol=1e-5
                    ), "Runtime needs a single shared material UV layout"
            parts.append(
                {
                    "positions": acc(a["POSITION"])
                    + np.asarray(node.get("translation", [0, 0, 0])),
                    "normals": acc(a["NORMAL"]),
                    "uv": uv,
                    "triangles": acc(prim["indices"]).reshape(-1, 3),
                    "material": mat,
                    "images": {
                        k: image(v)
                        for k, v in [
                            ("base", pbr.get("baseColorTexture")),
                            ("mr", pbr.get("metallicRoughnessTexture")),
                            ("normal", mat.get("normalTexture")),
                        ]
                        if v
                    },
                    "wood": "wood" in node["name"].lower(),
                }
            )
    return parts


def planting_origin(part):
    p = part["positions"]
    tri = part["triangles"]
    points, inverse = np.unique(np.round(p, 5), axis=0, return_inverse=True)
    t = inverse[tri]
    parent = list(range(len(points)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for a, b, c in t:
        ra, rb, rc = find(a), find(b), find(c)
        parent[ra] = rc
        parent[rb] = rc
    groups = {}
    for i, face in enumerate(t):
        groups.setdefault(find(face[0]), []).append(i)
    areas = (
        np.linalg.norm(
            np.cross(p[tri[:, 1]] - p[tri[:, 0]], p[tri[:, 2]] - p[tri[:, 0]]), axis=1
        )
        * 0.5
    )
    primary = max(groups.values(), key=lambda ids: float(areas[ids].sum()))
    stem = p[np.unique(tri[primary])]
    base = float(stem[:, 1].min())
    band = stem[stem[:, 1] < base + 0.20]
    centre = (band.min(0) + band.max(0)) * 0.5
    return np.array([centre[0], base, centre[2]], np.float32)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", type=Path, required=True)
    args = ap.parse_args()
    src = args.source
    out = ROOT / "assets/models"
    collision = ROOT / "assets/collision"
    catalog = {}
    records = []
    inputs = {}
    textures = {}

    def save_texture(data, kind, mat, wood):
        pbr = mat["pbrMetallicRoughness"]
        key = hashlib.sha256(
            (data or b"")
            + json.dumps(
                [
                    kind,
                    pbr.get("roughnessFactor", 1),
                    pbr.get("metallicFactor", 1),
                    wood,
                ]
            ).encode()
        ).hexdigest()[:16]
        target = f"realistic-tree-{key}-{kind}.png"
        if target in textures:
            return target
        if kind == "mr":
            im = (
                Image.open(io.BytesIO(data)).convert("RGB")
                if data
                else Image.new("RGB", (4, 4), (255, 255, 255))
            )
            im.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
            a = np.array(im)
            a[:, :, 1] = np.round(a[:, :, 1] * pbr.get("roughnessFactor", 1)).astype(
                "uint8"
            )
            a[:, :, 2] = np.round(a[:, :, 2] * pbr.get("metallicFactor", 1)).astype(
                "uint8"
            )
            im = Image.fromarray(a)
        else:
            im = Image.open(io.BytesIO(data)).convert(
                "RGB" if kind == "normal" else "RGBA"
            )
            im.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
            if kind == "normal":
                a = np.array(im)
                a[:, :, 1] = 255 - a[:, :, 1]
                im = Image.fromarray(a)
            elif wood:
                im.putalpha(255)
        im.save(out / target)
        textures[target] = hashlib.sha256((out / target).read_bytes()).hexdigest()
        return target

    for name in NAMES:
        levels = []
        for suffix in ["Quality", "LOD0", "LOD1", "LOD2"]:
            file = src / "models" / (name + "_" + suffix + ".glb")
            inputs[file.name] = hashlib.sha256(file.read_bytes()).hexdigest()
            levels.append(read_glb(file))
        assert sum(len(p["triangles"]) for p in levels[0]) == 50000
        origin = planting_origin(next(p for p in levels[0] if p["wood"]))
        names = [name] + [name + f"_Lod{i}" for i in [1, 2, 3]]
        counts = []
        positions = []
        for model, parts in zip(names, levels):
            entries = []
            total = 0
            for i, part in enumerate(parts):
                p = part["positions"] - origin
                n = part["normals"]
                uv = part["uv"].copy()
                uv[:, 1] = 1 - uv[:, 1]
                t = part["triangles"]
                verts = np.concatenate([p, n, uv], axis=1)[t.flatten()].astype("<f4")
                assert np.isfinite(verts).all()
                positions.append(p)
                total += len(t)
                filename = f"{model}_{i}.mesh"
                (out / filename).write_bytes(
                    b"SGM1" + struct.pack("<I", len(verts)) + verts.tobytes()
                )
                m = part["material"]
                assert (
                    m["pbrMetallicRoughness"].get("baseColorFactor", [1] * 4) == [1] * 4
                )
                entry = {
                    "mesh": filename,
                    "texture": save_texture(
                        part["images"]["base"], "base", m, part["wood"]
                    ),
                    "repeat": True,
                    "alphaCutoff": 0 if part["wood"] else 0.333,
                    "metallicRoughness": save_texture(
                        part["images"].get("mr"), "mr", m, part["wood"]
                    ),
                }
                if "normal" in part["images"]:
                    entry["normal"] = save_texture(
                        part["images"]["normal"], "normal", m, part["wood"]
                    )
                entries.append(entry)
            (out / (model + ".json")).write_text(
                json.dumps({"version": 1, "parts": entries}, indent=2) + "\n"
            )
            counts.append(total)
        # The lowest render LOD supplies a compact, leaf-free wood collision mesh.
        wood = next(p for p in levels[-1] if p["wood"])
        p = (wood["positions"] - origin).astype("<f4")
        t = wood["triangles"]
        pts, inverse = np.unique(p, axis=0, return_inverse=True)
        t = inverse[t].astype("<i4")
        tri = pts[t]
        t = t[
            np.linalg.norm(
                np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0]), axis=1
            )
            > 1e-8
        ]
        assert 0 < len(t) < 100000
        (collision / (name + ".sgc")).write_bytes(
            b"SGC1"
            + struct.pack("<II", len(pts), len(t))
            + pts.astype("<f4").tobytes()
            + t.astype("<i4").tobytes()
        )
        allp = np.concatenate(positions)
        catalog[name] = {
            "min": allp.min(0).tolist(),
            "max": allp.max(0).tolist(),
            "lods": names[1:],
            "lodDistances": [40, 90, 180],
        }
        records.append(
            {
                "name": name,
                "triangles": counts,
                "planting_origin": origin.tolist(),
                "wood_collision_triangles": len(t),
                "lodDistances": [40, 90, 180],
            }
        )
        print(
            name, counts, "planting origin", origin.tolist(), "wood collision", len(t)
        )
    for file in ["realistic-trees-license.txt", "more-realistic-trees-license.txt"]:
        shutil.copyfile(src / file, out / file)
    (out / "realistic-trees-bounds.json").write_text(
        json.dumps(catalog, indent=2) + "\n"
    )
    (out / "realistic-trees-provenance.json").write_text(
        json.dumps(
            {
                "version": 1,
                "author": "Nicholas-3D",
                "license": "CC BY 4.0",
                "source": str(src),
                "method": "Approved Quality / LOD0 / LOD1 / LOD2 GLBs; metres, Y-up; shared planting origin from principal trunk base; original UVs flipped for runtime; OpenGL normal green inverted for DirectX; alpha retained; textures capped at 2048; leaf-free far-LOD wood collision",
                "models": records,
                "source_sha256": inputs,
                "textures_sha256": textures,
            },
            indent=2,
        )
        + "\n"
    )


if __name__ == "__main__":
    main()
