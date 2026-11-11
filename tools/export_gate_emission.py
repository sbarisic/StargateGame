"""Offline material-assignment masks on existing runtime UVs; source blend is read-only.
blender --background <Stargate_and_DHD.blend> --python tools/export_gate_emission.py -- --output assets/models
Run after export_assets.py when regenerating geometry. No colour-threshold heuristics.
"""

import argparse, bpy, hashlib, json, math, struct, sys
from pathlib import Path
from mathutils import Vector

parser = argparse.ArgumentParser()
parser.add_argument("--output", required=True)
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :])
root = Path(args.output).resolve()
path = root / "stargate.json"
manifest = json.loads(path.read_text())
active = {"MI_Stargate_ChevronLightTop", "MI_Stargate_ChevronLight_Master"}
report = {
    "source_sha256": hashlib.sha256(Path(bpy.data.filepath).read_bytes()).hexdigest(),
    "method": "source triangle material assignments rasterized into existing runtime UVs",
    "parts": [],
}
for part in manifest["parts"]:
    if not any(name in active for name in part["materials"]):
        continue
    source = bpy.data.objects[part["name"]]
    mesh = source.data
    mesh.calc_loop_triangles()
    raw = (root / part["mesh"]).read_bytes()
    count = struct.unpack_from("<I", raw, 4)[0]
    assert count == len(mesh.loop_triangles) * 3, "Runtime/source topology mismatch"
    vertices = list(struct.iter_unpack("<8f", raw[8:]))
    size = 512
    mask = bytearray(size * size)
    metal = bytearray(size * size)
    for index, triangle in enumerate(mesh.loop_triangles):
        runtime = vertices[index * 3 : index * 3 + 3]
        for li, v in zip(triangle.loops, runtime):
            p = source.matrix_world @ mesh.vertices[
                mesh.loops[li].vertex_index
            ].co - Vector((-0.9, 0, 0))
            assert (
                Vector((p.x, p.z, -p.y)) - Vector(v[:3])
            ).length < 0.0001, "Runtime/source placement mismatch"
        emitting = (
            source.material_slots[triangle.material_index].material.name in active
        )
        a, b, c = [(v[6] * size, v[7] * size) for v in runtime]
        cross = lambda u, v: u[0] * v[1] - u[1] * v[0]
        ab = (b[0] - a[0], b[1] - a[1])
        ac = (c[0] - a[0], c[1] - a[1])
        area = cross(ab, ac)
        if abs(area) < 1e-8:
            continue
        for y in range(
            max(0, math.floor(min(a[1], b[1], c[1]))),
            min(size, math.ceil(max(a[1], b[1], c[1]))),
        ):
            for x in range(
                max(0, math.floor(min(a[0], b[0], c[0]))),
                min(size, math.ceil(max(a[0], b[0], c[0]))),
            ):
                ap = (x + 0.5 - a[0], y + 0.5 - a[1])
                u = cross(ap, ac) / area
                v = cross(ab, ap) / area
                if u >= 0 and v >= 0 and u + v <= 1:
                    (mask if emitting else metal)[y * size + x] = 1
    # Two-pixel padding avoids dark cracks, but never expands into metal polygons.
    for _ in range(2):
        padded = mask[:]
        for y in range(1, size - 1):
            for x in range(1, size - 1):
                i = y * size + x
                if mask[i]:
                    for j in (i - 1, i + 1, i - size, i + size):
                        if not metal[j]:
                            padded[j] = 1
        mask = padded
    pixels = [
        component
        for value in mask
        for component in (float(value), float(value), float(value), 1.0)
    ]
    image = bpy.data.images.new("EmissionMask", width=size, height=size, alpha=False)
    image.colorspace_settings.name = "Non-Color"
    image.pixels.foreach_set(pixels)
    name = Path(part["mesh"]).stem + "-emission.png"
    image.filepath_raw = str(root / name)
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)
    part["emissionTexture"] = name
    part["emissionColor"] = [1.0, 0.20, 0.025]
    part["emissionIntensity"] = 3.5
    if "Crystal" in part["name"]:
        part["lightPosition"] = [
            (min(v[i] for v in vertices) + max(v[i] for v in vertices)) * 0.5
            for i in range(3)
        ]
        part["lightPosition"][2] = max(v[2] for v in vertices) + 0.08
    report["parts"].append(
        {
            "mesh": part["mesh"],
            "mask": name,
            "mask_sha256": hashlib.sha256((root / name).read_bytes()).hexdigest(),
            "lit_pixels": sum(mask),
        }
    )
    print("EMISSION", part["name"], sum(mask), flush=True)
path.write_text(json.dumps(manifest, indent=2) + "\n")
(root / "gate-emission-provenance.json").write_text(json.dumps(report, indent=2) + "\n")
