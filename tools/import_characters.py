"""Offline skeletal importer. Run with Blender --background --python tools/import_characters.py.
Sources are read-only; output is a versioned runtime package with provenance.
"""

import bpy, json, gzip, pathlib, hashlib, math, sys, subprocess
from mathutils import Matrix, Vector

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "assets/characters"
OUT.mkdir(parents=True, exist_ok=True)
SOURCE = ROOT / "assets_itch"
sources = set()


def remember(p):
    sources.add(pathlib.Path(p).resolve())
    return str(p)


def source_name(p):
    # Asset sources may live outside the checkout behind the assets_itch junction.
    if p.is_relative_to(SOURCE.resolve()):
        return (pathlib.Path("assets_itch") / p.relative_to(SOURCE.resolve())).as_posix()
    return p.relative_to(ROOT).as_posix()


def flatten(m):
    return [v for row in m for v in row]  # runtime transposes column-vector matrices


def texture(path, name):
    path = pathlib.Path(path)
    remember(path)
    im = bpy.data.images.load(str(path), check_existing=False)
    if max(im.size) > 2048:
        ratio = 2048 / max(im.size)
        im.scale(round(im.size[0] * ratio), round(im.size[1] * ratio))
    destination = OUT / name
    temporary = destination.with_name(destination.stem + ".importing.png")
    im.filepath_raw = str(temporary)
    im.file_format = "PNG"
    im.save()
    bpy.data.images.remove(im)
    if destination.exists() and destination.read_bytes() == temporary.read_bytes():
        temporary.unlink()
    else:
        temporary.replace(destination)
    return name


def export(name, meshes, rigs, space, clips, material_files, lods=(1,)):
    bones = [(r, b) for r in rigs for b in r.data.bones]
    lookup = {(r.name, b.name): i for i, (r, b) in enumerate(bones)}
    assert 0 < len(bones) <= 128
    bind = [space @ r.matrix_world @ b.matrix_local for r, b in bones]
    inv = [m.inverted() for m in bind]
    parents = [
        lookup.get((r.name, b.parent.name), -1) if b.parent else -1 for r, b in bones
    ]
    parts = []
    for ratio in lods:
        for obj in meshes:
            rig = next(
                (m.object for m in obj.modifiers if m.type == "ARMATURE"), None
            ) or (obj.parent if obj.parent and obj.parent.type == "ARMATURE" else None)
            if rig not in rigs:
                continue
            clone = obj.copy()
            clone.data = obj.data.copy()
            bpy.context.scene.collection.objects.link(clone)
            for mod in list(clone.modifiers):
                clone.modifiers.remove(mod)
            if ratio < 1:
                mod = clone.modifiers.new("LOD", "DECIMATE")
                mod.ratio = ratio
            ev = clone.evaluated_get(bpy.context.evaluated_depsgraph_get())
            me = ev.to_mesh()
            me.calc_loop_triangles()
            world = space @ obj.matrix_world
            nmat = world.to_3x3().inverted().transposed()
            normalmap = {}
            for mat_index in sorted(set(t.material_index for t in me.loop_triangles)):
                vertices = []
                normals = []
                uvs = []
                weights = []
                joints = []
                indices = []
                seen = {}
                for tri in me.loop_triangles:
                    if tri.material_index != mat_index:
                        continue
                    for li in tri.loops:
                        loop = me.loops[li]
                        v = me.vertices[loop.vertex_index]
                        uv = (
                            me.uv_layers.active.data[li].uv
                            if me.uv_layers.active
                            else (0, 0)
                        )
                        n = (nmat @ me.corner_normals[li].vector).normalized()
                        p = world @ v.co
                        influences = sorted(
                            [
                                (
                                    lookup[(rig.name, obj.vertex_groups[g.group].name)],
                                    g.weight,
                                )
                                for g in v.groups
                                if (rig.name, obj.vertex_groups[g.group].name) in lookup
                            ],
                            key=lambda x: -x[1],
                        )[:4]
                        if not influences:
                            influences = [
                                (lookup[(rig.name, rig.data.bones[0].name)], 1)
                            ]
                        total = sum(w for _, w in influences)
                        influences = [(j, w / total) for j, w in influences] + [
                            (0, 0)
                        ] * (4 - len(influences))
                        key = (tuple(p), tuple(n), tuple(uv), tuple(influences))
                        if key not in seen:
                            seen[key] = len(vertices) // 3
                            vertices += list(p)
                            normals += list(n)
                            uvs += list(uv)
                            joints += [j for j, w in influences]
                            weights += [w for j, w in influences]
                        indices.append(seen[key])
                mat = (
                    obj.material_slots[
                        min(mat_index, len(obj.material_slots) - 1)
                    ].material
                    if obj.material_slots
                    else None
                )
                files = material_files(obj, mat)
                parts.append(
                    dict(
                        name=obj.name,
                        lod=ratio,
                        positions=vertices,
                        normals=normals,
                        uvs=uvs,
                        joints=joints,
                        weights=weights,
                        indices=indices,
                        **files
                    )
                )
            ev.to_mesh_clear()
            bpy.data.objects.remove(clone, do_unlink=True)
        print("GEOMETRY", name, ratio, flush=True)
    animations = []
    for rig in rigs:
        rig.data.pose_position = "POSE"
    for clip, duration, setup in clips:
        frames = max(2, math.ceil(duration * 30) + 1)
        palettes = []
        for i in range(frames):
            setup(i / (frames - 1))
            bpy.context.view_layer.update()
            global_pose = [
                space @ r.matrix_world @ r.pose.bones[b.name].matrix for r, b in bones
            ]
            palettes += [
                flatten(global_pose[parent].inverted() @ m if parent >= 0 else m)
                for m, parent in zip(global_pose, parents)
            ]
        animations.append(
            dict(name=clip, duration=duration, frames=frames, matrices=palettes)
        )
        print("ANIMATION", name, clip, frames, flush=True)
    data = dict(
        version=2,
        animationSpace="local",
        name=name,
        bones=[b.name for r, b in bones],
        parents=parents,
        inverseBind=[flatten(m) for m in inv],
        parts=parts,
        clips=animations,
    )
    temporary = OUT / (name + ".sga.gz.tmp")
    with gzip.open(temporary, "wt", encoding="utf8") as f:
        json.dump(data, f, separators=(",", ":"))
    temporary.replace(OUT / (name + ".sga.gz"))
    return data


