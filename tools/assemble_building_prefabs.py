"""Compose ready-to-place kit pieces from imported runtime assets.
Run normally after import_building_kit.py; use Blender -b --python this_file
to also render missing selectable-catalog thumbnails. Original kit files stay intact.
"""

import json, math, struct, hashlib
import sys
from pathlib import Path
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from build_wall_caps import generate_caps
from build_door_collision import generate_doors
from fit_building_openings import fit_openings
from build_stair_reference import generate_reference
MODELS = ROOT / "assets/models"
BUILD = ROOT / "assets/building"
recipes = {
    "kit-assembled-window": (
        "Wall with window",
        "kit-wall-unevenbrick-window-wide-flat",
        [
            ("kit-wall-unevenbrick-window-wide-flat", (0, 0, 0), 0),
            ("kit-window-wide-flat1", (0, 0, 0), 0),
        ],
        [],
    ),
    "kit-assembled-door": (
        "Wall with door",
        "kit-wall-unevenbrick-door-flat",
        [
            ("kit-wall-unevenbrick-door-flat", (0, 0, 0), 0),
            ("kit-doorframe-flat-brick", (0, 0, 0), 0),
        ],
        [dict(prefab="kit-door-1-flat", offset=[-0.53, 0, 0], rotation=0)],
    ),
    "kit-assembled-roof": (
        "Finished roof 6 x 6 m",
        "kit-roof-roundtiles-6x6",
        [
            ("kit-roof-roundtiles-6x6", (0, 0, 0), 0),
            ("kit-roof-front-brick6", (0, 0, -3), 0),
            ("kit-roof-front-brick6", (0, 0, 3), 6),
        ],
        [],
    ),
}
hidden = {
    "kit-stair-interior-simple", "kit-stairs-exterior-platform",
    "fort-swing-gate", "fort-sliding-gate", "fort-bollard", "fort-gate-control",
    "kit-wall-unevenbrick-window-wide-flat",
    "kit-window-wide-flat1",
    "kit-wall-unevenbrick-door-flat",
    "kit-doorframe-flat-brick",
    "kit-door-1-flat",
    "kit-corner-exterior-brick",
    "kit-roof-roundtiles-6x6",
    "kit-roof-front-brick6",
    "kit-roof-modular-roundtiles",
}
catalog = [
    p
    for p in json.loads((BUILD / "prefabs.json").read_text())
    if p["id"] not in recipes
]
lookup = {p["id"]: p for p in catalog}
fit_openings(catalog, MODELS)
sources = {}


def read(path):
    b = path.read_bytes()
    sources[str(path.relative_to(ROOT))] = hashlib.sha256(b).hexdigest()
    return b


def rotation(step):
    a = -step * math.pi / 6
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


for key, (title, base, components, children) in recipes.items():
    parts = []
    triangles = []
    for model, offset, angle in components:
        r = rotation(angle)
        for part in json.loads(read(MODELS / (model + ".json")))["parts"]:
            b = read(MODELS / part["mesh"])
            v = np.frombuffer(b[8:], dtype="<f4").reshape(-1, 8).copy()
            v[:, :3] = v[:, :3] @ r.T + offset
            v[:, 3:6] = v[:, 3:6] @ r.T
            mesh = f"{key}-{len(parts)}.mesh"
            (MODELS / mesh).write_bytes(
                b"SGM1" + struct.pack("<I", len(v)) + v.astype("<f4").tobytes()
            )
            parts.append({**part, "mesh": mesh})
        triangles.extend((np.array(lookup[model]["triangles"]) @ r.T + offset).tolist())
    (MODELS / (key + ".json")).write_text(
        json.dumps(dict(version=1, parts=parts), indent=2) + "\n"
    )
    points = np.array(triangles).reshape(-1, 3)
    definition = {
        **lookup[base],
        "id": key,
        "title": title,
        "triangles": triangles,
        "min": points.min(0).tolist(),
        "max": points.max(0).tolist(),
        "children": children,
        "selectable": True,
    }
    catalog.insert(catalog.index(lookup[base]) + 1, definition)
for p in catalog:
    if p["id"] in hidden:
        p["selectable"] = False
catalog = generate_doors(generate_reference(generate_caps(catalog, MODELS), MODELS), MODELS)
(BUILD / "prefabs.json").write_text(json.dumps(catalog, separators=(",", ":")) + "\n")
(BUILD / "assemblies.json").write_text(
    json.dumps(
        dict(
            version=1,
            tool="tools/assemble_building_prefabs.py",
            recipes=recipes,
            sources=sources,
        ),
        indent=2,
    )
    + "\n"
)
print(
    "Assembled 3 building prefabs; moving door remains a separate owned child.",
    flush=True,
)

try:
    from import_masonry_materials import install as install_masonry
except ModuleNotFoundError as error:
    if error.name != "PIL":
        raise
    # Blender does not bundle Pillow; use the asset pipeline's system Python.
    import subprocess, shutil
    subprocess.run([shutil.which("python"), str(ROOT / "tools/import_masonry_materials.py")], check=True)
