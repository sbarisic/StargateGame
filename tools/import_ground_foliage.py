"""Blender background importer for some_foliage; preserves original sources.
blender -b --python tools/import_ground_foliage.py
"""

from mathutils import Vector
import bpy, sys, json, struct, gzip, hashlib, math, site

sys.path.append(site.getusersitepackages())
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from import_terrain_pbr import linear, srgb, shrink, quantize

SOURCE = ROOT / "assets_itch/foliage/some_foliage"
OUT = ROOT / "assets/foliage"


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(SOURCE / "scene.gltf"))
    materials = []
    for name in ["Grass", "Ferns", "Dead_Bush"]:
        maps = {
            k: SOURCE / "textures" / f"{name}_{k}.png"
            for k in ["baseColor", "normal", "metallicRoughness"]
        }
        arrays = {
            k: np.asarray(Image.open(p).convert("RGBA"), np.float32) / 255
            for k, p in maps.items()
        }
        c = linear(arrays["baseColor"][:, :, :3])
        alpha = arrays["baseColor"][:, :, 3:4]
        n = arrays["normal"][:, :, :3] * 2 - 1
        n[:, :, 1] *= -1
        m = np.stack(
            [
                np.ones(c.shape[:2]),
                arrays["metallicRoughness"][:, :, 1],
                np.full(c.shape[:2], 0.5),
                np.zeros(c.shape[:2]),
            ],
            axis=-1,
        )
        coverage = float((alpha >= 0.4).mean())
        file = name + ".tpb.gz"
        with (OUT / file).open("wb") as raw, gzip.GzipFile(
            filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=6
        ) as f:
            f.write(b"TPB1" + struct.pack("<II", 2048, 12))
            for level in range(12):
                norm = n / np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-8)
                adjusted = alpha
                if level > 0:
                    lo, hi = 0.25, 8.0
                    for _ in range(12):
                        mid = (lo + hi) / 2
                        if (alpha * mid >= 0.4).mean() < coverage:
                            lo = mid
                        else:
                            hi = mid
                    adjusted = np.clip(alpha * ((lo + hi) / 2), 0, 1)
                for a in [
                    np.concatenate([srgb(c), adjusted], axis=-1),
                    np.concatenate([norm * 0.5 + 0.5, np.ones_like(alpha)], axis=-1),
                    m,
                ]:
                    f.write(np.flipud(quantize(a)).tobytes())
                if level < 11:
                    c = shrink(c)
                    alpha = shrink(alpha)
                    n = shrink(n)
                    m = shrink(m)
        materials.append(
            {
                "name": name,
                "file": file,
                "alphaCutoff": 0.4,
                "source": {
                    k: {
                        "file": str(p.relative_to(ROOT)).replace("\\", "/"),
                        "sha256": sha(p),
                    }
                    for k, p in maps.items()
                },
                "sha256": sha(OUT / file),
            }
        )
    models = []
    for obj in sorted(
        [o for o in bpy.context.scene.objects if o.type == "MESH"], key=lambda o: o.name
    ):
        name = obj.name
        kind = "grass" if "Grass" in name else "fern" if "Fern" in name else "bush"
        material = {"grass": 0, "fern": 1, "bush": 2}[kind]
        mesh = obj.data
        mesh.transform(obj.matrix_world)
        obj.matrix_world.identity()
        # Source scene is in centimetres and contains gallery translations.
        points = np.array([v.co[:] for v in mesh.vertices])
        lo = points.min(0)
        hi = points.max(0)
        # Blender import is Z-up. Bake to metre-shaped Y-up, normalized to unit height.
        height = hi[2] - lo[2]
        assert height > 0
        centre = (lo + hi) * 0.5
        centre[2] = lo[2]
        for v in mesh.vertices:
            v.co = (v.co - Vector(centre)) / height
        entries = []
        for level in range(2):
            copy = obj.copy()
            copy.data = mesh.copy()
            bpy.context.collection.objects.link(copy)
            bpy.context.view_layer.objects.active = copy
            copy.select_set(True)
            if level:
                mod = copy.modifiers.new("Distance LOD", "DECIMATE")
                mod.ratio = 0.6 if kind == "grass" else 0.45
                mod.use_collapse_triangulate = True
                bpy.ops.object.modifier_apply(modifier=mod.name)
            data = copy.data
            data.calc_loop_triangles()
            uv = data.uv_layers.active
            assert uv
            values = []
            for tri in data.loop_triangles:
                for li in tri.loops:
                    loop = data.loops[li]
                    v = data.vertices[loop.vertex_index]
                    p = v.co
                    n = (
                        loop.normal.normalized()
                        if loop.normal.length > 1e-6
                        else tri.normal.normalized()
                    )
                    u = uv.data[li].uv
                    values.append([p.x, p.z, -p.y, n.x, n.z, -n.y, u.x, u.y])
            vertices = np.asarray(values, dtype="<f4")
            vertices[:, 1] = np.clip(vertices[:, 1], 0, 1)
            assert np.isfinite(vertices).all()
            file = f"{name}-lod{level}.mesh"
            (OUT / file).write_bytes(
                b"SGM1" + struct.pack("<I", len(vertices)) + vertices.tobytes()
            )
            entries.append(
                {
                    "file": file,
                    "triangles": len(vertices) // 3,
                    "sha256": sha(OUT / file),
                }
            )
            bpy.data.objects.remove(copy, do_unlink=True)
        models.append(
            {
                "name": name,
                "kind": kind,
                "material": material,
                "sourceHeightMetres": float(height / 100),
                "min": [
                    -float((hi[0] - lo[0]) / height / 2),
                    0,
                    -float((hi[1] - lo[1]) / height / 2),
                ],
                "max": [
                    float((hi[0] - lo[0]) / height / 2),
                    1,
                    float((hi[1] - lo[1]) / height / 2),
                ],
                "lods": entries,
            }
        )
        print(name, kind, [e["triangles"] for e in entries], flush=True)
    (OUT / "manifest.json").write_text(
        json.dumps(
            {
                "version": 1,
                "source": str(SOURCE.relative_to(ROOT)).replace("\\", "/"),
                "sceneSha256": sha(SOURCE / "scene.gltf"),
                "bufferSha256": sha(SOURCE / "scene.bin"),
                "materials": materials,
                "models": models,
                "changes": "Gallery transforms removed, unit-height Y-up meshes; Blender bottom-left UVs matching bottom-up texture rows; DirectX normals; alpha-preserving mipmaps; decimated distance LOD",
            },
            indent=2,
        )
        + "\n"
    )
    (OUT / "license.txt").write_bytes((SOURCE / "license.txt").read_bytes())
    if (ROOT / "assets_itch/foliage/realistic_grass_pack/scene.gltf").exists():
        from import_realistic_grass import run

        run()


if __name__ == "__main__":
    main()
