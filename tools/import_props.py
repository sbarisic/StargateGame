"""Import the supplied Sketchfab prop scenes. Sources stay untouched.
Requires numpy and Pillow. Run from any directory; output is reproducible.
"""

import hashlib, json, struct
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "assets/models"
records = []
for pack, names in [
    ("wooden_box", ["wooden-crate"]),
    ("barrel", ["barrel-black", "barrel-red"]),
]:
    source = ROOT / "assets_itch" / pack
    doc = json.loads((source / "scene.gltf").read_text())
    buffers = [(source / b["uri"]).read_bytes() for b in doc["buffers"]]

    def accessor(index):
        a = doc["accessors"][index]
        v = doc["bufferViews"][a["bufferView"]]
        dtype = np.dtype({5123: "<u2", 5125: "<u4", 5126: "<f4"}[a["componentType"]])
        n = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a["type"]]
        return np.ndarray(
            (a["count"], n),
            dtype,
            buffer=buffers[v["buffer"]],
            offset=v.get("byteOffset", 0) + a.get("byteOffset", 0),
            strides=(v.get("byteStride", dtype.itemsize * n), dtype.itemsize),
        ).copy()

    nodes = []

    def visit(index, parent):
        node = doc["nodes"][index]
        assert not any(
            k in node for k in ("translation", "rotation", "scale")
        ), "Add explicit TRS support before importing another source"
        transform = parent @ np.array(
            node.get("matrix", np.eye(4).flatten(order="F").tolist())
        ).reshape(4, 4, order="F")
        if "mesh" in node:
            nodes.append((node["mesh"], transform))
        for child in node.get("children", []):
            visit(child, transform)

    for index in doc["scenes"][doc.get("scene", 0)]["nodes"]:
        visit(
            index, np.diag([0.01, 0.01, 0.01, 1]) if pack == "wooden_box" else np.eye(4)
        )
    assert len(nodes) == len(names)
    for name, (mesh, transform) in zip(names, nodes):
        parts = []
        lo = np.full(3, np.inf)
        hi = -lo
        for primitive in doc["meshes"][mesh]["primitives"]:
            a = primitive["attributes"]
            p = accessor(a["POSITION"])
            p = (np.c_[p, np.ones(len(p))] @ transform.T)[:, :3]
            n = accessor(a["NORMAL"]) @ np.linalg.inv(transform[:3, :3])
            n /= np.linalg.norm(n, axis=1)[:, None]
            uv = accessor(a["TEXCOORD_0"])
            uv[:, 1] = 1 - uv[:, 1]
            indices = accessor(primitive["indices"]).ravel()
            lo = np.minimum(lo, p.min(0))
            hi = np.maximum(hi, p.max(0))
            parts.append((p, n, uv, indices, doc["materials"][primitive["material"]]))
        center = (lo + hi) / 2
        entries = []
        for index, (p, n, uv, indices, material) in enumerate(parts):
            filename = f"{name}-{index}.mesh"
            vertices = np.c_[p - center, n, uv][indices].astype("<f4")
            (OUT / filename).write_bytes(
                b"SGM1" + struct.pack("<I", len(vertices)) + vertices.tobytes()
            )
            entry = {"mesh": filename}
            for key, ref in [
                ("texture", material["pbrMetallicRoughness"]["baseColorTexture"]),
                ("normal", material["normalTexture"]),
                (
                    "metallicRoughness",
                    material["pbrMetallicRoughness"]["metallicRoughnessTexture"],
                ),
            ]:
                imagepath = (
                    source
                    / doc["images"][doc["textures"][ref["index"]]["source"]]["uri"]
                )
                image = Image.open(imagepath).convert("RGB")
                image.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
                if key == "normal":
                    pixels = np.array(image)
                    pixels[:, :, 1] = 255 - pixels[:, :, 1]
                    image = Image.fromarray(
                        pixels
                    )  # Runtime expects DirectX normal green.
                filename = f"{pack}-{key}.png"
                image.save(OUT / filename)
                entry[key] = filename
            entries.append(entry)
        (OUT / (name + ".json")).write_text(
            json.dumps({"version": 1, "parts": entries}, indent=2) + "\n"
        )
        records.append(
            {
                "model": name,
                "sizeMetres": (hi - lo).tolist(),
                "vertices": sum(len(p[3]) for p in parts),
                "sourceMesh": mesh,
            }
        )
        print(name, "size", hi - lo)
    (OUT / (pack + "-license.txt")).write_bytes((source / "license.txt").read_bytes())
    records.append(
        {
            "source": str(source.relative_to(ROOT)),
            "files": [
                {
                    "path": str(p.relative_to(source)),
                    "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
                }
                for p in sorted(source.rglob("*"))
                if p.is_file()
            ],
        }
    )
(OUT / "props-provenance.json").write_text(
    json.dumps(
        {
            "version": 1,
            "conversion": "glTF scene transforms; metres Y-up; collider-centred; flipped UV V and normal green; textures capped at 2048",
            "sources": records,
        },
        indent=2,
    )
    + "\n"
)
