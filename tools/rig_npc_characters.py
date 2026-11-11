"""Reproducible NPC rigs and two looping clips. Run in Blender background mode.

Sources are read-only. Joint landmarks are in a 1.8m, Z-up, -Y-facing frame.
Weights come from a closed voxel proxy; production geometry/UVs are retained.
"""

import bpy, bmesh, json, math, pathlib, shutil, sys, hashlib
import numpy as np
from mathutils import Vector, Matrix
from mathutils.kdtree import KDTree

ROOT = pathlib.Path(__file__).resolve().parents[1]
NPC = ROOT / "assets_itch/npc"
LIB = ROOT / "assets_itch/Universal Animation Library[Standard]"
ART = ROOT / "artifacts/npc-rigs"
ART.mkdir(parents=True, exist_ok=True)
CONFIG = {
    "fantasy_villager": dict(
        center=0.025,
        hip=0.91,
        knee=0.45,
        ankle=0.105,
        leg=0.12,
        shoulder=1.40,
        neck=1.50,
        head=1.60,
        left=[
            (0.18, 0.025, 1.40),
            (0.275, -0.005, 1.15),
            (0.235, -0.09, 0.89),
            (0.20, -0.11, 0.76),
        ],
        right=[
            (-0.14, 0.12, 1.40),
            (-0.255, 0.18, 1.14),
            (-0.35, 0.02, 1.17),
            (-0.35, -0.045, 1.16),
        ],
    ),
    "medieval_peasant_warrior": dict(
        center=0,
        hip=0.91,
        knee=0.46,
        ankle=0.10,
        leg=0.13,
        shoulder=1.40,
        neck=1.50,
        head=1.60,
        left=[
            (0.18, 0.015, 1.40),
            (0.255, -0.005, 1.12),
            (0.26, -0.035, 0.91),
            (0.255, -0.06, 0.78),
        ],
        right=[
            (-0.18, 0.015, 1.40),
            (-0.255, -0.005, 1.12),
            (-0.26, -0.035, 0.91),
            (-0.255, -0.06, 0.78),
        ],
    ),
    "turkish_old_villager": dict(
        center=0,
        hip=0.86,
        knee=0.43,
        ankle=0.09,
        leg=0.16,
        shoulder=1.39,
        neck=1.50,
        head=1.61,
        left=[
            (0.21, 0.015, 1.39),
            (0.49, 0.005, 1.38),
            (0.74, -0.02, 1.38),
            (0.89, -0.025, 1.38),
        ],
        right=[
            (-0.21, 0.015, 1.39),
            (-0.49, 0.005, 1.38),
            (-0.74, -0.02, 1.38),
            (-0.89, -0.025, 1.38),
        ],
    ),
    "turkish_old_villager2": dict(
        center=0,
        hip=0.89,
        knee=0.45,
        ankle=0.10,
        leg=0.095,
        shoulder=1.43,
        neck=1.53,
        head=1.62,
        left=[
            (0.21, 0.015, 1.43),
            (0.53, 0.015, 1.43),
            (0.81, -0.015, 1.43),
            (0.98, -0.025, 1.41),
        ],
        right=[
            (-0.21, 0.015, 1.43),
            (-0.53, 0.015, 1.43),
            (-0.81, -0.015, 1.43),
            (-0.98, -0.025, 1.41),
        ],
    ),
}


def log(*v):
    print(*v, flush=True)


def activate(obj):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def tri_count(obj):
    obj.data.calc_loop_triangles()
    return len(obj.data.loop_triangles)


def action(r, a):
    r.animation_data_create()
    r.animation_data.action = a
    if a and len(a.slots):
        r.animation_data.action_slot = a.slots[0]


