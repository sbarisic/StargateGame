"""Blender offline refinement of MoreRealisticTree_01 from its untouched glTF source.
Run blender -b --python tools/refine_tree_detail.py -- --output <cache directory>.
Uses the existing tree reduction algorithm, with 20k wood triangles and 40k leaves.
Nicholas-3D source assets remain CC BY 4.0; existing attribution applies.
"""

import argparse, hashlib, json, math, sys
from pathlib import Path
import bpy, bmesh, numpy as np
from mathutils import Vector, Matrix

ROOT = Path(__file__).resolve().parents[1]


def triangles(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


def components(mesh):
    parent = list(range(len(mesh.vertices)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for e in mesh.edges:
        a, b = e.vertices
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb
    groups = {}
    for poly in mesh.polygons:
        groups.setdefault(find(poly.vertices[0]), []).append(poly.index)
    return list(groups.values())


def weld_join(obs, name):
    bpy.ops.object.select_all(action="DESELECT")
    for o in obs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = obs[0]
    bpy.ops.object.join()
    o = bpy.context.object
    o.name = name
    o.data.transform(o.matrix_world)
    o.parent = None
    o.matrix_world = Matrix.Identity(4)
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=0.00001)
    bm.to_mesh(o.data)
    bm.free()
    o.data.update()
    return o


def wood_reduce(src, groups, target, label):
    # Keep the trunk and larger branch components by surface area, avoiding the
    # thousands of tiny disconnected terminal twigs that block useful decimation.
    ranked = sorted(
        groups, key=lambda g: sum(src.data.polygons[i].area for i in g), reverse=True
    )
    limit = min(len(ranked), max(24, round(target / 50)))
    for attempt in range(4):
        o = src.copy()
        o.data = src.data.copy()
        bpy.context.scene.collection.objects.link(o)
        o.name = label
        o.hide_render = False
        keep = set(i for g in ranked[:limit] for i in g)
        bm = bmesh.new()
        bm.from_mesh(o.data)
        bm.faces.ensure_lookup_table()
        bmesh.ops.delete(
            bm, geom=[f for f in bm.faces if f.index not in keep], context="FACES"
        )
        bm.to_mesh(o.data)
        bm.free()
        o.data.update()
        bpy.ops.object.select_all(action="DESELECT")
        o.select_set(True)
        bpy.context.view_layer.objects.active = o
        dec = o.modifiers.new("Preserve primary branches", "DECIMATE")
        dec.ratio = min(1, target / max(1, triangles(o)))
        dec.use_collapse_triangulate = True
        bpy.ops.object.modifier_apply(modifier=dec.name)
        n = triangles(o)
        if n <= target * 1.15:
            break
        bpy.data.objects.remove(o, do_unlink=True)
        limit = max(12, int(limit * 0.6))
    for p in o.data.polygons:
        p.use_smooth = True
    return o, limit


def leaf_records(src, groups):
    mesh = src.data
    uvlayer = mesh.uv_layers[0]
    records = []
    for fs in groups:
        loops = [i for f in fs for i in mesh.polygons[f].loop_indices]
        ids = [mesh.loops[i].vertex_index for i in loops]
        p = np.array([mesh.vertices[i].co[:] for i in ids])
        uv = np.array([uvlayer.data[i].uv[:] for i in loops])
        umin = uv.min(0)
        umax = uv.max(0)
        # Fit a textured quad to the source leaf, retaining its original atlas region.
        coefficients = np.linalg.lstsq(
            np.column_stack((uv, np.ones(len(uv)))), p, rcond=None
        )[0]
        q = np.array(
            [
                [umin[0], umin[1]],
                [umax[0], umin[1]],
                [umax[0], umax[1]],
                [umin[0], umax[1]],
            ]
        )
        coords = np.column_stack((q, np.ones(4))) @ coefficients
        records.append((coords, q, p.mean(0)))
    return records


def leaves_reduce(src, records, count, label, seed):
    centers = np.array([r[2] for r in records])
    span = np.maximum(np.ptp(centers, axis=0), 1e-6)
    cell = np.floor((centers - centers.min(0)) / span * 24).astype(int)
    rng = np.random.default_rng(seed)
    order = np.lexsort((rng.random(len(records)), cell[:, 2], cell[:, 1], cell[:, 0]))
    count = min(count, len(records))
    selected = order[np.linspace(0, len(order) - 1, count, dtype=int)]
    # Force the canopy's extrema into the representative set.
    extrema = np.unique(np.r_[centers.argmin(0), centers.argmax(0)])
    for i, e in enumerate(extrema):
        if e not in selected:
            selected[i] = e
    growth = min(1.65, max(1, math.sqrt(len(records) / count) * 0.78))
    verts = []
    uvs = []
    faces = []
    for idx in selected:
        coords, uv, center = records[idx]
        coords = (coords - center) * growth + center
        base = len(verts)
        verts.extend(coords.tolist())
        uvs.extend(uv.tolist())
        faces.extend([(base, base + 1, base + 2), (base, base + 2, base + 3)])
    me = bpy.data.meshes.new(label)
    me.from_pydata(verts, [], faces)
    me.update()
    # Each source UV channel uses the same per-leaf atlas, retain required names.
    for source_uv in src.data.uv_layers:
        layer = me.uv_layers.new(name=source_uv.name)
        for loop in me.loops:
            layer.data[loop.index].uv = uvs[loop.vertex_index]
    for mat in src.data.materials:
        me.materials.append(mat)
    o = bpy.data.objects.new(label, me)
    bpy.context.scene.collection.objects.link(o)
    return o, growth


def bounds(obs):
    bpy.context.view_layer.update()
    pts = [o.matrix_world @ Vector(c) for o in obs for c in o.bound_box]
    return (
        pts,
        Vector([min(v[i] for v in pts) for i in range(3)]),
        Vector([max(v[i] for v in pts) for i in range(3)]),
    )


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--output", type=Path, required=True)
    args = ap.parse_args(sys.argv[sys.argv.index("--") + 1 :])
    args.output.mkdir(parents=True, exist_ok=True)
    source_dir = ROOT / "assets_itch/trees/more_realistic_trees_free"
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(source_dir / "scene.gltf"))
    bpy.context.view_layer.update()
    groups = {"bark": [], "leaves": []}
    for o in list(bpy.context.scene.objects):
        if o.type != "MESH":
            continue
        a = o.parent
        while a and not a.name.startswith("Tree"):
            a = a.parent
        if ".001" in a.name:
            bpy.data.objects.remove(o, do_unlink=True)
            continue
        groups[
            "leaves" if "leaf" in o.data.materials[0].name.lower() else "bark"
        ].append(o)
    source = {k: weld_join(v, "Original_" + k) for k, v in groups.items()}
    records = leaf_records(source["leaves"], components(source["leaves"].data))
    wood, retained = wood_reduce(
        source["bark"], components(source["bark"].data), 20000, "Detail_wood"
    )
    leaves, growth = leaves_reduce(
        source["leaves"], records, 40000, "Detail_leaves", 18
    )
    desired = min(
        1.9, max(1, math.sqrt(len(records) / min(40000, len(records))) * 0.95)
    )
    for i in range(0, len(leaves.data.vertices), 4):
        vs = [leaves.data.vertices[i + j] for j in range(4)]
        center = sum((v.co for v in vs), Vector()) / 4
        for v in vs:
            v.co = center + (v.co - center) * (desired / growth)
    leaves.data.update()
    _, lo, hi = bounds(list(source.values()))
    offset = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))
    bpy.ops.object.select_all(action="DESELECT")
    for o in [wood, leaves]:
        o.location = -offset
        o.select_set(True)
    target = args.output / "MoreRealisticTree_01_detail.glb"
    bpy.ops.export_scene.gltf(
        filepath=str(target),
        export_format="GLB",
        use_selection=True,
        export_yup=True,
        export_animations=False,
    )
    report = dict(
        model="MoreRealisticTree_01",
        woodTriangles=triangles(wood),
        leafTriangles=triangles(leaves),
        sourceLeaves=len(records),
        retainedWoodComponents=retained,
        leafScale=desired,
        source={
            p.name: hashlib.sha256(p.read_bytes()).hexdigest()
            for p in [source_dir / "scene.gltf", source_dir / "scene.bin"]
        },
        outputSha256=hashlib.sha256(target.read_bytes()).hexdigest(),
        tool="tools/refine_tree_detail.py",
        author="Nicholas-3D",
        license="CC BY 4.0",
    )
    (args.output / "detail-report.json").write_text(json.dumps(report, indent=2))
    print(json.dumps(report), flush=True)


if __name__ == "__main__":
    main()