# First-person pack: original authoring file preserves synchronized hand/weapon poses.
retro = SOURCE / "RetroWeaponPack/RetroWeaponsPack"
blend = retro / "FP_Arms/BlendFiles/FP_Arms_Rifle_01_Anims.blend"
remember(blend)
bpy.ops.wm.open_mainfile(filepath=str(blend))
arms = bpy.data.objects["Arms_Armature"]
rifle = bpy.data.objects["Rifle_01_Armature"]
cam = bpy.data.objects["Camera"]
weapon_blend = retro / "Guns/Rifle_01/BlendFile/Rifle_01.blend"
remember(weapon_blend)
with bpy.data.libraries.load(str(weapon_blend), link=False) as (a, b):
    b.objects = [n for n in a.objects if n.endswith("_Mesh")]
meshes = [bpy.data.objects["FPS_Arms_Mesh"]]
# The source arms use flat polygon shading. Smooth the skin, preserving the
# deliberately hard edges on the rifle and its accessories.
for polygon in meshes[0].data.polygons:
    polygon.use_smooth = True
meshes[0].data.update()
for o in b.objects:
    if o is None or o.type != "MESH":
        continue
    bpy.context.scene.collection.objects.link(o)
    o.parent = rifle
    o.matrix_parent_inverse = Matrix.Identity(4)
    o.matrix_basis = Matrix.Identity(4)
    for mod in o.modifiers:
        if mod.type == "ARMATURE":
            mod.object = rifle
    meshes.append(o)