def connected_region(mesh, points, mask_function, seed):
    coords, inv = np.unique(np.round(points, 5), axis=0, return_inverse=True)
    edges = inv[np.array([e.vertices[:] for e in mesh.data.edges])]
    mask = mask_function(coords)
    parent = np.arange(len(coords))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for a, b in edges[mask[edges[:, 0]] & mask[edges[:, 1]]]:
        a = find(a)
        b = find(b)
        if a != b:
            parent[b] = a
    candidates = np.flatnonzero(mask)
    target = find(
        candidates[np.argmin(np.linalg.norm(coords[candidates] - seed, axis=1))]
    )
    selected = np.zeros(len(coords), dtype=bool)
    for i in candidates:
        selected[i] = find(i) == target
    return np.flatnonzero(selected[inv])


def prepare(name):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    source = (
        NPC
        / name
        / (
            "optimized_400k/fantasy_villager_400k.glb"
            if name == "fantasy_villager"
            else "scene.gltf"
        )
    )
    bpy.ops.import_scene.gltf(filepath=str(source))
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.join()
    mesh = bpy.context.object
    transform = mesh.matrix_world.copy()
    mesh.parent = None
    mesh.matrix_world = transform
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    points = np.array([v.co[:] for v in mesh.data.vertices])
    lo = points.min(0)
    hi = points.max(0)
    scale = 1.8 / (hi[2] - lo[2])
    offset = (lo + hi) * 0.5
    offset[2] = lo[2]
    points = (points - offset) * scale
    mesh.data.vertices.foreach_set("co", points.ravel())
    mesh.data.update()
    mesh.name = name
    for o in list(bpy.context.scene.objects):
        if o != mesh:
            bpy.data.objects.remove(o, do_unlink=True)
    return mesh, source, points


def skeleton(name):
    c = CONFIG[name]
    x = c["center"]
    hip = (x, 0.025, c["hip"])
    waist = (x, 0.025, 1.08)
    chest = (x, 0.015, 1.27)
    neck = (x, 0.015, c["neck"])
    head = (x, -0.005, c["head"])
    bones = [
        ("root", (0, 0, 0), (0, 0, 0.15), None),
        ("pelvis", hip, waist, "root"),
        ("spine_01", waist, chest, "pelvis"),
        ("spine_02", chest, (x, 0.015, 1.38), "spine_01"),
        ("spine_03", (x, 0.015, 1.38), neck, "spine_02"),
        ("neck_01", neck, head, "spine_03"),
        ("Head", head, (x, -0.005, 1.78), "neck_01"),
    ]
    for side, sgn, key in [("l", 1, "left"), ("r", -1, "right")]:
        sh, el, wr, hand = c[key]
        bones += [
            (
                "clavicle_" + side,
                (x + sgn * 0.03, 0.015, c["shoulder"]),
                sh,
                "spine_03",
            ),
            ("upperarm_" + side, sh, el, "clavicle_" + side),
            ("lowerarm_" + side, el, wr, "upperarm_" + side),
            ("hand_" + side, wr, hand, "lowerarm_" + side),
        ]
        legx = x + sgn * c["leg"]
        h = (x + sgn * 0.09, 0.025, c["hip"])
        k = (legx, -0.025, c["knee"])
        a = (legx, 0.025, c["ankle"])
        ball = (legx, -0.105, 0.035)
        bones += [
            ("thigh_" + side, h, k, "pelvis"),
            ("calf_" + side, k, a, "thigh_" + side),
            ("foot_" + side, a, ball, "calf_" + side),
            ("ball_" + side, ball, (legx, -0.19, 0.035), "foot_" + side),
        ]
    data = bpy.data.armatures.new(name + "_skeleton")
    rig = bpy.data.objects.new(name + "_rig", data)
    bpy.context.collection.objects.link(rig)
    activate(rig)
    bpy.ops.object.mode_set(mode="EDIT")
    for n, h, t, pa in bones:
        b = data.edit_bones.new(n)
        b.head = h
        b.tail = t
        if pa:
            b.parent = data.edit_bones[pa]
        b.use_deform = n != "root"
    bpy.ops.object.mode_set(mode="OBJECT")
    rig.show_in_front = True
    rig.data.display_type = "OCTAHEDRAL"
    return rig


