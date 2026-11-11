"""Render exported NPC skins/clips; Blender --python ... -- name [clip]."""

import bpy, sys, pathlib, math
from mathutils import Vector

ROOT = pathlib.Path(__file__).resolve().parents[1]
ARGS = (
    sys.argv[sys.argv.index("--") + 1 :]
    if "--" in sys.argv
    else ["turkish_old_villager"]
)
name = ARGS[0]
clip = ARGS[1] if len(ARGS) > 1 else "walk"
out = ROOT / "artifacts/npc-rigs" / name
out.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(
    filepath=str(ROOT / "assets_itch/npc" / name / "rigged" / (name + "_rigged.glb"))
)
rig = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
print("ACTIONS", [(a.name, list(a.frame_range)) for a in bpy.data.actions], flush=True)
for track in rig.animation_data.nla_tracks:
    track.mute = True
a = next(
    a
    for a in bpy.data.actions
    if a.name == clip or a.name.startswith(clip + "_") or a.name.endswith("_" + clip)
)
rig.animation_data.action = a
if a.slots:
    rig.animation_data.action_slot = a.slots[0]
s = bpy.context.scene
s.render.engine = "CYCLES"
s.cycles.samples = 16
s.cycles.use_denoising = True
s.render.resolution_x = 640
s.render.resolution_y = 800
s.render.resolution_percentage = 100
s.world = bpy.data.worlds.new("Studio")
s.world.use_nodes = True
s.world.node_tree.nodes["Background"].inputs[0].default_value = (0.46, 0.51, 0.60, 1)
s.world.node_tree.nodes["Background"].inputs[1].default_value = 0.45
s.view_settings.view_transform = "AgX"
s.render.film_transparent = False
bpy.ops.mesh.primitive_plane_add(size=200)
mat = bpy.data.materials.new("Floor")
mat.diffuse_color = (0.115, 0.135, 0.16, 1)
bpy.context.object.data.materials.append(mat)
for loc, power, size in [
    ((-3, -4, 5), 550, 4),
    ((4, -1, 3), 350, 3),
    ((0, 3, 4), 650, 3),
]:
    bpy.ops.object.light_add(type="AREA", location=loc)
    o = bpy.context.object
    o.data.energy = power
    o.data.shape = "DISK"
    o.data.size = size
    o.rotation_euler = (
        (Vector((0, 0, 1)) - o.location).to_track_quat("-Z", "Y").to_euler()
    )
bpy.ops.object.camera_add(location=(2.4, -7, 2.35))
c = bpy.context.object
s.camera = c
c.rotation_euler = (
    (Vector((0, 0, 0.9)) - c.location).to_track_quat("-Z", "Y").to_euler()
)
c.data.type = "ORTHO"
c.data.ortho_scale = 2.12
if "--animate" in ARGS:
    s.render.engine = "CYCLES"
    s.cycles.samples = 8
    s.render.resolution_x = 400
    s.render.resolution_y = 500
    fractions = [i / 24 for i in range(24)]
else:
    fractions = [0, 0.25, 0.5, 0.75]
for i, t in enumerate(fractions):
    f = a.frame_range[0] + t * (a.frame_range[1] - a.frame_range[0])
    s.frame_set(int(f), subframe=f - int(f))
    s.render.filepath = str(out / f"{clip}-{i:02}.png")
    bpy.ops.render.render(write_still=True)
