"""Render fixtures exported by --furnishing-fixtures. Explicit offline review only.

blender -b --python tools/render_furnished_interiors.py
Uses the runtime meshes and exact C# placement output, not a second layout solver.
"""

import bpy, json, math, struct, sys
from pathlib import Path
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts/furnishings"
MODELS = ROOT / "assets/models"


def material(part):
    name = part.get("texture", "plain")
    if name in bpy.data.materials:
        return bpy.data.materials[name]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    for field, input_name in [
        ("texture", "Base Color"),
        ("normal", "Normal"),
        ("metallicRoughness", None),
    ]:
        if field not in part:
            continue
        image = bpy.data.images.load(str(MODELS / part[field]), check_existing=True)
        if field != "texture":
            image.colorspace_settings.name = "Non-Color"
        node = m.node_tree.nodes.new("ShaderNodeTexImage")
        node.image = image
        if field == "normal":
            # Runtime normals are DirectX-style; undo the importer green flip.
            separate = m.node_tree.nodes.new("ShaderNodeSeparateXYZ")
            combine = m.node_tree.nodes.new("ShaderNodeCombineXYZ")
            inv = m.node_tree.nodes.new("ShaderNodeMath")
            inv.operation = "SUBTRACT"
            inv.inputs[0].default_value = 1
            m.node_tree.links.new(node.outputs["Color"], separate.inputs[0])
            m.node_tree.links.new(separate.outputs["Y"], inv.inputs[1])
            for axis in ["X", "Z"]:
                m.node_tree.links.new(separate.outputs[axis], combine.inputs[axis])
            m.node_tree.links.new(inv.outputs[0], combine.inputs["Y"])
            normal = m.node_tree.nodes.new("ShaderNodeNormalMap")
            m.node_tree.links.new(combine.outputs[0], normal.inputs["Color"])
            m.node_tree.links.new(normal.outputs[0], bsdf.inputs["Normal"])
        elif field == "metallicRoughness":
            separate = m.node_tree.nodes.new("ShaderNodeSeparateColor")
            m.node_tree.links.new(node.outputs["Color"], separate.inputs[0])
            m.node_tree.links.new(separate.outputs["Green"], bsdf.inputs["Roughness"])
            m.node_tree.links.new(separate.outputs["Blue"], bsdf.inputs["Metallic"])
        else:
            m.node_tree.links.new(node.outputs["Color"], bsdf.inputs[input_name])
    return m


def model(name, position=(0, 0, 0), rotation=0, cut_height=None):
    document = json.loads((MODELS / (name + ".json")).read_text())
    objects = []
    for index, part in enumerate(document["parts"]):
        data = (MODELS / part["mesh"]).read_bytes()
        if data[:4] != b"SGM1":
            raise ValueError("Unsupported runtime mesh")
        count = struct.unpack_from("<I", data, 4)[0]
        verts = list(struct.iter_unpack("<8f", data[8:]))
        assert len(verts) == count
        triangles = [verts[i : i + 3] for i in range(0, len(verts), 3)]
        if cut_height is not None:
            triangles = [t for t in triangles if max(v[1] for v in t) < cut_height]
        verts = [v for t in triangles for v in t]
        if not verts:
            continue
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata(
            [(v[0], -v[2], v[1]) for v in verts],
            [],
            [(i, i + 1, i + 2) for i in range(0, len(verts), 3)],
        )
        mesh.update()
        uv = mesh.uv_layers.new(name="UVMap")
        # Runtime UVs already use Blender/OpenGL's bottom-left texture origin.
        for loop in mesh.loops:
            uv.data[loop.index].uv = (
                verts[loop.vertex_index][6],
                verts[loop.vertex_index][7],
            )
        mesh.normals_split_custom_set_from_vertices(
            [(v[3], -v[5], v[4]) for v in verts]
        )
        obj = bpy.data.objects.new(name + "-" + str(index), mesh)
        bpy.context.collection.objects.link(obj)
        mesh.materials.append(material(part))
        obj.location = (position[0], -position[2], position[1])
        obj.rotation_euler.z = -rotation * math.pi / 6
        objects.append(obj)
    return objects


def camera(s, position, target, ortho=None):
    d = bpy.data.cameras.new("Camera")
    o = bpy.data.objects.new("Camera", d)
    s.collection.objects.link(o)
    o.location = position
    o.rotation_euler = (Vector(target) - o.location).to_track_quat("-Z", "Y").to_euler()
    d.clip_start = 0.05
    if ortho:
        d.type = "ORTHO"
        d.ortho_scale = ortho
    else:
        d.lens = 18
    s.camera = o


def main():
    rooms = json.loads((OUT / "fixtures.json").read_text())
    if "--roles" in sys.argv:
        roles = sys.argv[sys.argv.index("--roles") + 1].split(",")
        rooms = [r for r in rooms if r["role"] in roles]
    OUT.mkdir(exist_ok=True)
    for room in rooms:
        for view in ["overview", "interior"]:
            bpy.ops.wm.read_factory_settings(use_empty=True)
            s = bpy.context.scene
            s.render.engine = "CYCLES"
            s.cycles.samples = 32
            s.cycles.use_denoising = True
            s.render.resolution_x = 1400
            s.render.resolution_y = 1050
            s.render.resolution_percentage = 100
            s.world = bpy.data.worlds.new("World")
            s.world.use_nodes = True
            s.world.node_tree.nodes["Background"].inputs[1].default_value = 0.5
            f = room["Floor"]
            span = max(f[2] - f[0], f[3] - f[1])
            outdoor = room["Outdoor"]
            geometry = model(
                room["template"],
                cut_height=0.8 if view == "overview" else 3.01 if not outdoor else None,
            )
            for p in room["pieces"]:
                pos = p["Position"]
                geometry += model(
                    p["Asset"], (pos["X"], pos["Y"], pos["Z"]), p["Rotation"]
                )
            bpy.ops.mesh.primitive_plane_add(size=200, location=(0, 0, -0.03))
            floor = bpy.context.object
            m = bpy.data.materials.new("Ground")
            m.diffuse_color = (0.35, 0.31, 0.25, 1)
            floor.data.materials.append(m)
            for loc, power in [((0, 0, 6), 1800), ((2, -2, 4), 800)]:
                d = bpy.data.lights.new("Studio light", "AREA")
                d.energy = power
                d.shape = "DISK"
                d.size = 5
                o = bpy.data.objects.new("Studio light", d)
                s.collection.objects.link(o)
                o.location = loc
            if view == "overview":
                camera(s, (span * 0.5, span * 0.7, span), (0, 0, 0), span * 1.6)
                bpy.context.view_layer.update()
                basis = s.camera.rotation_euler.to_matrix()
                inv = basis.transposed()
                points = [
                    inv @ (o.matrix_world @ Vector(c))
                    for o in geometry
                    for c in o.bound_box
                ]
                lo = Vector(tuple(min(p[i] for p in points) for i in range(3)))
                hi = Vector(tuple(max(p[i] for p in points) for i in range(3)))
                s.camera.data.ortho_scale = (
                    max(
                        hi.x - lo.x,
                        (hi.y - lo.y) * s.render.resolution_x / s.render.resolution_y,
                    )
                    * 1.12
                )
                shift = basis @ Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, 0))
                s.camera.location += shift
            else:
                camera(s, (0, -f[1] - 0.2, 1.65), (0, -0.5, 1.0))
            name = room["template"] + "-" + room["role"] + "-" + view
            s.render.filepath = str(OUT / (name + ".png"))
            bpy.ops.render.render(write_still=True)
            print("RENDERED", name, flush=True)


if __name__ == "__main__":
    main()
