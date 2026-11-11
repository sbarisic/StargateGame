"""Cook the source-derived inspection trunk and voxel canopy. No collision writes."""

import argparse
import hashlib
import io
import json
import struct
from pathlib import Path

import numpy as np
from PIL import Image
from import_realistic_trees import read_glb
from cook_assets import srgb_linear, linear_srgb
from tree_volume_sources import prepared_tree
from tree_volume_data import downsample_canopy, normalize_normals, pad_attributes

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--model", default="MoreRealisticTree_01")
    parser.add_argument("--id", default="")
    parser.add_argument(
        "--prepared", type=Path, default=ROOT / "artifacts/cooked/models"
    )
    args = parser.parse_args()
    models = ROOT / "assets/models"
    if args.id and not all(c.isalnum() or c == "-" for c in args.id):
        raise ValueError("Prototype ID must contain letters, digits or hyphens")
    output = ROOT / "assets/vegetation/prototype" / args.id
    output.mkdir(parents=True, exist_ok=True)
    if args.source:
        extraction = json.loads((args.source / "extraction.json").read_text())
        if extraction.get("model", "MoreRealisticTree_01") != args.model:
            raise ValueError("Extraction belongs to a different tree model")
        original = json.loads((models / (args.model + ".json")).read_text())
        wood_material = next(
            p for p in original["parts"] if p.get("alphaCutoff", 0) == 0
        )
        provenance = json.loads(
            (models / "realistic-trees-provenance.json").read_text()
        )
        origin = np.array(
            next(
                m["planting_origin"]
                for m in provenance["models"]
                if m["name"] == args.model
            )
        )
        leaf = None
        for level, lod in enumerate(extraction["lods"]):
            source = args.source / lod["file"]
            if hashlib.sha256(source.read_bytes()).hexdigest() != lod["sha256"]:
                raise ValueError("Extraction checksum mismatch")
            parts = read_glb(source)
            wood = next(p for p in parts if p["wood"])
            if level == 0:
                leaf = next(p for p in parts if not p["wood"])
            uv = wood["uv"].copy()
            uv[:, 1] = 1 - uv[:, 1]
            data = np.concatenate(
                [wood["positions"] - origin, wood["normals"], uv], axis=1
            )[wood["triangles"].flatten()].astype("<f4")
            name = (
                f"prototype-{args.id}-trunk-{level}"
                if args.id
                else f"prototype-trunk-{level}"
            )
            (models / (name + ".mesh")).write_bytes(
                b"SGM1" + struct.pack("<i", len(data)) + data.tobytes()
            )
            (models / (name + ".json")).write_text(
                json.dumps(
                    {
                        "version": 1,
                        "parts": [
                            dict(wood_material, mesh=name + ".mesh", surfaceRole="wood")
                        ],
                    },
                    indent=2,
                )
            )
        leaf["positions"] = leaf["positions"] - origin
        leaves = [leaf]
        trunk_levels = len(extraction["lods"])
    else:
        if not args.id:
            raise ValueError("Prepared prototypes need an explicit --id")
        leaves, extraction, trunk_levels = prepared_tree(
            args.model, args.id, args.prepared
        )
    positions = np.concatenate([leaf["positions"] for leaf in leaves])
    minimum = positions.min(0) - 0.03
    maximum = positions.max(0) + 0.03
    cell = (maximum - minimum).max() / 254
    dimensions = np.ceil((maximum - minimum) / cell).astype(int) + 2
    dimensions = np.minimum(256, 2 ** np.ceil(np.log2(dimensions)).astype(int))
    maximum = minimum + dimensions * cell
    count = int(np.prod(dimensions))
    weights = np.zeros(count, np.float32)
    colors = np.zeros((count, 3), np.float32)
    normals = np.zeros((count, 3), np.float32)
    for leaf in leaves:
        texture = (
            np.array(Image.open(io.BytesIO(leaf["images"]["base"])).convert("RGBA"))
            / 255.0
        )
        texture[:, :, :3] = srgb_linear(texture[:, :, :3])
        # Imported needle/branch cards span more voxels than individual source leaves.
        # Area-dependent sample buckets avoid baking these cards into isolated dots.
        faces = leaf["triangles"]
        points = leaf["positions"][faces]
        area_all = (
            np.linalg.norm(
                np.cross(points[:, 1] - points[:, 0], points[:, 2] - points[:, 0]),
                axis=1,
            )
            * 0.5
        )
        counts = np.clip(
            2 ** np.ceil(np.log2(np.maximum(48, area_all / (cell * cell) * 4))),
            64,
            8192,
        ).astype(int)
        if args.source:
            counts[:] = 48  # Keep the established source-leaf bake reproducible.
        batches = ((count, faces[counts == count]) for count in np.unique(counts))
        for sample_count, selected_faces in batches:
            index = np.arange(sample_count) + 0.5
            u = np.sqrt(index / len(index))
            v = np.mod(index * 0.61803398875, 1)
            barycentric = np.stack([1 - u, u * (1 - v), u * v], axis=1)
            batch_size = max(1, min(4096, 500000 // sample_count))
            for start in range(0, len(selected_faces), batch_size):
                triangles = selected_faces[start : start + batch_size]
                points = leaf["positions"][triangles]
                area = (
                    np.linalg.norm(
                        np.cross(
                            points[:, 1] - points[:, 0], points[:, 2] - points[:, 0]
                        ),
                        axis=1,
                    )
                    * 0.5
                )
                samples = np.einsum("si,tij->tsj", barycentric, points)
                uv = np.einsum("si,tij->tsj", barycentric, leaf["uv"][triangles])
                uv = (
                    np.mod(uv, 1)
                    if leaf.get("repeat", True)
                    else np.clip(uv, 0, np.nextafter(1.0, 0.0))
                )
                texels = texture[
                    (uv[:, :, 1] * texture.shape[0]).astype(int),
                    (uv[:, :, 0] * texture.shape[1]).astype(int),
                ]
                occupied = texels[:, :, 3] >= leaf.get("cutoff", 0.333)
                voxels = np.clip(
                    ((samples - minimum) / cell).astype(int), 0, dimensions - 1
                )
                flat = voxels[:, :, 0] + dimensions[0] * (
                    voxels[:, :, 1] + dimensions[1] * voxels[:, :, 2]
                )
                contribution = np.broadcast_to(
                    area[:, None] / (len(index) * cell * cell), flat.shape
                )[occupied]
                selected = flat[occupied]
                np.add.at(weights, selected, contribution)
                np.add.at(
                    colors, selected, texels[:, :, :3][occupied] * contribution[:, None]
                )
                normal = leaf["normals"][triangles].mean(1)
                normal *= np.where(normal[:, 1:2] < 0, -1, 1)
                np.add.at(
                    normals,
                    selected,
                    np.broadcast_to(normal[:, None, :], samples.shape)[occupied]
                    * contribution[:, None],
                )
    shape = tuple(dimensions[::-1])
    colors = (colors / np.maximum(weights[:, None], 1e-6)).reshape(*shape, 3)
    normals = normalize_normals(normals).reshape(*shape, 3)
    # Version 1 accumulated this opacity once per .65-cell camera step.
    # Retain that optical thickness, but now integrate it by physical distance.
    reference_distance = float(cell * 0.65)
    density = np.minimum(weights * 2, -np.log(1 / 255)).reshape(shape)
    occupancy = -np.expm1(-weights * 2).reshape(shape)
    levels = []
    total = 0
    level = 0
    while True:
        padded_colors, padded_normals = pad_attributes(colors, normals, density)
        slabs = []
        depth, height, width = density.shape
        for z in range(depth):
            color = np.concatenate(
                [linear_srgb(padded_colors[z]), occupancy[z, ..., None]], axis=-1
            )
            normal = np.concatenate(
                [padded_normals[z] * 0.5 + 0.5, -np.expm1(-density[z, ..., None])],
                axis=-1,
            )
            paths = []
            for role, values in [("color", color), ("normal", normal)]:
                name = f"{role}-{level}-{z}.bin"
                data = np.clip(np.rint(values * 255), 0, 255).astype(np.uint8).tobytes()
                (output / name).write_bytes(data)
                total += len(data)
                paths.append((output / name).relative_to(ROOT / "assets").as_posix())
            slabs.append(paths)
        levels.append({"size": [width, height, depth], "slices": slabs})
        if max(width, height, depth) == 1:
            break
        del padded_colors, padded_normals
        colors, normals, density, occupancy = downsample_canopy(
            colors, normals, density, occupancy
        )
        level += 1
    if total > 160 * 1024 * 1024:
        raise ValueError("Prototype exceeds its 160 MiB resource cap")
    descriptor = {
        "version": 2,
        "model": args.model,
        "trunkLevels": trunk_levels,
        "densityEncoding": "opacity-at-reference-distance",
        "densityReferenceDistance": reference_distance,
        "minimum": minimum.tolist(),
        "maximum": maximum.tolist(),
        "cellSize": float(cell),
        "bytes": total,
        "levels": levels,
        "source": extraction,
        "inspectionOnly": True,
    }
    (output / "canopy.json").write_text(json.dumps(descriptor, indent=2))
    from bake_canopy_ambient import bake
    bake(output / "canopy.json")
    print(
        f"Canopy {dimensions.tolist()}, {total/1048576:.1f} MiB; source crown retained",
        flush=True,
    )


if __name__ == "__main__":
    main()
