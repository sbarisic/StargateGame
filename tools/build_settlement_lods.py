"""Blender background pass over imported assemblies; preserves material UV seams."""

import bpy, bmesh, json, struct, math, sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
models = root / "assets/models"
records = []
for template in json.loads((root / "assets/settlements/templates.json").read_text()):
    name = template["id"]
    if "--houses-only" in sys.argv and not name.startswith(("settlement-mega-house-", "settlement-seahill-house-")): continue
    manifest = json.loads((models / (name + ".json")).read_text())
    entries = []
    before = after = 0
    for index, part in enumerate(manifest["parts"]):
        packed = list(
            struct.iter_unpack("<8f", (models / part["mesh"]).read_bytes()[8:])
        )
        before += len(packed) // 3
        bpy.ops.wm.read_factory_settings(use_empty=True)
        mesh = bpy.data.meshes.new("assembly")
        mesh.from_pydata(
            [v[:3] for v in packed],
            [],
            [(i, i + 1, i + 2) for i in range(0, len(packed), 3)],
        )
        mesh.update()
        uv = mesh.uv_layers.new()
        for loop in mesh.loops:
            uv.data[loop.index].uv = packed[loop.vertex_index][6:8]
        # Weld positions while preserving UV data on face corners.
        bm = bmesh.new()
        bm.from_mesh(mesh)
        bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=0.00001)
        bm.to_mesh(mesh)
        bm.free()
        obj = bpy.data.objects.new("assembly", mesh)
        bpy.context.collection.objects.link(obj)
        bpy.context.view_layer.objects.active = obj
        modifier = obj.modifiers.new("Coplanar faces", "DECIMATE")
        modifier.decimate_type = "DISSOLVE"
        modifier.angle_limit = math.radians(2)
        modifier.delimit = {"MATERIAL", "UV", "SEAM"}
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        modifier = obj.modifiers.new("Distant silhouette", "DECIMATE")
        modifier.ratio = 0.25
        modifier.use_collapse_triangulate = True
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        mesh = obj.data
        mesh.calc_loop_triangles()
        uv = mesh.uv_layers.active
        vertices = []
        for tri in mesh.loop_triangles:
            for li in tri.loops:
                p = mesh.vertices[mesh.loops[li].vertex_index].co
                n = mesh.corner_normals[li].vector
                t = uv.data[li].uv
                vertices.append((*p, *n, *t))
        after += len(vertices) // 3
        filename = f"{name}-far-{index}.mesh"
        with (models / filename).open("wb") as f:
            f.write(b"SGM1" + struct.pack("<I", len(vertices)))
            for v in vertices:
                f.write(struct.pack("<8f", *v))
        entries.append({**part, "mesh": filename})
    (models / (name + "-far.json")).write_text(
        json.dumps(dict(version=1, parts=entries), indent=2) + "\n"
    )
    records.append(dict(model=name, nearTriangles=before, farTriangles=after))
    print(name, before, "->", after, flush=True)
if "--houses-only" in sys.argv:
    previous = json.loads((root / "assets/settlements/lods.json").read_text())
    updated = {r['model']: r for r in records}
    records = [updated.pop(r['model'], r) for r in previous['models']] + list(updated.values())
(root / "assets/settlements/lods.json").write_text(
    json.dumps(
        dict(
            tool="tools/build_settlement_lods.py",
            blender=bpy.app.version_string,
            models=records,
        ),
        indent=2,
    )
    + "\n"
)