# Additional meshes include the detachable magazine.
extra = retro / "Guns/Rifle_01/Fbx_Files/Rifle_01_AdditionalMeshes.fbx"
if extra.exists():
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=remember(extra))
    extras = [o for o in bpy.data.objects if o not in before and o.type == "MESH"]
    for o in extras:
        if "mag" not in o.name.lower():
            continue
        o.parent = rifle
        o.matrix_parent_inverse = Matrix.Identity(4)
        o.matrix_basis = Matrix.Identity(4)
        o.vertex_groups.clear()
        g = o.vertex_groups.new(name="Magazine")
        g.add(list(range(len(o.data.vertices))), 1, "REPLACE")
        for m in list(o.modifiers):
            o.modifiers.remove(m)
        m = o.modifiers.new("Rig", "ARMATURE")
        m.object = rifle
        meshes.append(o)
# Keep the authored skin replacement when reimporting the animated source.
# The original Retro atlas remains available as a fallback and source reference.
if (OUT / "retro-arms-detail.png").exists():
    remember(OUT / "retro-arms-detail.png")
    armtex = "retro-arms-detail.png"
else:
    armtex = texture(retro / "FP_Arms/Texture/FPS_Arms_Albedo.png", "retro-arms.png")
rifletex = texture(
    next((retro / "Guns/Rifle_01").rglob("Rifle_01_Albedo.png")), "retro-rifle.png"
)


def action(r, n):
    r.animation_data_create()
    r.animation_data.action = bpy.data.actions.get(n)
    if r.animation_data.action and len(r.animation_data.action.slots):
        r.animation_data.action_slot = r.animation_data.action.slots[0]


action(arms, "Arms_BasePose")
action(rifle, "Rifle_BasePose")
bpy.context.scene.frame_set(1)
bpy.context.view_layer.update()
space = Matrix.Scale(0.01, 4) @ cam.matrix_world.inverted()
clips = []
for target, an, wn in [
    ("idle", "BasePose", "Breathing"),
    ("aim", "AimPose", "AimPose"),
    ("fire", "Fire", "Fire"),
    ("fire-aim", "AimFireAiming", "AimFire"),
    ("reload", "Reload", "Reload"),
    ("draw", "Draw", "Draw"),
    ("holster", "Hide", "Hide"),
    ("walk", "BasePose", "Walk"),
    ("run", "BasePose", "Run"),
    ("crouch-idle", "BasePose", "Breathing_Crouching"),
    ("crouch-walk", "BasePose", "Walk_Crouching"),
]:
    aa = bpy.data.actions.get("Arms_" + an)
    wa = bpy.data.actions.get("Rifle_" + wn)
    duration = (
        max(
            aa.frame_range[1] - aa.frame_range[0], wa.frame_range[1] - wa.frame_range[0]
        )
        / 30
    )

    def setup(t, aa=aa, wa=wa):
        action(arms, aa.name)
        action(rifle, wa.name)
        f = 1 + t * max(aa.frame_range[1] - 1, wa.frame_range[1] - 1)
        bpy.context.scene.frame_set(int(f), subframe=f - int(f))

    clips.append((target, max(duration, 1 / 30), setup))
for rig in [arms, rifle]:
    rig.data.pose_position = "REST"
export(
    "retro-rifle",
    meshes,
    [arms, rifle],
    space,
    clips,
    lambda o, m: {"albedo": armtex if o == meshes[0] else rifletex},
)
# Note export evaluates animation in pose mode; rest geometry above is mesh data, not evaluated skin.
manifest = {
    "version": 1,
    "sources": [
        {
            "path": source_name(p),
            "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
        }
        for p in sorted(sources)
    ],
    "packages": ["retro-rifle.sga.gz"],
    "notes": ["Original source files preserved. Centimetres converted to metres."],
}
(OUT / "manifest.json").write_text(json.dumps(manifest, indent=2))