else:
    install_masonry()

try:
    import bpy
except ImportError:
    raise SystemExit(0)
from mathutils import Vector

if "--spawn-icons" in sys.argv:
    import runpy
    runpy.run_path(str(ROOT / "tools/render_lantern_thumbnail.py"), run_name="__main__")

for prefab in ([dict(id="wooden-crate"), dict(id="barrel-red")] if "--spawn-icons" in sys.argv else catalog):
    if not prefab.get("selectable", True):
        continue
    key = prefab["id"]
    if "--refresh-icons" not in sys.argv and (ROOT / "assets/gui/building" / (key + ".png")).exists():
        continue
    children = prefab.get("children") or []
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    all_points = []
    manifests = [(key, (0, 0, 0), 0)] + [
        (c["prefab"], c["offset"], c.get("rotation", 0)) for c in children
    ]
    for model, offset, angle in manifests:
        for part in json.loads((MODELS / (model + ".json")).read_text())["parts"]:
            v = (
                np.frombuffer((MODELS / part["mesh"]).read_bytes()[8:], dtype="<f4")
                .reshape(-1, 8)
                .copy()
            )
            v[:, :3] = v[:, :3] @ rotation(angle).T + offset
            points = [(float(p[0]), float(-p[2]), float(p[1])) for p in v]
            all_points.extend(points)
            mesh = bpy.data.meshes.new("part")
            mesh.from_pydata(
                points, [], [(i, i + 1, i + 2) for i in range(0, len(v), 3)]
            )
            mesh.update()
            uv = mesh.uv_layers.new()
            for loop in mesh.loops:
                uv.data[loop.index].uv = v[loop.vertex_index, 6:8]
            obj = bpy.data.objects.new("part", mesh)
            scene.collection.objects.link(obj)
            material = bpy.data.materials.new("imported")
            material.use_nodes = True
            bs = material.node_tree.nodes.get("Principled BSDF")
            bs.inputs["Roughness"].default_value = 0.8
            tex = material.node_tree.nodes.new("ShaderNodeTexImage")
            tex.image = bpy.data.images.load(
                str(MODELS / part["texture"]), check_existing=True
            )
            material.node_tree.links.new(tex.outputs["Color"], bs.inputs["Base Color"])
            nodes, links = material.node_tree.nodes, material.node_tree.links
            if part.get("metallicRoughness"):
                mr = nodes.new("ShaderNodeTexImage")
                mr.image = bpy.data.images.load(str(MODELS / part["metallicRoughness"]), check_existing=True)
                mr.image.colorspace_settings.name = "Non-Color"
                channels = nodes.new("ShaderNodeSeparateColor")
                links.new(mr.outputs["Color"], channels.inputs["Color"])
                links.new(channels.outputs["Green"], bs.inputs["Roughness"])
                links.new(channels.outputs["Blue"], bs.inputs["Metallic"])
            if part.get("normal"):
                normal = nodes.new("ShaderNodeTexImage")
                normal.image = bpy.data.images.load(str(MODELS / part["normal"]), check_existing=True)
                normal.image.colorspace_settings.name = "Non-Color"
                flip = nodes.new("ShaderNodeVectorMath"); flip.operation = "MULTIPLY"
                flip.inputs[1].default_value = (1, -1, 1)
                normal_offset = nodes.new("ShaderNodeVectorMath"); normal_offset.operation = "ADD"
                normal_offset.inputs[1].default_value = (0, 1, 0)
                links.new(normal.outputs["Color"], flip.inputs[0])
                links.new(flip.outputs["Vector"], normal_offset.inputs[0])
                mapped = nodes.new("ShaderNodeNormalMap")
                links.new(normal_offset.outputs["Vector"], mapped.inputs["Color"])
                links.new(mapped.outputs["Normal"], bs.inputs["Normal"])
            mesh.materials.append(material)
    lo = Vector(tuple(min(p[i] for p in all_points) for i in range(3)))
    hi = Vector(tuple(max(p[i] for p in all_points) for i in range(3)))
    centre = (lo + hi) * 0.5
    size = max(hi - lo)
    bpy.ops.object.camera_add(location=centre + Vector((1.1, 1.8, 1.1)) * size)
    camera = bpy.context.object
    camera.rotation_euler = (
        (centre - camera.location).to_track_quat("-Z", "Y").to_euler()
    )
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = size * 1.48
    scene.camera = camera
    bpy.ops.object.light_add(type="AREA", location=centre + Vector((1, 2, 3)) * size)
    bpy.context.object.data.energy = 150 * size * size
    bpy.context.object.data.size = size * 2
    scene.world = bpy.data.worlds.new("World")
    scene.world.use_nodes = True
    scene.world.node_tree.nodes["Background"].inputs[1].default_value = 0.6
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 24
    scene.cycles.use_denoising = True
    scene.render.resolution_x = 256
    scene.render.resolution_y = 192
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.view_settings.view_transform = "AgX"
    scene.render.filepath = str(ROOT / "assets/gui/building" / (key + ".png"))
    bpy.ops.render.render(write_still=True)
