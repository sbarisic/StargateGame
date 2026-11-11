"""Blender source extraction for the inspection-only tree prototype.

Original models and collision assets are never modified. Run Blender in background
with --python this_file -- --output PATH, then run cook_tree_prototype.py.
"""

import argparse
import hashlib
import json
import sys
from pathlib import Path

import bpy
from mathutils import Vector

sys.path.insert(0, str(Path(__file__).parent))
from refine_tree_detail import components, weld_join, triangles, bounds, wood_reduce

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument(
        "--tree",
        choices=["MoreRealisticTree_01", "MoreRealisticTree_02"],
        default="MoreRealisticTree_01",
    )
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :])
    args.output.mkdir(parents=True, exist_ok=True)
    source = ROOT / "assets_itch/trees/more_realistic_trees_free"
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(source / "scene.gltf"))
    groups = {"wood": [], "leaf": []}
    for obj in list(bpy.context.scene.objects):
        if obj.type != "MESH":
            continue
        ancestor = obj.parent
        while ancestor and not ancestor.name.startswith("Tree"):
            ancestor = ancestor.parent
        if ancestor is None or ((".001" in ancestor.name) != args.tree.endswith("02")):
            continue
        role = "leaf" if "leaf" in obj.data.materials[0].name.lower() else "wood"
        groups[role].append(obj)
    wood = weld_join(groups["wood"], "SourceWood")
    leaves = weld_join(groups["leaf"], "SourceLeaves")
    _, minimum, maximum = bounds([wood, leaves])
    offset = Vector(
        ((minimum.x + maximum.x) / 2, (minimum.y + maximum.y) / 2, minimum.z)
    )
    report = {
        "model": args.tree,
        "sourceWoodTriangles": triangles(wood),
        "sourceLeafTriangles": triangles(leaves),
        "lods": [],
    }
    for target in [40000, 10000, 3000]:
        reduced, retained_groups = wood_reduce(
            wood, components(wood.data), target, f"Wood{target}"
        )
        bpy.ops.object.select_all(action="DESELECT")
        for obj in [reduced, leaves]:
            obj.location = -offset
            obj.select_set(True)
        path = args.output / f"tree-{target}.glb"
        bpy.ops.export_scene.gltf(
            filepath=str(path),
            export_format="GLB",
            use_selection=True,
            export_yup=True,
            export_animations=False,
        )
        report["lods"].append(
            {
                "file": path.name,
                "woodTriangles": triangles(reduced),
                "retainedBranchGroups": retained_groups,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            }
        )
        bpy.data.objects.remove(reduced, do_unlink=True)
    report["sources"] = {
        name: hashlib.sha256((source / name).read_bytes()).hexdigest()
        for name in ["scene.gltf", "scene.bin"]
    }
    report["license"] = (
        "Nicholas-3D, CC BY 4.0; assets/models/more-realistic-trees-license.txt"
    )
    (args.output / "extraction.json").write_text(json.dumps(report, indent=2))
    print(json.dumps(report), flush=True)


if __name__ == "__main__":
    main()
