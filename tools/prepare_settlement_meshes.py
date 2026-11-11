"""Run with Blender --background --python ...; retain source transforms and UVs."""

from pathlib import Path
import bpy, hashlib, json, math

root = Path(__file__).resolve().parents[1]
source = root / "assets_itch/Seahill_Towns_Complete/Models/GLB"
output = root / "artifacts/settlement-meshes"
output.mkdir(parents=True, exist_ok=True)
names = [
    "wall_out_timber",
    "wall_out_timber_window",
    "wall_out_timber_doorway",
    "wall_out_stone",
    "floor_in_planks_dark",
    "roof_clay",
    "roof_slate",
    "post_timber",
    "attic_gable_timber",
    "roof_ridge_clay",
    "roof_ridge_slate",
    "door_round",
]
records = []
for name in names:
    path = source / (name + ".glb")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(path))
    before = sum(
        len(o.data.polygons) for o in bpy.context.scene.objects if o.type == "MESH"
    )
    for obj in list(bpy.context.scene.objects):
        if obj.type != "MESH":
            continue
        bpy.context.view_layer.objects.active = obj
        # Source bevels are highly tessellated. Dissolve redundant coplanar
        # edges while preserving material/UV boundaries and narrow frames.
        modifier = obj.modifiers.new("Planar cleanup", "DECIMATE")
        modifier.decimate_type = "DISSOLVE"
        modifier.angle_limit = math.radians(1)
        modifier.delimit = {"MATERIAL", "UV", "SEAM"}
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        # Keep authored frames and glass boundaries intact. Planar dissolve
        # removes redundant tessellation without collapsing narrow window bars.
    after = sum(
        len(o.data.polygons) for o in bpy.context.scene.objects if o.type == "MESH"
    )
    bpy.ops.export_scene.gltf(
        filepath=str(output / (name + ".glb")),
        export_format="GLB",
        export_animations=False,
        export_cameras=False,
        export_lights=False,
    )
    records.append(
        dict(
            name=name,
            source_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
            before=before,
            after=after,
        )
    )
(output / "report.json").write_text(
    json.dumps(dict(blender=bpy.app.version_string, meshes=records), indent=2)
)