# The explicit --original-arms option restores the source Retro mesh. Ordinary
# imports use the detailed arms, fitted offline onto these exact animation tracks.
if "--original-arms" not in sys.argv:
    sys.path.insert(0, str(ROOT / "tools"))
    import import_viewmodel_arms
    subprocess.run(["python", str(ROOT / "tools/import_viewmodel_arms.py")], check=True, cwd=ROOT)
    for name in import_viewmodel_arms.SOURCE_FILES:
        remember(import_viewmodel_arms.SOURCE / name)
    manifest["notes"].append("Cransh arms2 fitted to the original rifle rig; see arms2-provenance.json and arms2-license.txt.")
    manifest["viewmodelArms"] = "arms2-provenance.json"

# Body: reconstruct the supplied glTF skin explicitly, avoiding importer bind-transform ambiguity.
import numpy as np
from mathutils import Quaternion

p = SOURCE / "characters/military_tactical/scene.gltf"
d = json.loads(pathlib.Path(remember(p)).read_text())
buffers = [
    pathlib.Path(remember(p.parent / b["uri"])).read_bytes() for b in d["buffers"]
]


def acc(i):
    a = d["accessors"][i]
    v = d["bufferViews"][a["bufferView"]]
    dt = np.dtype(
        {5126: "<f4", 5125: "<u4", 5123: "<u2", 5121: "u1"}[a["componentType"]]
    )
    cols = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}[a["type"]]
    x = np.ndarray(
        (a["count"], cols),
        dtype=dt,
        buffer=buffers[v["buffer"]],
        offset=v.get("byteOffset", 0) + a.get("byteOffset", 0),
        strides=(v.get("byteStride", cols * dt.itemsize), dt.itemsize),
    ).copy()
    return x / np.iinfo(dt).max if a.get("normalized") and dt.kind in "ui" else x


nodes = d["nodes"]
glob = {}
parent = {}


def visit(i, pa):
    n = nodes[i]
    if "matrix" in n:
        local = np.array(n["matrix"]).reshape(4, 4).T
    else:
        r = n.get("rotation", [0, 0, 0, 1])
        local = np.array(
            Matrix.LocRotScale(
                Vector(n.get("translation", [0, 0, 0])),
                Quaternion((r[3], *r[:3])),
                Vector(n.get("scale", [1, 1, 1])),
            )
        )
    glob[i] = pa @ local
    for c in n.get("children", []):
        parent[c] = i
        visit(c, glob[i])


for i in d["scenes"][d.get("scene", 0)]["nodes"]:
    visit(i, np.eye(4))
skin = d["skins"][0]
jointnodes = skin["joints"]
ib = acc(skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1)
# Recover the actual bind pose. Baking the glTF's displayed idle pose back
# into rest geometry distorts elbows and wrists when that mesh is skinned again.
hips = next(j for j in jointnodes if "Hips_" in nodes[j].get("name", ""))
unit = float(
    np.median(np.linalg.norm((glob[hips] @ ib[jointnodes.index(hips)])[:3, :3], axis=0))
)
bind_space = np.diag([unit, unit, unit, 1.0])
bindglob = {j: bind_space @ np.linalg.inv(ib[k]) for k, j in enumerate(jointnodes)}
# Unweighted terminal markers have scaled-identity placeholder binds in this source.
# Their actual tip offsets come from the node hierarchy, not the origin.
weighted = set()
for node in nodes:
    if "skin" not in node:
        continue
    for primitive in d["meshes"][node["mesh"]]["primitives"]:
        attributes = primitive["attributes"]
        js = acc(attributes["JOINTS_0"]).astype(int)
        ws = acc(attributes["WEIGHTS_0"])
        weighted.update(js[ws > 0].tolist())
for k, j in enumerate(jointnodes):
    pa = parent.get(j)
    placeholder = np.allclose(ib[k][:3, 3], 0, atol=1e-8) and np.allclose(
        ib[k][:3, :3], np.eye(3) * ib[k][0, 0], atol=1e-8
    )
    if k not in weighted and pa in bindglob and placeholder:
        bindglob[j] = bindglob[pa] @ np.linalg.inv(glob[pa]) @ glob[j]