def skin(mesh, rig, points, name):
    # Voxel remeshing closes source scan seams before bone-heat weighting.
    proxy = mesh.copy()
    proxy.data = mesh.data.copy()
    bpy.context.collection.objects.link(proxy)
    proxy.name = "weight_proxy"
    activate(proxy)
    remesh = proxy.modifiers.new("Closed skinning proxy", "REMESH")
    remesh.mode = "VOXEL"
    remesh.voxel_size = 0.012
    remesh.use_smooth_shade = True
    bpy.ops.object.modifier_apply(modifier=remesh.name)
    dec = proxy.modifiers.new("Proxy simplification", "DECIMATE")
    dec.ratio = min(1, 18000 / max(1, tri_count(proxy)))
    bpy.ops.object.modifier_apply(modifier=dec.name)
    activate(proxy)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")
    missing = sum(not v.groups for v in proxy.data.vertices)
    log("PROXY", name, len(proxy.data.vertices), "unweighted", missing)
    method = "bone heat on closed proxy"
    if missing:
        # Some scans have small disconnected surfaces that make Blender's heat
        # solver singular. Seed by bone-segment distance and diffuse on the proxy
        # surface instead; this never changes the production mesh or its UVs.
        method = "surface-smoothed bone distance on closed proxy"
        proxy.vertex_groups.clear()
        bs = [b for b in rig.data.bones if b.use_deform]
        pp = np.array([v.co[:] for v in proxy.data.vertices])
        dist = []
        for b in bs:
            a = np.array(b.head_local)
            v = np.array(b.tail_local) - a
            t = np.clip(((pp - a) @ v) / (v @ v), 0, 1)
            dist.append(np.linalg.norm(pp - a - t[:, None] * v, axis=1))
        dist = np.array(dist).T
        weights = np.exp(-(dist - dist.min(1)[:, None]) / 0.025)
        weights /= weights.sum(1)[:, None]
        edges = np.array([e.vertices[:] for e in proxy.data.edges])
        degree = np.bincount(edges.ravel(), minlength=len(pp)).clip(1)
        for _ in range(8):
            smooth = np.zeros_like(weights)
            np.add.at(smooth, edges[:, 0], weights[edges[:, 1]])
            np.add.at(smooth, edges[:, 1], weights[edges[:, 0]])
            weights = 0.7 * weights + 0.3 * smooth / degree[:, None]
        for j, b in enumerate(bs):
            group = proxy.vertex_groups.new(name=b.name)
            for i in np.flatnonzero(weights[:, j] > 0.002):
                group.add([int(i)], float(weights[i, j]), "REPLACE")
    # Transfer smooth weights by nearest polygon interpolation, retaining all UVs.
    activate(mesh)
    transfer = mesh.modifiers.new("Transfer heat weights", "DATA_TRANSFER")
    transfer.object = proxy
    transfer.use_vert_data = True
    transfer.data_types_verts = {"VGROUP_WEIGHTS"}
    transfer.vert_mapping = "POLYINTERP_NEAREST"
    for group in proxy.vertex_groups:
        mesh.vertex_groups.new(name=group.name)
    bpy.ops.object.modifier_apply(modifier=transfer.name)
    bpy.data.objects.remove(proxy, do_unlink=True)

    def assign(ids, bone):
        ids = [int(i) for i in ids]
        if not ids:
            return
        for group in mesh.vertex_groups:
            group.remove(ids)
        mesh.vertex_groups[bone].add(ids, 1, "REPLACE")

    if name == "medieval_peasant_warrior":
        # Recover the hand surface by connectivity, rather than a box that could
        # also catch the tunic beside it. Weld only the analysis coordinates.
        ids = connected_region(
            mesh,
            points,
            lambda p: (p[:, 0] < -0.18) & (p[:, 2] > 0.68) & (p[:, 2] < 0.99),
            np.array([-0.28, -0.06, 0.82]),
        )
        for i in ids:
            w = float(np.clip((0.97 - points[i, 2]) / 0.06, 0, 1))
            for group in mesh.vertex_groups:
                group.remove([int(i)])
            if w:
                mesh.vertex_groups["hand_r"].add([int(i)], w, "REPLACE")
            if w < 1:
                mesh.vertex_groups["lowerarm_r"].add([int(i)], 1 - w, "REPLACE")
    if name == "fantasy_villager":
        # The staff is a straight tapered shaft through the closed right hand.
        # Keep every shaft/ring vertex rigidly attached to that hand.
        shaft_x = -0.321 - 0.017 * points[:, 2]
        staff = (points[:, 0] - shaft_x) ** 2 + (points[:, 1] + 0.026) ** 2 < 0.031**2
        assign(np.flatnonzero(staff), "hand_r")

        # Cloak fabric follows the torso rather than being pulled apart by the legs.
        def smoothstep(x):
            x = np.clip(x, 0, 1)
            return x * x * (3 - 2 * x)

        cloak = (
            smoothstep((points[:, 1] - 0.12) / 0.08)
            * smoothstep((points[:, 2] - 0.30) / 0.12)
            * smoothstep((1.55 - points[:, 2]) / 0.15)
        )
        for i in np.flatnonzero(cloak > 0):
            amount = float(cloak[i])
            vtx = mesh.data.vertices[int(i)]
            existing = [(g.group, g.weight) for g in vtx.groups]
            spine_blend = float(smoothstep((points[i, 2] - 0.95) / 0.40))
            for j, w in existing:
                mesh.vertex_groups[j].add([int(i)], w * (1 - amount), "REPLACE")
            for target, fraction in [
                ("pelvis", 1 - spine_blend),
                ("spine_01", spine_blend),
            ]:
                old = next(
                    (w for j, w in existing if mesh.vertex_groups[j].name == target), 0
                )
                mesh.vertex_groups[target].add(
                    [int(i)], old * (1 - amount) + amount * fraction, "REPLACE"
                )
        log(
            "ACCESSORIES",
            int(staff.sum()),
            "staff vertices",
            int((cloak > 0).sum()),
            "cloak vertices",
        )
        # The left hand lies close to the dark tunic hem. Remove transferred arm
        # influence from that cloth while leaving the forward-facing fingers alone.
        hem = (
            smoothstep((points[:, 1] + 0.085) / 0.04)
            * smoothstep((0.80 - points[:, 2]) / 0.06)
            * (points[:, 0] > 0.14)
        )
        arm_groups = {"hand_l", "lowerarm_l", "upperarm_l", "clavicle_l"}
        for i in np.flatnonzero(hem > 0):
            amount = float(hem[i])
            vtx = mesh.data.vertices[int(i)]
            existing = [(g.group, g.weight) for g in vtx.groups]
            removed = 0
            for j, w in existing:
                if mesh.vertex_groups[j].name in arm_groups:
                    removed += w * amount
                    mesh.vertex_groups[j].add([int(i)], w * (1 - amount), "REPLACE")
            if removed:
                old = next(
                    (w for j, w in existing if mesh.vertex_groups[j].name == "thigh_l"),
                    0,
                )
                mesh.vertex_groups["thigh_l"].add([int(i)], old + removed, "REPLACE")
    mesh.parent = rig
    mod = mesh.modifiers.new("Humanoid skin", "ARMATURE")
    mod.object = rig
    mod.use_deform_preserve_volume = False
    # Restrict to four normalized influences, matching both glTF and the runtime.
    activate(mesh)
    bpy.ops.object.vertex_group_limit_total(limit=4)
    bpy.ops.object.vertex_group_normalize_all(lock_active=False)
    missing = [
        v.index
        for v in mesh.data.vertices
        if not v.groups or sum(g.weight for g in v.groups) < 0.001
    ]
    if missing:
        raise RuntimeError(f"{name}: unweighted production vertices {len(missing)}")
    return dict(
        method=method,
        unweighted=0,
        max_influences=max(len(v.groups) for v in mesh.data.vertices),
    )


