"""Validate the delivered GLBs, including deformation at every exported frame."""

import bpy, json, pathlib, numpy as np, sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
names = json.loads((ROOT / "assets_itch/npc/selection.json").read_text())["selected"]
if "--" in sys.argv:
    names = sys.argv[sys.argv.index("--") + 1 :]
report = []
for name in names:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.render.fps = 30
    p = ROOT / "assets_itch/npc" / name / "rigged" / (name + "_rigged.glb")
    bpy.ops.import_scene.gltf(filepath=str(p))
    rig = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
    meshes = [
        o
        for o in bpy.context.scene.objects
        if o.type == "MESH"
        and any(m.type == "ARMATURE" and m.object == rig for m in o.modifiers)
    ]
    assert 1 <= len(rig.data.bones) <= 128
    assert {a.name for a in bpy.data.actions} == {"idle", "walk"}
    for t in rig.animation_data.nla_tracks:
        t.mute = True
    triangles = 0
    max_influences = 0
    weight_error = 0
    edges_data = []
    for o in meshes:
        o.data.calc_loop_triangles()
        triangles += len(o.data.loop_triangles)
        for v in o.data.vertices:
            weights = [g.weight for g in v.groups if g.weight > 0]
            assert weights and min(weights) >= 0
            max_influences = max(max_influences, len(weights))
            weight_error = max(weight_error, abs(sum(weights) - 1))
        assert o.data.uv_layers and o.data.materials
        base = np.array([v.co[:] for v in o.data.vertices])
        edges = np.array([e.vertices[:] for e in o.data.edges])
        lengths = np.linalg.norm(base[edges[:, 0]] - base[edges[:, 1]], axis=1)
        edges_data.append((o, edges, lengths))
    assert max_influences <= 4 and weight_error < 1e-5
    used_images = {
        n.image
        for o in meshes
        for m in o.data.materials
        if m and m.use_nodes
        for n in m.node_tree.nodes
        if n.type == "TEX_IMAGE" and n.image
    }
    for im in used_images:
        # Imported packed images load their pixels lazily.
        assert min(im.size) > 0 and len(im.pixels) > 0, im.name
    row = dict(
        name=name,
        triangles=triangles,
        bones=len(rig.data.bones),
        max_influences=max_influences,
        max_weight_sum_error=weight_error,
        clips=[],
    )
    for a in bpy.data.actions:
        rig.animation_data.action = a
        rig.animation_data.action_slot = a.slots[0]
        start, end = map(int, a.frame_range)
        floor = []
        max_stretch = 0
        endpoints = []
        min_bounds = np.full(3, np.inf)
        max_bounds = -min_bounds
        max_step = 0
        prev = None
        roots = []
        for f in range(start, end + 1):
            bpy.context.scene.frame_set(f)
            bpy.context.view_layer.update()
            dg = bpy.context.evaluated_depsgraph_get()
            joints = np.array(
                [list(rig.matrix_world @ b.matrix.translation) for b in rig.pose.bones]
            )
            roots.append(list(rig.pose.bones["root"].matrix.translation))
            if prev is not None:
                max_step = max(
                    max_step, float(np.linalg.norm(joints - prev, axis=1).max())
                )
            prev = joints
            if f in [start, end]:
                endpoints.append(
                    np.array([list(map(list, b.matrix)) for b in rig.pose.bones])
                )
            lowest = 10
            for o, edges, lengths in edges_data:
                ev = o.evaluated_get(dg)
                me = ev.to_mesh()
                positions = np.empty(len(me.vertices) * 3)
                me.vertices.foreach_get("co", positions)
                positions = positions.reshape(-1, 3)
                assert np.isfinite(positions).all()
                lowest = min(lowest, float(positions[:, 2].min()))
                min_bounds = np.minimum(min_bounds, positions.min(0))
                max_bounds = np.maximum(max_bounds, positions.max(0))
                growth = (
                    np.linalg.norm(
                        positions[edges[:, 0]] - positions[edges[:, 1]], axis=1
                    )
                    - lengths
                )
                max_stretch = max(max_stretch, float(growth.max()))
                ev.to_mesh_clear()
            floor.append(lowest)
        seam = float(np.max(np.abs(endpoints[0] - endpoints[-1])))
        assert seam < 1e-4, (name, a.name, seam)
        assert min(floor) > -0.02, (name, a.name, "below floor", min(floor))
        assert max(floor) < 0.025, (name, a.name, "floating", max(floor))
        assert max_step < 0.15, (name, a.name, "joint discontinuity", max_step)
        root_xy = float(np.abs(np.array(roots)[:, :2]).max())
        assert root_xy < 1e-5, (name, a.name, "root offset", root_xy)
        row["clips"].append(
            dict(
                name=a.name,
                duration_seconds=(end - start) / 30,
                sampled_frames=end - start + 1,
                loop_matrix_error=seam,
                lowest_vertex_z_range=[min(floor), max(floor)],
                max_joint_step_m=max_step,
                max_edge_growth_m=max_stretch,
                max_root_horizontal_offset_m=root_xy,
                bounds=[min_bounds.tolist(), max_bounds.tolist()],
            )
        )
    report.append(row)
    print(json.dumps(row), flush=True)
out = ROOT / "artifacts/npc-rigs/validation.json"
if "--" in sys.argv and out.exists():
    previous = json.loads(out.read_text())
    report = report + [r for r in previous if r["name"] not in names]
out.write_text(json.dumps(report, indent=2))
print("VALIDATED", out, flush=True)