bpy.ops.wm.read_factory_settings(use_empty=True)
C = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
rigdata = bpy.data.armatures.new("Military")
rig = bpy.data.objects.new("Military", rigdata)
bpy.context.scene.collection.objects.link(rig)
bpy.context.view_layer.objects.active = rig
rig.select_set(True)
bpy.ops.object.mode_set(mode="EDIT")
bn = []
for j in jointnodes:
    n = nodes[j].get("name", str(j))
    b = rigdata.edit_bones.new(n)
    m = C @ Matrix(bindglob[j].tolist())
    b.head = m.translation
    b.tail = b.head + (m.to_quaternion() @ Vector((0, 0.05, 0)))
    b.align_roll(m.to_quaternion() @ Vector((0, 0, 1)))
    bn.append(n)
for k, j in enumerate(jointnodes):
    pa = parent.get(j)
    while pa is not None and pa not in jointnodes:
        pa = parent.get(pa)
    if pa in jointnodes:
        rigdata.edit_bones[bn[k]].parent = rigdata.edit_bones[bn[jointnodes.index(pa)]]
bpy.ops.object.mode_set(mode="OBJECT")
meshes = []
materialmap = {}
head = next(k for k, n in enumerate(bn) if n.startswith("mixamorig:Head_"))
for i, n in enumerate(nodes):
    if "mesh" not in n:
        continue
    for prim in d["meshes"][n["mesh"]]["primitives"]:
        a = prim["attributes"]
        pos = acc(a["POSITION"])
        hp = np.column_stack([pos, np.ones(len(pos))])
        ind = acc(prim["indices"]).reshape(-1, 3)
        if "skin" in n:
            js = acc(a["JOINTS_0"]).astype(int)
            ws = acc(a["WEIGHTS_0"])
            world = hp @ bind_space.T
        else:
            world = (
                hp
                @ (
                    bindglob[jointnodes[head]]
                    @ np.linalg.inv(glob[jointnodes[head]])
                    @ glob[i]
                ).T
            )
            js = np.full((len(pos), 4), head)
            ws = np.zeros((len(pos), 4))
            ws[:, 0] = 1
        mi = prim.get("material", 0)
        material = d["materials"][mi]
        matname = material.get("name", "material")
        # The source headset is displaced upward. Reattach rigidly around the head.
        if "Headphones" in matname:
            center = (world[:, :3].min(0) + world[:, :3].max(0)) * 0.5
            target = bindglob[jointnodes[head]][:3, 3] + np.array([0, 0.08, 0])
            world[:, :3] += target - center
            js[:] = head
            ws[:] = 0
            ws[:, 0] = 1
        verts = world[:, :3][:, [0, 2, 1]]
        verts[:, 1] *= -1
        mesh = bpy.data.meshes.new(matname)
        mesh.from_pydata(verts.tolist(), [], ind.tolist())
        mesh.update()
        obj = bpy.data.objects.new(matname, mesh)
        bpy.context.scene.collection.objects.link(obj)
        obj.parent = rig
        for name in bn:
            obj.vertex_groups.new(name=name)
        for vi in range(len(verts)):
            for j, w in zip(js[vi], ws[vi]):
                if w > 0:
                    obj.vertex_groups[int(j)].add([vi], float(w), "REPLACE")
        mod = obj.modifiers.new("Rig", "ARMATURE")
        mod.object = rig
        uv = acc(a["TEXCOORD_0"])
        layer = mesh.uv_layers.new()
        for loop in mesh.loops:
            layer.data[loop.index].uv = (
                uv[loop.vertex_index, 0],
                1 - uv[loop.vertex_index, 1],
            )
        for poly in mesh.polygons:
            poly.use_smooth = True
        mat = bpy.data.materials.new(matname)
        mesh.materials.append(mat)
        files = {}
        for role, ref in [
            (
                "albedo",
                material.get("pbrMetallicRoughness", {}).get("baseColorTexture"),
            ),
            ("normal", material.get("normalTexture")),
            (
                "metallicRoughness",
                material.get("pbrMetallicRoughness", {}).get(
                    "metallicRoughnessTexture"
                ),
            ),
        ]:
            if ref:
                image = d["images"][d["textures"][ref["index"]]["source"]]["uri"]
                name = "military-" + str(mi) + "-" + role + ".png"
                files[role] = texture(p.parent / image, name)
        materialmap[obj.name] = files
        meshes.append(obj)
