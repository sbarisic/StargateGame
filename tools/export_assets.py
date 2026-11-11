"""Blender offline exporter. Never saves or modifies the source blend.
blender --background <source.blend> --python tools/export_assets.py -- --output assets/models
"""

import bpy, sys, argparse, json, struct, hashlib
from pathlib import Path
from mathutils import Matrix, Vector

parser = argparse.ArgumentParser()
parser.add_argument("--output", required=True)
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :])
out = Path(args.output).resolve()
out.mkdir(parents=True, exist_ok=True)
scene = bpy.context.scene
scene.render.engine = "CYCLES"
scene.cycles.samples = 1
scene.cycles.device = "CPU"
scene.render.bake.margin = 8
scene.render.bake.use_clear = True
scene.view_settings.view_transform = "Standard"

# Preserve original object/UV coordinates when baking layered source shaders.
# Each mesh is baked separately to its own atlas. Runtime parts share that atlas.
report = {}
for key, prefix, origin in [
    ("stargate", "STARGATE", Vector((-0.9, 0, 0))),
    ("dhd", "DHD", Vector((3, -2.5, 0))),
]:
    col = next(c for c in bpy.data.collections if c.name.startswith(prefix))
    objects = sorted([o for o in col.objects if o.type == "MESH"], key=lambda o: o.name)
    parts, cache = [], {}
    bounds = []
    for index, source in enumerate(objects):
        # Repeated chevron/key meshes can reuse a baked atlas when material and geometry match.
        signature = (
            source.data.name,
            tuple(s.material.name for s in source.material_slots),
        )
        obj = source.copy()
        obj.data = source.data.copy()
        scene.collection.objects.link(obj)
        # glTF splits vertices at UV/normal seams. Weld only the working bake mesh
        # so smart-project can form coherent islands instead of packing triangles.
        # Keep the original triangle corners/normals for the runtime mesh.
        original_mesh = source.data
        original_mesh.calc_loop_triangles()
        original_corners = [
            (
                original_mesh.vertices[loop.vertex_index].co.copy(),
                original_mesh.corner_normals[i].vector.copy(),
            )
            for i, loop in enumerate(original_mesh.loops)
        ]
        original_triangles = [tuple(tri.loops) for tri in original_mesh.loop_triangles]
        source_loops = obj.data.attributes.new(
            name="SourceLoop", type="INT", domain="CORNER"
        )
        source_loops.data.foreach_set("value", list(range(len(obj.data.loops))))
        original_uv = (
            obj.data.uv_layers.active.name if obj.data.uv_layers.active else ""
        )
        for slot in obj.material_slots:
            slot.material = slot.material.copy()
            mat = slot.material
            nodes, links = mat.node_tree.nodes, mat.node_tree.links
            # Explicitly bind implicit texture coordinates before introducing bake UVs.
            uvnode = nodes.new("ShaderNodeUVMap")
            uvnode.uv_map = original_uv
            for node in list(nodes):
                if node.type == "TEX_COORD":
                    for link in list(node.outputs["UV"].links):
                        links.new(uvnode.outputs["UV"], link.to_socket)
                if node.type == "TEX_IMAGE" and not node.inputs["Vector"].is_linked:
                    links.new(uvnode.outputs["UV"], node.inputs["Vector"])
            bs = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
            output = next(n for n in nodes if n.type == "OUTPUT_MATERIAL")
            emission = nodes.new("ShaderNodeEmission")
            color = bs.inputs["Base Color"]
            if color.is_linked:
                links.new(color.links[0].from_socket, emission.inputs["Color"])
            else:
                emission.inputs["Color"].default_value = color.default_value
            links.new(emission.outputs[0], output.inputs["Surface"])
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bake_uv = obj.data.uv_layers.new(name="RuntimeUV")
        obj.data.uv_layers.active = bake_uv
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.remove_doubles(threshold=0.000001)
        bpy.ops.uv.smart_project(angle_limit=1.1519, island_margin=0.002)
        bpy.ops.object.mode_set(mode="OBJECT")
        bake_uv = obj.data.uv_layers["RuntimeUV"]
        bake_uv.active_render = True
        # The two full gate rings need more texels than their smaller components.
        size = (
            4096
            if key == "stargate"
            and any(n in source.name for n in ("GlyphTrack", "Segment"))
            else 2048 if len(obj.data.polygons) > 3000 else 512
        )
        texname = f"{key}_{index:02}.png"
        if signature in cache:
            texname = cache[signature]
        else:
            image = bpy.data.images.new(
                f"bake_{key}_{index}", width=size, height=size, alpha=False
            )
            for slot in obj.material_slots:
                nodes = slot.material.node_tree.nodes
                node = nodes.new("ShaderNodeTexImage")
                node.image = image
                nodes.active = node
            bpy.ops.object.bake(type="EMIT")
            image.filepath_raw = str(out / texname)
            image.file_format = "PNG"
            image.save()
            cache[signature] = texname
            bpy.data.images.remove(image)
        mesh = obj.data
        bake_uv = mesh.uv_layers["RuntimeUV"]
        mapped_uv = {
            mesh.attributes["SourceLoop"].data[i].value: bake_uv.data[i].uv.copy()
            for i in range(len(mesh.loops))
        }
        if len(mapped_uv) != len(original_corners):
            raise RuntimeError(f"UV unwrap lost source corners on {source.name}")
        transform = obj.matrix_world
        normal_matrix = transform.to_3x3().inverted().transposed()
        filename = f"{key}_{index:02}.mesh"
        vertices = []
        for tri in original_triangles:
            for li in tri:
                coordinate, normal = original_corners[li]
                p = transform @ coordinate - origin
                n = (normal_matrix @ normal).normalized()
                uv = mapped_uv[li]
                # Blender Z-up -> right-handed Y-up, facing +Z.
                vertices.append((p.x, p.z, -p.y, n.x, n.z, -n.y, uv.x, uv.y))
                bounds.append((p.x, p.z, -p.y))
        with (out / filename).open("wb") as f:
            f.write(b"SGM1")
            f.write(struct.pack("<I", len(vertices)))
            for vertex in vertices:
                f.write(struct.pack("<8f", *vertex))
        parts.append(
            {
                "name": source.name,
                "mesh": filename,
                "texture": texname,
                "materials": [s.material.name for s in source.material_slots],
                "vertices": len(vertices),
            }
        )
        bpy.data.objects.remove(obj, do_unlink=True)
        print(
            f"EXPORTED {key} {index+1}/{len(objects)} {len(vertices)} vertices",
            flush=True,
        )
    (out / f"{key}.json").write_text(
        json.dumps({"version": 1, "parts": parts}, indent=2)
    )
    report[key] = {
        "parts": len(parts),
        "min": [min(p[i] for p in bounds) for i in range(3)],
        "max": [max(p[i] for p in bounds) for i in range(3)],
    }
report["source_sha256"] = hashlib.sha256(
    Path(bpy.data.filepath).read_bytes()
).hexdigest()
(out / "export-report.json").write_text(json.dumps(report, indent=2))
print(json.dumps(report), flush=True)

# Emission masks share the just-exported UVs and preserve source material assignments.
import runpy

runpy.run_path(
    str(Path(__file__).with_name("export_gate_emission.py")), run_name="__main__"
)

runpy.run_path(
    str(Path(__file__).with_name("export_dhd_materials.py")), run_name="__main__"
)
