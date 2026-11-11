"""Reproducible Tree Pack 3D OBJ import. Source files are never modified.
Run Blender in background with --python tools/import_tree_pack.py -- --source PATH.
The pack has solid leaf geometry and material colours, not texture atlases.
"""

import argparse
import hashlib
import json
import struct
import sys
import zlib
from pathlib import Path
import bpy
import bmesh
import numpy as np

ROOT = Path(__file__).resolve().parents[1]


def obj(path):
    vertices, groups, material = [], {}, None
    for line in path.read_text().splitlines():
        words = line.split()
        if not words:
            continue
        if words[0] == "v":
            vertices.append(tuple(map(float, words[1:4])))
        elif words[0] == "usemtl":
            material = words[1]
        elif words[0] == "f":
            indices = [int(w.split("/")[0]) for w in words[1:]]
            indices = [i - 1 if i > 0 else len(vertices) + i for i in indices]
            for i in range(1, len(indices) - 1):
                groups.setdefault(material, []).append(
                    (indices[0], indices[i], indices[i + 1])
                )
    return np.asarray(vertices, dtype=np.float32), groups


def texture(path, linear):
    # Store sRGB: the runtime converts model albedo back to linear lighting.
    rgb = [
        round(255 * (12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055))
        for c in linear
    ]

    def chunk(tag, data):
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data))
        )

    pixels = (b"\0" + bytes(rgb) * 8) * 8
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", 8, 8, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(pixels))
        + chunk(b"IEND", b"")
    )