# Retarget library world-space orientation deltas onto the reconstructed military rest skeleton.
ual = SOURCE / "Universal Animation Library[Standard]/Unreal-Godot/UAL1_Standard.glb"
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=remember(ual))
source = next(o for o in bpy.data.objects if o not in before and o.type == "ARMATURE")
mapping = {
    "Hips": "pelvis",
    "Spine": "spine_01",
    "Spine1": "spine_02",
    "Spine2": "spine_03",
    "Neck": "neck_01",
    "Head": "Head",
}
for side, suffix in [("Left", "l"), ("Right", "r")]:
    for a, b in [
        ("Shoulder", "clavicle"),
        ("Arm", "upperarm"),
        ("ForeArm", "lowerarm"),
        ("Hand", "hand"),
        ("UpLeg", "thigh"),
        ("Leg", "calf"),
        ("Foot", "foot"),
        ("ToeBase", "ball"),
    ]:
        mapping[side + a] = b + "_" + suffix
    for finger in ["Thumb", "Index", "Middle", "Ring", "Pinky"]:
        for segment in range(1, 5):
            mapping[side + "Hand" + finger + str(segment)] = (
                finger.lower()
                + "_"
                + str(segment).zfill(2)
                + ("_leaf" if segment == 4 else "")
                + "_"
                + suffix
            )
rest = {b.name: b.matrix_local.copy() for b in rig.data.bones}
srcrest = {b.name: b.matrix_local.copy() for b in source.data.bones}


def clean(n):
    return n.split(":")[-1].rsplit("_", 1)[0]


# Calibrate UAL's neutral reference against the military's bent reference.
reference = {}
for b in rig.data.bones:
    sn = mapping.get(clean(b.name))
    reference[b.name] = rest[b.name].to_quaternion()
    if not sn or sn not in srcrest:
        continue
    children = [c for c in b.children if mapping.get(clean(c.name)) in srcrest]
    if not children:
        continue
    child = children[0]
    sc = mapping[clean(child.name)]
    td = rest[child.name].translation - rest[b.name].translation
    sd = srcrest[sc].translation - srcrest[sn].translation
    if td.length > 1e-6 and sd.length > 1e-6:
        reference[b.name] = (
            td.normalized().rotation_difference(sd.normalized()) @ reference[b.name]
        )
    if clean(b.name) in ["LeftHand", "RightHand"]:
        side = clean(b.name).replace("Hand", "")
        suffix = "l" if side == "Left" else "r"
        index = next(x for x in rig.data.bones if clean(x.name) == side + "HandIndex1")
        pinky = next(x for x in rig.data.bones if clean(x.name) == side + "HandPinky1")

        def palm(origin, i, p):
            y = ((i + p) * 0.5 - origin).normalized()
            x = (i - p).normalized()
            z = x.cross(y).normalized()
            x = y.cross(z).normalized()
            return Matrix((x, y, z)).transposed().to_quaternion()

        target_palm = palm(
            rest[b.name].translation,
            rest[index.name].translation,
            rest[pinky.name].translation,
        )
        source_palm = palm(
            srcrest[sn].translation,
            srcrest["index_01_" + suffix].translation,
            srcrest["pinky_01_" + suffix].translation,
        )
        reference[b.name] = (
            source_palm @ target_palm.inverted() @ rest[b.name].to_quaternion()
        )


