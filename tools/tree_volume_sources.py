"""Read existing prepared tree parts for inspection volumes without changing originals."""

import hashlib
import json
import struct
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]


def prepared_tree(model, identity, prepared):
    models = ROOT / "assets/models"
    inputs = {}

    def source(name):
        path = prepared / name
        if not path.exists():
            path = models / name
        data = path.read_bytes()
        inputs[name] = hashlib.sha256(data).hexdigest()
        return data

    variants = json.loads((ROOT / "assets/vegetation/trees.json").read_text())[model]
    leaves = []
    for level, variant in enumerate(variants[:3]):
        manifest = json.loads(source(variant + ".json"))
        wood = []
        for part in manifest["parts"]:
            is_leaf = (
                part.get("surfaceRole") == "leaf" or part.get("alphaCutoff", 0) > 0
            )
            if not is_leaf:
                for key in ("mesh", "texture", "normal", "metallicRoughness"):
                    if isinstance(part.get(key), str):
                        source(part[key])
                wood.append(dict(part, surfaceRole="wood"))
            if not is_leaf or level != 0:
                continue
            data = source(part["mesh"])
            magic, count = struct.unpack_from("<4si", data)
            if magic != b"SGM1" or count % 3 or len(data) != 8 + count * 32:
                raise ValueError("Invalid prepared tree geometry")
            vertices = np.frombuffer(data, dtype="<f4", offset=8).reshape(count, 8)
            uv = vertices[:, 6:8].copy()
            uv[:, 1] = (
                1 - uv[:, 1]
            )  # Runtime UVs are bottom-origin; PNG rows are top-origin.
            leaves.append(
                dict(
                    positions=vertices[:, :3],
                    normals=vertices[:, 3:6],
                    uv=uv,
                    triangles=np.arange(count).reshape(-1, 3),
                    images={"base": source(part["texture"])},
                    cutoff=part.get("alphaCutoff", 0.333),
                    repeat=part.get("repeat", False),
                )
            )
        if not wood:
            raise ValueError(f"No separate wood in {variant}")
        (models / f"prototype-{identity}-trunk-{level}.json").write_text(
            json.dumps(dict(version=1, parts=wood), indent=2)
        )
    if not leaves:
        raise ValueError(f"No foliage in {model}")
    return (
        leaves,
        dict(
            model=model,
            sources=inputs,
            inputKind="prepared-game-meshes",
            license="Existing asset attribution: THIRD-PARTY-NOTICES.md and assets/vegetation/provenance.json",
        ),
        len(variants[:3]),
    )