def clips(rig, name):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(LIB / "Unreal-Godot/UAL1_Standard.glb"))
    imported = [o for o in bpy.data.objects if o not in before]
    source = next(o for o in imported if o.type == "ARMATURE")
    sr = {b.name: b.matrix_local.copy() for b in source.data.bones}
    rest = {b.name: b.matrix_local.copy() for b in rig.data.bones}
    # Align each target bind axis to the library's neutral pose, including posed arms.
    reference = {}
    for b in rig.data.bones:
        q = b.matrix_local.to_quaternion()
        if b.name in sr and b.name != "root":
            sb = source.data.bones[b.name]
            q = (b.tail_local - b.head_local).normalized().rotation_difference(
                (sb.tail_local - sb.head_local).normalized()
            ) @ q
        reference[b.name] = q
    scene = bpy.context.scene
    scene.render.fps = 30
    library_actions = {key: bpy.data.actions[key] for key in ["Idle_Loop", "Walk_Loop"]}
    result = []
    for label, key, duration in [
        ("idle", "Idle_Loop", 2.5),
        ("walk", "Walk_Loop", 1.3333333333333333),
    ]:
        src = library_actions[key]
        action(source, src)
        action(rig, None)
        clip = bpy.data.actions.new(label)
        clip.use_fake_user = True
        action(rig, clip)
        frames = round(duration * 30)
        samples = []
        for frame in range(frames + 1):
            # Sample loop endpoint from its start to make the seam exactly continuous.
            t = frame / frames if frame < frames else 0
            f = src.frame_range[0] + t * (src.frame_range[1] - src.frame_range[0])
            scene.frame_set(int(f), subframe=f - int(f))
            bpy.context.view_layer.update()
            posed = {}
            for bone in rig.data.bones:
                n = bone.name
                pa = bone.parent
                local = rest[pa.name].inverted() @ rest[n] if pa else rest[n]
                base = posed[pa.name] if pa else Matrix.Identity(4)
                desired = reference[n]
                if n in sr and n != "root":
                    desired = (
                        source.pose.bones[n].matrix.to_quaternion()
                        @ sr[n].to_quaternion().inverted()
                        @ reference[n]
                    )
                if name == "fantasy_villager" and n in [
                    "clavicle_r",
                    "upperarm_r",
                    "lowerarm_r",
                    "hand_r",
                ]:
                    torso = (
                        source.pose.bones["spine_03"].matrix.to_quaternion()
                        @ sr["spine_03"].to_quaternion().inverted()
                    )
                    desired = torso @ rest[n].to_quaternion()
                if name == "medieval_peasant_warrior" and n in [
                    "clavicle_l",
                    "upperarm_l",
                    "lowerarm_l",
                    "hand_l",
                ]:
                    torso = (
                        source.pose.bones["spine_03"].matrix.to_quaternion()
                        @ sr["spine_03"].to_quaternion().inverted()
                    )
                    desired = torso @ rest[n].to_quaternion()
                if name == "fantasy_villager" and n == "hand_l":
                    # Preserve the scanned glove's palm roll. The library hand axis alone
                    # cannot recover palm orientation without this model's finger bones.
                    desired = (
                        posed["lowerarm_l"].to_quaternion()
                        @ rest["lowerarm_l"].to_quaternion().inverted()
                        @ rest[n].to_quaternion()
                    )
                position = local.translation.copy()
                if n == "pelvis":
                    # Source root translations include the mannequin's authoring offset.
                    # Keep these clips in place; ground compensation supplies vertical motion.
                    position.z += (
                        0.006 * math.cos(4 * math.pi * t)
                        if label == "walk"
                        else 0.002 * math.sin(2 * math.pi * t)
                    )
                posed[n] = base @ Matrix.LocRotScale(
                    position,
                    base.to_quaternion().inverted() @ desired,
                    Vector((1, 1, 1)),
                )
            samples.append(posed)
        # Add a small global lift so each pose stays above the floor. This correction
        # is based on the skinned shoe vertices, not approximate bone endpoints.
        shoe_ids = [v.index for v in mesh_global.data.vertices if v.co.z < 0.075]
        shoe_positions = np.array(
            [list(mesh_global.data.vertices[i].co) + [1] for i in shoe_ids]
        )
        shoe_weights = [
            [
                (mesh_global.vertex_groups[g.group].name, g.weight)
                for g in mesh_global.data.vertices[i].groups
            ]
            for i in shoe_ids
        ]
        inverse = {n: m.inverted() for n, m in rest.items()}
        for frame, posed in enumerate(samples):
            transforms = {n: np.array(posed[n] @ inverse[n]) for n in rest}
            minz = 10.0
            for p, weights in zip(shoe_positions, shoe_weights):
                minz = min(minz, sum(w * (transforms[n] @ p)[2] for n, w in weights))
            lift = -minz + 0.002
            for m in posed.values():
                m.translation.z += lift
            for bone in rig.data.bones:
                n = bone.name
                pa = bone.parent
                local = rest[pa.name].inverted() @ rest[n] if pa else rest[n]
                base = posed[pa.name] if pa else Matrix.Identity(4)
                pb = rig.pose.bones[n]
                pb.rotation_mode = "QUATERNION"
                pb.matrix_basis = local.inverted() @ base.inverted() @ posed[n]
                pb.keyframe_insert("location", frame=frame)
                pb.keyframe_insert("rotation_quaternion", frame=frame)
                pb.keyframe_insert("scale", frame=frame)
        result.append((label, clip, frames, duration))
        log("CLIP", name, label, frames + 1)
    # Remove the animation-library mannequin and its unused data from delivery.
    for obj in imported:
        bpy.data.objects.remove(obj, do_unlink=True)
    for a in list(bpy.data.actions):
        if a not in [x[1] for x in result]:
            bpy.data.actions.remove(a)
    action(rig, result[0][1])
    scene.frame_start = 0
    scene.frame_end = result[0][2]
    scene.frame_set(0)
    return result


names = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else list(CONFIG)
for name in names:
    log("START", name)
    mesh_global, source, points = prepare(name)
    rig = skeleton(name)
    stats = skin(mesh_global, rig, points, name)
    out = NPC / name / "rigged"
    out.mkdir(exist_ok=True)
    animations = clips(rig, name)
    bpy.ops.file.pack_all()
    bpy.ops.wm.save_as_mainfile(filepath=str(out / (name + "_rigged.blend")))
    # NLA tracks give the glTF exporter stable idle/walk clip names.
    action(rig, None)
    for label, clip, frames, duration in animations:
        track = rig.animation_data.nla_tracks.new()
        track.name = label
        strip = track.strips.new(label, 0, clip)
        track.mute = True
    activate(mesh_global)
    rig.select_set(True)
    bpy.ops.export_scene.gltf(
        filepath=str(out / (name + "_rigged.glb")),
        export_format="GLB",
        use_selection=True,
        export_animations=True,
        export_animation_mode="NLA_TRACKS",
        export_force_sampling=True,
        export_frame_range=False,
        export_skins=True,
        export_all_influences=False,
    )
    for track in rig.animation_data.nla_tracks:
        rig.animation_data.nla_tracks.remove(track)
    action(rig, animations[0][1])
    bpy.context.scene.frame_set(0)
    shutil.copy2(NPC / name / "license.txt", out / "model-license.txt")
    shutil.copy2(LIB / "License.txt", out / "animation-license.txt")
    manifest = dict(
        name=name,
        height_metres=1.8,
        triangles=tri_count(mesh_global),
        bones=len(rig.data.bones),
        clips=[
            dict(name=n, frames=f + 1, duration=d, in_place=True)
            for n, a, f, d in animations
        ],
        weights=stats,
        source=str(source.relative_to(ROOT)),
        source_sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
        animation_source="Quaternius Universal Animation Library Standard (CC0)",
        blender=bpy.app.version_string,
    )
    (out / "rig-manifest.json").write_text(json.dumps(manifest, indent=2))
    log("DONE", name)