def pose(t, clip):
    a = next(
        a for a in bpy.data.actions if a.name == clip or a.name.startswith(clip + ".")
    )
    action(source, a.name)
    f = a.frame_range[0] + t * (a.frame_range[1] - a.frame_range[0])
    bpy.context.scene.frame_set(int(f), subframe=f - int(f))
    bpy.context.view_layer.update()
    # Build the complete hierarchy ourselves. Reading a Blender pose parent's
    # matrix immediately after assigning it can return the previous evaluation,
    # detaching fingers and feeding stale transforms into the next sample.
    desired_matrices = {}
    for b in rig.data.bones:
        pb = rig.pose.bones[b.name]
        sn = mapping.get(clean(b.name))
        local = (
            rest[b.parent.name].inverted() @ rest[b.name] if b.parent else rest[b.name]
        )
        base = desired_matrices[b.parent.name] if b.parent else Matrix.Identity(4)
        if sn and sn in source.pose.bones:
            delta = (
                source.pose.bones[sn].matrix.to_quaternion()
                @ srcrest[sn].to_quaternion().inverted()
            )
            desired = delta @ reference[b.name]
            q = base.to_quaternion().inverted() @ desired
            desired_matrices[b.name] = base @ Matrix.LocRotScale(
                local.translation, q, Vector((1, 1, 1))
            )
        else:
            desired_matrices[b.name] = base @ local
        # Basis is relative to the parent rest transform, not its pending pose.
        pb.matrix_basis = local.inverted() @ base.inverted() @ desired_matrices[b.name]
    if clip.startswith("Crouch_"):
        # Orientation-only retargeting leaves bent legs suspended below a
        # standing pelvis. Lower the root until the supporting foot returns to
        # its rest height, preserving every limb length and the authored gait.
        feet = [b.name for b in rig.data.bones if clean(b.name) in ("LeftFoot", "RightFoot")]
        if feet:
            shift = min(rest[n].translation.z for n in feet) - min(desired_matrices[n].translation.z for n in feet)
            for b in rig.data.bones:
                if b.parent is None:
                    rig.pose.bones[b.name].matrix_basis = rest[b.name].inverted() @ Matrix.Translation(Vector((0, 0, shift))) @ desired_matrices[b.name]
    bpy.context.view_layer.update()


clips = []


def bind_pose(t):
    for pb in rig.pose.bones:
        pb.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()


clips.append(("bind", 1, bind_pose))
for name, clip in [
    ("idle", "Idle_Loop"),
    ("walk", "Walk_Loop"),
    ("jog", "Jog_Fwd_Loop"),
    ("run", "Sprint_Loop"),
    ("crouch-idle", "Crouch_Idle_Loop"),
    ("crouch-walk", "Crouch_Fwd_Loop"),
    ("jump", "Jump_Start"),
    ("fall", "Jump_Loop"),
    ("land", "Jump_Land"),
    ("swim", "Swim_Fwd_Loop"),
    ("swim-idle", "Swim_Idle_Loop"),
]:
    a = next(
        a for a in bpy.data.actions if a.name == clip or a.name.startswith(clip + ".")
    )
    duration = (a.frame_range[1] - a.frame_range[0]) / bpy.context.scene.render.fps
    clips.append((name, duration, lambda t, c=clip: pose(t, c)))
# Geometry faces +Z in source; world characters face -Z.
space = Matrix.Rotation(math.pi, 4, "Y") @ C.inverted()
rootjoint = next(b for b in rig.data.bones if "Hips_" in b.name)
center = rootjoint.head_local
space = space @ Matrix.Translation(Vector((-center.x, -center.y, 0)))
export(
    "military",
    meshes,
    [rig],
    space,
    clips,
    lambda o, m: materialmap[o.name],
    (1, 0.5, 0.25),
)
manifest["sources"] = [
    {
        "path": source_name(p),
        "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
    }
    for p in sorted(sources)
]
manifest["packages"].append("military.sga.gz")
manifest["notes"] += [
    "Military headset reattached to head. Military locomotion retargeted from in-place Universal Animation Library."
]
(OUT / "manifest.json").write_text(json.dumps(manifest, indent=2))
for folder in ["characters/military_tactical", "Universal Animation Library[Standard]"]:
    for name in ["license.txt", "License.txt"]:
        f = SOURCE / folder / name
        if f.exists():
            (OUT / (pathlib.Path(folder).name + "-license.txt")).write_bytes(
                f.read_bytes()
            )
            break