def simplify(points, faces, ratio):
    used = np.unique(faces)
    remap = np.zeros(len(points), dtype=np.int32)
    remap[used] = np.arange(len(used))
    mesh = bpy.data.meshes.new("tree-import")
    mesh.from_pydata(points[used].tolist(), [], remap[np.asarray(faces)].tolist())
    # OBJ exports contain coincident split vertices. Weld within each material
    # before decimation so disconnected seams do not prevent useful LODs.
    welded = bmesh.new()
    welded.from_mesh(mesh)
    bmesh.ops.remove_doubles(welded, verts=list(welded.verts), dist=0.00001)
    welded.to_mesh(mesh)
    welded.free()
    mesh.update()
    item = bpy.data.objects.new("tree-import", mesh)
    bpy.context.collection.objects.link(item)
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    if ratio < 1:
        modifier = item.modifiers.new("LOD", "DECIMATE")
        modifier.ratio = ratio
        modifier.use_collapse_triangulate = True
    evaluated = item.evaluated_get(bpy.context.evaluated_depsgraph_get())
    result = evaluated.to_mesh()
    result.calc_loop_triangles()
    vertices = np.asarray([v.co[:] for v in result.vertices], dtype="<f4")
    normals = np.asarray([v.normal[:] for v in result.vertices], dtype="<f4")
    triangles = np.asarray([t.vertices[:] for t in result.loop_triangles], dtype="<i4")
    evaluated.to_mesh_clear()
    bpy.data.objects.remove(item, do_unlink=True)
    bpy.data.meshes.remove(mesh)
    t = vertices[triangles]
    triangles = triangles[
        np.linalg.norm(np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0]), axis=1) > 1e-8
    ]
    return vertices, normals, triangles


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--source", type=Path, default=ROOT / "assets_itch/Tree Pack 3D"
    )
    args = parser.parse_args(
        sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    )
    out, collision = ROOT / "assets/models", ROOT / "assets/collision"
    bounds, records, sources = {}, [], []
    # Four green crowns plus the pack's original autumn-coloured variant.
    colours = {
        "bark": (0.085, 0.060, 0.037),
        "leaves": (0.035, 0.18, 0.018),
        "autumn": (0.34, 0.155, 0.035),
    }
    for key, colour in colours.items():
        texture(out / ("tree-pack-" + key + ".png"), colour)
    for number in range(1, 6):
        source = next((args.source / str(number)).glob("*.obj"))
        vertices, groups = obj(source)
        wood = [
            key for key in groups if key.startswith("trunk") or key == "Material.002"
        ]
        assert len(wood) == 1 and len(groups) == 2
        wood = wood[0]
        wood_vertices = vertices[np.unique(groups[wood])]
        foot = wood_vertices[wood_vertices[:, 1] < wood_vertices[:, 1].min() + 0.12]
        shift = np.array([foot[:, 0].mean(), vertices[:, 1].min(), foot[:, 2].mean()])
        vertices -= shift
        name = "TreePack3D_" + str(number)
        all_positions = [vertices]
        triangles_per_lod = []
        # Keep authored curved branches intact nearby. Aggressive collapse at
        # close range turns the thin branch tubes into visible pointed wedges.
        for lod, (wood_ratio, leaf_ratio) in enumerate(
            ((1, 1), (0.35, 0.4), (0.10, 0.12))
        ):
            model = name + (f"_Lod{lod}" if lod else "")
            entries, triangle_count = [], 0
            for part, (material, faces) in enumerate(sorted(groups.items())):
                trunk = material == wood
                p, n, t = simplify(vertices, faces, wood_ratio if trunk else leaf_ratio)
                all_positions.append(p)
                uv = np.full((len(p), 2), 0.5, dtype=np.float32)
                packed = np.concatenate((p, n, uv), axis=1)[t.flatten()].astype("<f4")
                assert np.isfinite(packed).all()
                filename = f"{model}_{part}.mesh"
                (out / filename).write_bytes(
                    b"SGM1" + struct.pack("<I", len(packed)) + packed.tobytes()
                )
                tint = "bark" if trunk else "autumn" if number == 2 else "leaves"
                # Solid leaf polygons still need the renderer's two-sided foliage lighting.
                entries.append(
                    {
                        "mesh": filename,
                        "texture": f"tree-pack-{tint}.png",
                        "repeat": False,
                        "alphaCutoff": 0 if trunk else 0.5,
                    }
                )
                triangle_count += len(t)
            (out / (model + ".json")).write_text(
                json.dumps({"version": 1, "parts": entries}, indent=2) + "\n"
            )
            triangles_per_lod.append(triangle_count)
        p, _, t = simplify(vertices, groups[wood], min(1, 800 / len(groups[wood])))
        (collision / (name + ".sgc")).write_bytes(
            b"SGC1" + struct.pack("<II", len(p), len(t)) + p.tobytes() + t.tobytes()
        )
        full = np.concatenate(all_positions)
        bounds[name] = {
            "min": full.min(0).tolist(),
            "max": full.max(0).tolist(),
            "lods": [name + "_Lod1", name + "_Lod2"],
        }
        records.append(
            {
                "model": name,
                "source": source.relative_to(args.source).as_posix(),
                "shift": shift.tolist(),
                "height_m": float(np.ptp(vertices[:, 1])),
                "triangles_per_lod": triangles_per_lod,
                "wood_collision_triangles": len(t),
            }
        )
        for path in sorted(source.parent.iterdir()):
            if path.suffix.lower() in (".obj", ".mtl"):
                sources.append(
                    {
                        "path": path.relative_to(args.source).as_posix(),
                        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                    }
                )
        print(name, triangles_per_lod, "collision", len(t), flush=True)
    (out / "tree-pack-bounds.json").write_text(json.dumps(bounds, indent=2) + "\n")
    (out / "tree-pack-provenance.json").write_text(
        json.dumps(
            {
                "version": 1,
                "source": "User-supplied assets_itch/Tree Pack 3D",
                "blender": bpy.app.version_string,
                "units": "Authored units treated as metres; Y-up; trunk-root centred",
                "materials_linear_rgb": colours,
                "license": "No license or author notice supplied inside this local pack; no redistribution license inferred.",
                "models": records,
                "sources": sources,
            },
            indent=2,
        )
        + "\n"
    )


if __name__ == "__main__":
    main()
