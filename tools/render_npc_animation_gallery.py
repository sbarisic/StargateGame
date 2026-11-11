"""Render both clips from the four exported GLBs, without source blend files."""

import bpy, pathlib, json, math, sys
from mathutils import Vector

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts/npc-rigs/gallery"
OUT.mkdir(parents=True, exist_ok=True)
names = json.loads((ROOT / "assets_itch/npc/selection.json").read_text())["selected"]
fantasy_only = "--fantasy-only" in sys.argv
bpy.ops.wm.read_factory_settings(use_empty=True)
s = bpy.context.scene
s.render.fps = 30
actors = []
for i, name in enumerate(names):
    before = set(bpy.data.objects)
    old_actions = set(bpy.data.actions)
    bpy.ops.import_scene.gltf(
        filepath=str(
            ROOT / "assets_itch/npc" / name / "rigged" / (name + "_rigged.glb")
        )
    )
    imported = [o for o in bpy.data.objects if o not in before]
    rig = next(o for o in imported if o.type == "ARMATURE")
    for track in rig.animation_data.nla_tracks:
        track.mute = True
    actions = {
        key: next(
            a
            for a in bpy.data.actions
            if a not in old_actions and (a.name == key or a.name.startswith(key + "."))
        )
        for key in ["idle", "walk"]
    }
    root = bpy.data.objects.new(name + "_preview_position", None)
    bpy.context.collection.objects.link(root)
    for o in imported:
        if (
            o.parent is None
            and o.type in ["ARMATURE", "MESH"]
            and o.name != "Icosphere"
        ):
            o.parent = root
    root.location.x = (i - 1.5) * 1.25
    root.rotation_euler.z = -0.18
    actors.append((rig, actions))
s.render.engine = "CYCLES"
s.cycles.samples = 6
s.cycles.use_denoising = True
s.render.resolution_x = 1120
s.render.resolution_y = 540
s.render.resolution_percentage = 100
s.world = bpy.data.worlds.new("Studio")
s.world.use_nodes = True
s.world.node_tree.nodes["Background"].inputs[0].default_value = (0.46, 0.51, 0.60, 1)
s.world.node_tree.nodes["Background"].inputs[1].default_value = 0.45
s.view_settings.view_transform = "AgX"
bpy.ops.mesh.primitive_plane_add(size=200)
mat = bpy.data.materials.new("Floor")
mat.diffuse_color = (0.115, 0.135, 0.16, 1)
bpy.context.object.data.materials.append(mat)
for location, power, size in [
    ((-3, -4, 6), 950, 5),
    ((4, -1, 4), 650, 4),
    ((0, 3, 5), 1000, 4),
]:
    bpy.ops.object.light_add(type="AREA", location=location)
    o = bpy.context.object
    o.data.energy = power
    o.data.shape = "DISK"
    o.data.size = size
    o.rotation_euler = (
        (Vector((0, 0, 1)) - o.location).to_track_quat("-Z", "Y").to_euler()
    )
bpy.ops.object.camera_add(location=(0, -10, 2.65))
c = bpy.context.object
s.camera = c
c.rotation_euler = (
    (Vector((0, 0, 0.87)) - c.location).to_track_quat("-Z", "Y").to_euler()
)
c.data.type = "ORTHO"
c.data.ortho_scale = 5.3


def render(path, rear=False):
    if fantasy_only:
        s.render.use_border = True
        s.render.use_crop_to_border = True
        s.render.border_min_x = 0.73 if rear else 0
        s.render.border_max_x = 1 if rear else 0.27
        s.render.border_min_y = 0
        s.render.border_max_y = 1
        path = path.with_stem(path.stem + "-fantasy-update")
    s.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)


# Blender orthographic scale is the horizontal span for landscape images.
for clip in ["walk", "idle"]:
    for rig, actions in actors:
        a = actions[clip]
        rig.animation_data.action = a
        rig.animation_data.action_slot = a.slots[0]
    end = actors[0][1][clip].frame_range[1]
    for frame in range(24):
        f = end * frame / 24
        s.frame_set(int(f), subframe=f - int(f))
        render(OUT / f"{clip}-{frame:02}.png")
        print("FRAME", clip, frame, flush=True)
    # Two additional rear views reveal coat/shoulder deformation hidden in front.
    c.location = (0, 10, 2.65)
    c.rotation_euler = (
        (Vector((0, 0, 0.87)) - c.location).to_track_quat("-Z", "Y").to_euler()
    )
    for i, t in enumerate([0, 0.5]):
        f = end * t
        s.frame_set(int(f), subframe=f - int(f))
        render(OUT / f"{clip}-rear-{i}.png", rear=True)
    c.location = (0, -10, 2.65)
    c.rotation_euler = (
        (Vector((0, 0, 0.87)) - c.location).to_track_quat("-Z", "Y").to_euler()
    )
