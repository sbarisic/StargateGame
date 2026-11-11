"""Render-only tree variants and baked leaf-cluster LODs. Original meshes/colliders stay untouched."""

import hashlib, json, struct, math, argparse
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
MODELS = ROOT / "assets/models"
OUT = ROOT / "artifacts/vegetation/models"


def mesh(file):
    return (
        np.frombuffer(
            ((OUT / file) if (OUT / file).exists() else (MODELS / file)).read_bytes(),
            "<f4",
            offset=8,
        )
        .reshape(-1, 8)
        .copy()
    )


def write_mesh(name, data):
    data = np.asarray(data, dtype="<f4").reshape(-1, 8)
    (OUT / name).write_bytes(b"SGM1" + struct.pack("<i", len(data)) + data.tobytes())


def bake_cluster(triangles, texture, axes, minimum, maximum, size=48):
    """Orthographic alpha-tested bake. Depth order and source UVs preserve holes and leaf colour."""
    result = np.zeros((size, size, 4), np.uint8)
    depth = np.full((size, size), -np.inf)
    a, b = axes
    c = 3 - a - b
    span = np.maximum(maximum - minimum, 0.02)
    for t in triangles:
        xy = (t[:, [a, b]] - minimum[[a, b]]) / span[[a, b]] * (size - 3) + 1
        lo = np.maximum(0, np.floor(xy.min(0)).astype(int))
        hi = np.minimum(size - 1, np.ceil(xy.max(0)).astype(int))
        if np.any(hi < lo):
            continue
        x, y = np.meshgrid(np.arange(lo[0], hi[0] + 1), np.arange(lo[1], hi[1] + 1))
        p = np.stack([x, y], -1)
        v0 = xy[1] - xy[0]
        v1 = xy[2] - xy[0]
        v2 = p - xy[0]
        den = v0[0] * v1[1] - v1[0] * v0[1]
        if abs(den) < 1e-6:
            continue
        u = (v2[:, :, 0] * v1[1] - v1[0] * v2[:, :, 1]) / den
        v = (v0[0] * v2[:, :, 1] - v2[:, :, 0] * v0[1]) / den
        w = 1 - u - v
        inside = (u >= 0) & (v >= 0) & (w >= 0)
        uv = (
            t[0, 6:] * w[:, :, None]
            + t[1, 6:] * u[:, :, None]
            + t[2, 6:] * v[:, :, None]
        )
        tx = np.clip(
            (np.mod(uv[:, :, 0], 1) * texture.shape[1]).astype(int),
            0,
            texture.shape[1] - 1,
        )
        ty = np.clip(
            (np.mod(uv[:, :, 1], 1) * texture.shape[0]).astype(int),
            0,
            texture.shape[0] - 1,
        )
        col = texture[texture.shape[0] - 1 - ty, tx]
        z = t[0, c] * w + t[1, c] * u + t[2, c] * v
        dst = depth[lo[1] : hi[1] + 1, lo[0] : hi[0] + 1]
        keep = inside & (col[:, :, 3] >= 85) & (z >= dst)
        result[lo[1] : hi[1] + 1, lo[0] : hi[0] + 1][keep] = col[keep]
        dst[keep] = z[keep]
    return result


def clusters(data, texture, cell, name):
    tris = data.reshape(-1, 3, 8)
    centres = tris[:, :, :3].mean(1)
    keys = np.floor(centres / cell).astype(int)
    _, ids = np.unique(keys, axis=0, return_inverse=True)
    count = int(ids.max()) + 1
    tile = 50
    columns = math.ceil(math.sqrt(count * 3))
    side = 2 ** math.ceil(math.log2(columns * tile))
    atlas = np.zeros((side, side, 4), np.uint8)
    vertices = []
    crown = data[:, :3].mean(0)
    for group in range(count):
        triangles = tris[ids == group]
        lo = triangles[:, :, :3].min((0, 1))
        hi = triangles[:, :, :3].max((0, 1))
        centre = (lo + hi) * 0.5
        for view, (a, b) in enumerate(((0, 1), (2, 1), (0, 2))):
            pixels = bake_cluster(triangles, texture, (a, b), lo, hi)
            index = group * 3 + view
            x = (index % columns) * tile + 1
            y = (index // columns) * tile + 1
            atlas[y : y + 48, x : x + 48] = pixels
            c = 3 - a - b
            normal = centre - crown
            normal[1] += np.linalg.norm(normal) * 0.4
            normal /= max(np.linalg.norm(normal), 0.001)
            # UV padding matches the bake's one-pixel inset.
            points = []
            for u, v in ((0, 0), (1, 0), (1, 1), (0, 1)):
                p = centre.copy()
                p[a] = lo[a] + (hi[a] - lo[a]) * u
                p[b] = lo[b] + (hi[b] - lo[b]) * v
                points.append(
                    [*p, *normal, (x + 1 + 45 * u) / side, (y + 1 + 45 * v) / side]
                )
            for i in (0, 1, 2, 0, 2, 3):
                vertices.append(points[i])
    Image.fromarray(np.flipud(atlas)).save(OUT / (name + ".png"))
    write_mesh(name + ".mesh", vertices)
    return dict(
        mesh=name + ".mesh",
        texture=name + ".png",
        surfaceRole="leaf",
        alphaCutoff=0.333,
        repeat=False,
    )


def provenance_sources(definitions):
    result = {}
    for base, bounds in definitions.items():
        names = [base] + bounds["lods"]
        if (MODELS / (base + "_detail.json")).exists():
            names.append(base + "_detail")
        for name in names:
            manifest = MODELS / (name + ".json")
            result[manifest.name] = hashlib.sha256(manifest.read_bytes()).hexdigest()
            for part in json.loads(manifest.read_text())["parts"]:
                for field in ("mesh", "texture", "normal", "metallicRoughness"):
                    if field in part and part[field] not in result:
                        result[part[field]] = hashlib.sha256(
                            (MODELS / part[field]).read_bytes()
                        ).hexdigest()
    return result


def main(only=None):
    OUT.mkdir(parents=True, exist_ok=True)
    definitions = {}
    for file in ("trees-bounds.json", "realistic-trees-bounds.json"):
        definitions.update(json.loads((MODELS / file).read_text()))
    target = ROOT / "assets/vegetation"
    if only and (only not in definitions or not (target / "trees.json").exists()):
        raise ValueError(
            "A targeted bake needs a known tree and an existing complete vegetation bake."
        )
    mapping = json.loads((target / "trees.json").read_text()) if only else {}
    sources = {}
    for base, bounds in definitions.items():
        if only and base != only:
            continue
        names = [base] + bounds["lods"]
        doc = json.loads((MODELS / (base + ".json")).read_text())
        base_parts = doc["parts"]
        sources[base] = hashlib.sha256(
            (MODELS / (base + ".json")).read_bytes()
        ).hexdigest()
        variants = [("", base_parts)]
        if base == "European_White_Birch_Hero_Winter":
            # Attach leafy branch cards to the existing outer-branch distribution.
            summer = [dict(p) for p in base_parts if p.get("alphaCutoff", 0) == 0]
            green = json.loads((MODELS / "Leave-Tree-01a.json").read_text())["parts"]
            leaf = next(p for p in green if p.get("alphaCutoff", 0) > 0)
            anchors = np.concatenate(
                [
                    mesh(p["mesh"])[:, :3]
                    for p in base_parts
                    if p.get("alphaCutoff", 0) > 0
                ]
            )
            keys = np.floor(anchors / 0.45).astype(int)
            _, indices = np.unique(keys, axis=0, return_index=True)
            anchors = anchors[indices]
            verts = []
            for i, p in enumerate(anchors):
                yaw = i * 2.39996
                side = np.array([math.cos(yaw), 0.15, math.sin(yaw)]) * 0.40
                up = np.array([0, 0.60, 0])
                normal = np.cross(side, up)
                normal /= np.linalg.norm(normal)
                points = [
                    np.r_[p - side - up * 0.3, normal, 0, 0.5],
                    np.r_[p + side - up * 0.3, normal, 0.5, 0.5],
                    np.r_[p + side + up * 0.7, normal, 0.5, 1],
                    np.r_[p - side + up * 0.7, normal, 0, 1],
                ]
                verts.extend(points[j] for j in (0, 1, 2, 0, 2, 3))
            write_mesh("veg-summer-branches.mesh", verts)
            summer.append(
                dict(
                    mesh="veg-summer-branches.mesh",
                    texture=leaf["texture"],
                    surfaceRole="leaf",
                    alphaCutoff=0.5,
                    repeat=False,
                )
            )
            variants.append(("-summer", summer))
        for suffix, parts in variants:
            output_names = []
            for level, original in enumerate(names):
                name = "veg-" + original + suffix
                output = []
                # Optional near-only refinement. Cluster LODs still bake from the
                # retained base mesh, preserving distant silhouettes and collision.
                detail = MODELS / (base + "_detail.json")
                render_parts = (
                    json.loads(detail.read_text())["parts"]
                    if level == 0 and not suffix and detail.exists()
                    else parts
                )
                for index, part in enumerate(render_parts):
                    source = mesh(part["mesh"])
                    leaf = part.get("alphaCutoff", 0) > 0
                    item = dict(part)
                    item["surfaceRole"] = "leaf" if leaf else "wood"
                    if leaf and level > 0:
                        tex = np.array(
                            Image.open(MODELS / part["texture"]).convert("RGBA")
                        )
                        height = max(1, float(source[:, 1].max() - source[:, 1].min()))
                        cell = height * (
                            0.065 if level == 1 else 0.11 if level == 2 else 0.18
                        )
                        item = clusters(source, tex, cell, name + f"-{index}")
                    else:
                        # Preserve wood's original LOD geometry and branch gaps.
                        if not leaf and level > 0:
                            lodparts = json.loads(
                                (MODELS / (original + ".json")).read_text()
                            )["parts"]
                            woods = [
                                p for p in lodparts if p.get("alphaCutoff", 0) == 0
                            ]
                            wood_index = sum(
                                p.get("alphaCutoff", 0) == 0 for p in parts[:index]
                            )
                            if wood_index >= len(woods):
                                continue
                            item = dict(woods[wood_index])
                            item["surfaceRole"] = "wood"
                            source = mesh(item["mesh"])
                        if leaf:
                            centre = source[:, :3].mean(0)
                            outward = source[:, :3] - centre
                            outward[:, 1] += np.linalg.norm(outward, axis=1) * 0.35
                            outward /= np.maximum(
                                np.linalg.norm(outward, axis=1, keepdims=True), 0.001
                            )
                            n = source[:, 3:6] * 0.45 + outward * 0.55
                            n /= np.maximum(
                                np.linalg.norm(n, axis=1, keepdims=True), 0.001
                            )
                            source[:, 3:6] = n
                            item["mesh"] = name + f"-{index}.mesh"
                            write_mesh(item["mesh"], source)
                    output.append(item)
                (OUT / (name + ".json")).write_text(
                    json.dumps(dict(version=1, parts=output), indent=2)
                )
                output_names.append(name)
            mapping[base + suffix] = output_names
        print("Prepared", base, flush=True)
    target = ROOT / "assets/vegetation"
    target.mkdir(exist_ok=True)
    (target / "trees.json").write_text(json.dumps(mapping, indent=2))
    (target / "provenance.json").write_text(
        json.dumps(
            dict(
                tool="tools/prepare_vegetation.py",
                version=2,
                sources=provenance_sources(definitions),
                license="Derivative runtime foliage; original tree licenses and attribution apply.",
                collision="Original sources and collision bounds unchanged",
            ),
            indent=2,
        )
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=OUT)
    parser.add_argument("--only")
    args = parser.parse_args()
    OUT = args.output
    main(args.only)
