"""Deterministic settlement assets. Reads supplied glTF/GLB and existing kit meshes.
Sources are never modified. Requires numpy/Pillow; no Blender or game runtime.
"""

import hashlib, io, json, math, shutil, struct, sys
from pathlib import Path
import numpy as np
from PIL import Image
from building_base_geometry import repair_house, exterior_depth
from build_door_collision import generate_doors

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "assets/models"
DEST = ROOT / "assets/settlements"
DEST.mkdir(exist_ok=True)
(DEST / "licenses").mkdir(exist_ok=True)
sources = {}
prefabs = []
templates = []
reports = []


def track(p):
    p = p.resolve()
    sources[str(p)] = hashlib.sha256(p.read_bytes()).hexdigest()


def matrix(n):
    if "matrix" in n:
        return np.array(n["matrix"]).reshape(4, 4, order="F")
    x, y, z, w = n.get("rotation", [0, 0, 0, 1])
    m = np.eye(4)
    m[:3, :3] = np.array(
        [
            [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
        ]
    ) @ np.diag(n.get("scale", [1, 1, 1]))
    m[:3, 3] = n.get("translation", [0, 0, 0])
    return m


class Scene:
    def __init__(self, path):
        self.path = path
        track(path)
        self.blob = None
        if path.suffix == ".glb":
            data = path.read_bytes()
            length, _ = struct.unpack_from("<II", data, 12)
            self.doc = json.loads(data[20 : 20 + length])
            self.blob = data[28 + length :]
        else:
            self.doc = json.loads(path.read_text())
        self.buffers = []
        for b in self.doc["buffers"]:
            if "uri" in b:
                p = path.parent / b["uri"]
                track(p)
                self.buffers.append(p.read_bytes())
            else:
                self.buffers.append(self.blob)
        self.nodes = []

        def visit(i, parent, owner):
            n = self.doc["nodes"][i]
            m = parent @ matrix(n)
            if "mesh" in n:
                self.nodes.append((i, n["mesh"], m, owner))
            for c in n.get("children", []):
                visit(c, m, i)

        for i in self.doc["scenes"][self.doc.get("scene", 0)]["nodes"]:
            visit(i, np.eye(4), i)

    def accessor(self, i):
        a = self.doc["accessors"][i]
        v = self.doc["bufferViews"][a["bufferView"]]
        dt = np.dtype(
            {5121: "u1", 5123: "<u2", 5125: "<u4", 5126: "<f4"}[a["componentType"]]
        )
        n = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a["type"]]
        return np.ndarray(
            (a["count"], n),
            dt,
            buffer=self.buffers[v["buffer"]],
            offset=v.get("byteOffset", 0) + a.get("byteOffset", 0),
            strides=(v.get("byteStride", dt.itemsize * n), dt.itemsize),
        ).copy()

    def image(self, ref):
        im = self.doc["images"][self.doc["textures"][ref["index"]]["source"]]
        if "uri" in im:
            from urllib.parse import unquote

            p = self.path.parent / unquote(im["uri"])
            track(p)
            return Image.open(p).convert("RGBA")
        v = self.doc["bufferViews"][im["bufferView"]]
        return Image.open(
            io.BytesIO(
                self.buffers[v["buffer"]][
                    v.get("byteOffset", 0) : v.get("byteOffset", 0) + v["byteLength"]
                ]
            )
        ).convert("RGBA")

    def parts(self, owner=None, scale=1):
        result = []
        for _, mesh, m, group in self.nodes:
            if owner is not None and group != owner:
                continue
            for prim in self.doc["meshes"][mesh]["primitives"]:
                if prim.get("mode", 4) != 4:
                    continue
                a = prim["attributes"]
                p = self.accessor(a["POSITION"])
                p = (np.c_[p, np.ones(len(p))] @ m.T)[:, :3] * scale
                ids = (
                    self.accessor(prim["indices"]).ravel()
                    if "indices" in prim
                    else np.arange(len(p))
                )
                n = (
                    self.accessor(a["NORMAL"]) @ np.linalg.inv(m[:3, :3])
                    if "NORMAL" in a
                    else np.tile([0, 1, 0], (len(p), 1))
                )
                n /= np.maximum(1e-9, np.linalg.norm(n, axis=1))[:, None]
                uv = (
                    self.accessor(a["TEXCOORD_0"])
                    if "TEXCOORD_0" in a
                    else np.zeros((len(p), 2))
                )
                uv[:, 1] = 1 - uv[:, 1]
                material = self.doc.get("materials", [{}])[prim.get("material", 0)]
                pbr = material.get("pbrMetallicRoughness", {})
                meta = {"repeat": True}
                # The current opaque model pass has no refractive glass. Retain
                # window frames/openings instead of drawing translucent panes opaque.
                if (
                    material.get("extensions", {})
                    .get("KHR_materials_transmission", {})
                    .get("transmissionFactor", 0)
                    > 0
                ):
                    continue
                if np.linalg.det(m[:3, :3]) < 0:
                    ids = ids.reshape(-1, 3)[:, [0, 2, 1]].ravel()
                for key, ref in [
                    ("texture", pbr.get("baseColorTexture")),
                    ("normal", material.get("normalTexture")),
                    ("metallicRoughness", pbr.get("metallicRoughnessTexture")),
                ]:
                    if key != "texture" and ref is None:
                        continue
                    img = (
                        self.image(ref)
                        if ref is not None
                        else Image.new("RGBA", (4, 4), (255, 255, 255, 255))
                    )
                    img.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
                    pixels = np.array(img)
                    if key == "texture":
                        factor = np.array(pbr.get("baseColorFactor", [1, 1, 1, 1]))
                        pixels[:, :, :3] = (
                            pixels[:, :, :3] * np.power(factor[:3], 1 / 2.2)
                        ).astype("uint8")
                        pixels[:, :, 3] = (pixels[:, :, 3] * factor[3]).astype("uint8")
                    if key == "normal":
                        pixels[:, :, 1] = 255 - pixels[:, :, 1]
                    digest = hashlib.sha256(pixels.tobytes()).hexdigest()[:16]
                    name = f"settlement-{digest}-{key}.png"
                    if not (OUT / name).exists():
                        Image.fromarray(pixels).save(OUT / name)
                    meta[key] = name
                if material.get("alphaMode") == "MASK":
                    meta["alphaCutoff"] = material.get("alphaCutoff", 0.5)
                result.append((np.c_[p, n, uv][ids], meta))
        return result


def bounds(parts):
    v = np.concatenate([p[:, :3] for p, m in parts])
    return v.min(0), v.max(0)


def move(parts, pos=(0, 0, 0), yaw=0, scale=(1, 1, 1)):
    c, s = math.cos(yaw), math.sin(yaw)
    r = np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])
    result = []
    for a, m in parts:
        v = a.copy()
        v[:, :3] = (v[:, :3] * scale) @ r.T + pos
        v[:, 3:6] = (v[:, 3:6] / scale) @ r.T
        v[:, 3:6] /= np.maximum(1e-9, np.linalg.norm(v[:, 3:6], axis=1))[:, None]
        result.append((v, m))
    return result


def centre(parts):
    lo, hi = bounds(parts)
    return move(parts, (-(lo[0] + hi[0]) / 2, -lo[1], -(lo[2] + hi[2]) / 2))


def runtime(name):
    path = OUT / (name + ".json")
    track(path)
    result = []
    for p in json.loads(path.read_text())["parts"]:
        f = OUT / p["mesh"]
        track(f)
        v = np.frombuffer(f.read_bytes()[8:], dtype="<f4").reshape(-1, 8).copy()
        result.append((v, {k: x for k, x in p.items() if k != "mesh"}))
    return result


def box(lo, hi):
    x, y, z = lo
    X, Y, Z = hi
    v = np.array(
        [
            [x, y, z],
            [X, y, z],
            [X, y, Z],
            [x, y, Z],
            [x, Y, z],
            [X, Y, z],
            [X, Y, Z],
            [x, Y, Z],
        ]
    )
    return v[
        np.array(
            [
                [0, 2, 1],
                [0, 3, 2],
                [4, 5, 6],
                [4, 6, 7],
                [0, 1, 5],
                [0, 5, 4],
                [1, 2, 6],
                [1, 6, 5],
                [2, 3, 7],
                [2, 7, 6],
                [3, 0, 4],
                [3, 4, 7],
            ]
        )
    ]


def export(name, parts, collision=None, role="settlement"):
    groups = {}
    for v, m in parts:
        groups.setdefault(json.dumps(m, sort_keys=True), []).append(v)
    entries = []
    for i, (sig, arrays) in enumerate(sorted(groups.items())):
        v = np.concatenate(arrays).astype("<f4")
        f = f"{name}-{i}.mesh"
        (OUT / f).write_bytes(b"SGM1" + struct.pack("<I", len(v)) + v.tobytes())
        entries.append({"mesh": f, **json.loads(sig)})
    (OUT / (name + ".json")).write_text(
        json.dumps({"version": 1, "parts": entries}, indent=2) + "\n"
    )
    lo, hi = bounds(parts)
    if collision is None:
        collision = np.concatenate([v[:, :3].reshape(-1, 3, 3) for v, m in parts])
    prefabs.append(
        dict(
            id=name,
            title=name,
            group="Generated",
            role=role,
            width=float(hi[0] - lo[0]),
            depth=float(hi[2] - lo[2]),
            height=float(hi[1] - lo[1]),
            ground=role != "door",
            min=lo.tolist(),
            max=hi.tolist(),
            triangles=np.round(collision, 5).tolist(),
        )
    )
    return lo, hi


def template(
    name, parts, palette, kind="building", doors=None, collision=None, entry=None
):
    courses = None
    if name.startswith(("settlement-mega-house-", "settlement-seahill-house-")):
        exterior = abs(bounds(runtime("kit-wall-unevenbrick-straight"))[0][2]) if palette == "mega" else exterior_depth(parts, collision)
        parts, courses = repair_house(parts, collision, exterior)
    lo, hi = export(name, parts, collision)
    if courses is not None:
        prefabs[-1]["baseCourses"] = courses
    templates.append(
        dict(
            id=name,
            palette=palette,
            kind=kind,
            width=round(float(max(abs(lo[0]), abs(hi[0])) * 2), 3),
            depth=round(float(max(abs(lo[2]), abs(hi[2])) * 2), 3),
            height=round(float(hi[1]), 3),
            entrances=entry or [[0, 0, float(lo[2]) - 0.75]],
            doors=doors or [],
            floorHeight=0,
            roofCoverage=kind == "building",
        )
    )


def license(pack):
    d = ROOT / "assets_itch" / pack
    for p in sorted(d.glob("*.txt")):
        if "licen" in p.name.lower():
            track(p)
            shutil.copyfile(
                p, DEST / "licenses" / (pack.replace(" ", "-") + "-" + p.name)
            )


# Eight houses, four from each coherent kit. Doors are separate, never baked closed.
for family in ["mega", "seahill"]:
    if family == "mega":
        wall = runtime("kit-wall-unevenbrick-straight")
        window = runtime("kit-wall-unevenbrick-window-wide-flat") + runtime(
            "kit-window-wide-flat1"
        )
        opening = runtime("kit-wall-unevenbrick-door-flat") + runtime("kit-doorframe-flat-brick")
        floor = runtime("kit-floor-wooddark")
        roof = runtime("kit-roof-roundtiles-6x6")
        gable = runtime("kit-roof-front-brick6")
        door = "kit-door-1-flat"
        doorOffset = -0.53
    else:
        folder = ROOT / "artifacts/settlement-meshes"
        if not (folder / "report.json").exists():
            raise RuntimeError(
                "Run Blender --background --python tools/prepare_settlement_meshes.py first"
            )

        def sea(n):
            # The prepared GLB is the actual geometry input; original source
            # packs may have been archived after the preparation pass.
            track(folder / (n + ".glb"))
            original = ROOT / "assets_itch/Seahill_Towns_Complete/Models/GLB" / (n + ".glb")
            if original.exists(): track(original)
            return centre(Scene(folder / (n + ".glb")).parts(scale=2))

        wall = sea("wall_out_timber")
        window = sea("wall_out_timber_window")
        opening = sea("wall_out_timber_doorway")
        floor = move(sea("floor_in_planks_dark"), (0, -0.25, 0))
        roof = None
        gable = None
        leaf = move(sea("door_round"), scale=(1, 0.735, 1))
        lo, hi = bounds(leaf)
        leaf = move(leaf, ((hi[0] - lo[0]) / 2, 0, 0))
        door = "settlement-seahill-door"
        export(door, leaf, box(*bounds(leaf)), role="door")
        doorOffset = -(hi[0] - lo[0]) / 2
    for index, (w, d) in enumerate([(6, 6), (6, 8), (8, 8), (8, 10)]):
        parts = []
        coll = []
        for x in np.arange(-w / 2 + 1, w / 2, 2):
            for z in np.arange(-d / 2 + 1, d / 2, 2):
                parts += move(floor, (x, 0, z))
        coll.append(box((-w / 2, -0.22, -d / 2), (w / 2, 0, d / 2)))
        entranceX = -w / 2 + 1 + 2 * (int(w / 2) // 2)
        for x in np.arange(-w / 2 + 1, w / 2, 2):
            front = opening if x == entranceX else window
            parts += move(front, (x, 0, -d / 2))
            parts += move(wall, (x, 0, d / 2), math.pi)
            if x == entranceX:
                gap = 0.55 if family == "mega" else max(0.55, -doorOffset)
                for a, b in [(-1, -gap), (gap, 1)]:
                    if b > a:
                        coll.append(
                            box((x + a, 0, -d / 2 - 0.15), (x + b, 3, -d / 2 + 0.15))
                        )
                coll.append(
                    box((x - gap, 2.3, -d / 2 - 0.15), (x + gap, 3, -d / 2 + 0.15))
                )
            else:
                coll.append(box((x - 1, 0, -d / 2 - 0.15), (x + 1, 3, -d / 2 + 0.15)))
        coll.append(box((-w / 2, 0, d / 2 - 0.15), (w / 2, 3, d / 2 + 0.15)))
        for z in np.arange(-d / 2 + 1, d / 2, 2):
            parts += move(wall, (-w / 2, 0, z), math.pi / 2)
            parts += move(wall, (w / 2, 0, z), -math.pi / 2)
        for x in [-w / 2, w / 2]:
            coll.append(box((x - 0.15, 0, -d / 2), (x + 0.15, 3, d / 2)))
        if family == "mega":
            parts += move(roof, (0, 3, 0), scale=(w / 6, 1, d / 6))
            parts += move(gable, (0, 3, -d / 2), scale=(w / 6, 1, 1))
            parts += move(gable, (0, 3, d / 2), math.pi, scale=(w / 6, 1, 1))
            trim = runtime("kit-corner-exterior-brick")
            for x, z, a in [
                (-w / 2, -d / 2, -math.pi / 2),
                (w / 2, -d / 2, math.pi),
                (w / 2, d / 2, math.pi / 2),
                (-w / 2, d / 2, 0),
            ]:
                parts += move(trim, (x, 0, z), a)
        else:
            # Two independent sloping panels meet at a ridge along the Z axis.
            r = sea("roof_clay" if index % 2 == 0 else "roof_slate")
            lo, hi = bounds(r)
            half = move(
                r,
                scale=(
                    (d + 0.5) / (hi[0] - lo[0]),
                    1,
                    (w / 2 + 0.25) / (hi[2] - lo[2]),
                ),
            )
            parts += move(half, (-w / 4, 3, 0), math.pi / 2) + move(
                half, (w / 4, 3, 0), -math.pi / 2
            )
            g = sea("attic_gable_timber")
            glo, ghi = bounds(g)
            g = move(
                g, scale=(w / (ghi[0] - glo[0]), (hi[1] - lo[1]) / (ghi[1] - glo[1]), 1)
            )
            parts += move(g, (0, 3, -d / 2)) + move(g, (0, 3, d / 2), math.pi)
            ridge = sea("roof_ridge_clay" if index % 2 == 0 else "roof_ridge_slate")
            rlo, rhi = bounds(ridge)
            parts += move(
                ridge,
                (0, 3 + hi[1] - lo[1] - 0.12, 0),
                math.pi / 2,
                scale=(d / (rhi[0] - rlo[0]), 1, 1),
            )
            post = sea("post_timber")
            for x in [-w / 2, w / 2]:
                for z in [-d / 2, d / 2]:
                    parts += move(post, (x, 0, z))
        coll.append(box((-w / 2, 2.96, -d / 2), (w / 2, 3.08, d / 2)))
        # Foundations are an actual visible stone skirt, embedded below the graded plot.
        foundation = runtime("kit-stone-foundation")
        flo, fhi = bounds(foundation)
        parts += move(
            foundation,
            (0, -0.12, 0),
            scale=(w / (fhi[0] - flo[0]), 1, d / (fhi[2] - flo[2])),
        )
        template(
            f"settlement-{family}-house-{index+1}",
            parts,
            family,
            doors=[
                dict(
                    model=door, position=[entranceX + doorOffset, 0, -d / 2], rotation=0
                )
            ],
            collision=np.concatenate(coll),
            entry=[[entranceX, 0, -d / 2 - 0.75]],
        )
    license(
        "Medieval Village MegaKit[Standard]"
        if family == "mega"
        else "Seahill_Towns_Complete"
    )

# Targeted re-export restores original source faces before applying the current
# foundation rule. Preserve unrelated generated assets and metadata.
if '--houses-only' in sys.argv:
    for filename, generated in [('prefabs.json', prefabs), ('templates.json', templates)]:
        path = DEST / filename
        current = json.loads(path.read_text())
        replacements = {entry['id']: entry for entry in generated}
        merged = [replacements.pop(entry['id'], entry) for entry in current]
        merged.extend(replacements.values())
        path.write_text(json.dumps(merged, separators=(',', ':')) + '\n')
    (DEST / 'foundation-refresh.json').write_text(json.dumps(dict(
        tool='tools/import_settlements.py --houses-only',
        geometry='flush wall-footprint foundations v2',
        sources=[dict(path=p, sha256=h) for p,h in sorted(sources.items())]), indent=2) + '\n')
    from import_masonry_materials import install as install_masonry
    install_masonry()
    print('Restored and exported', len(templates), 'houses from source.', flush=True)
    raise SystemExit(0)

# Modern sets contain grouped whole buildings, not one building per material.
for pack in [
    "brownstone_building_set",
    "downtown-buildings-set",
    "industrial_buildings_set",
]:
    scene = Scene(ROOT / "assets_itch" / pack / "scene.gltf")
    license(pack)
    for i, owner in enumerate(sorted(set(n[3] for n in scene.nodes))):
        parts = centre(scene.parts(owner))
        lo, hi = bounds(parts)
        size = hi - lo
        # Oversize factory campuses are retained as reported exclusions rather than
        # shrunk to dollhouse scale to make the lot solver accept them.
        if max(size[0], size[2]) > 65 or size[1] > 110:
            reports.append(
                dict(
                    pack=pack,
                    group=owner,
                    excluded="footprint exceeds compact-city lot",
                    size=size.tolist(),
                )
            )
            continue
        kind = "industrial" if pack.startswith("industrial") else "building"
        template(
            f'settlement-{pack.replace("_","-")}-{i:02}',
            parts,
            "modern",
            kind,
            collision=box(lo, hi),
        )

# Desert compounds: open courtyards, human-sized portal, four connected variations.
desert = {}
for pack in [
    "desert_fortress_walls",
    "desert_fortress_floor",
    "desert_fortress_walkways",
    "desert__fortress__towers",
]:
    s = Scene(ROOT / "assets_itch" / pack / "scene.gltf")
    license(pack)
    for _, mesh, _, owner in s.nodes:
        name = s.doc["meshes"][mesh].get("name", "")
        desert[name] = centre(s.parts(owner))
wall = next(v for k, v in desert.items() if "Wall_Tall_01" in k)
lo, hi = bounds(wall)
wall = move(wall, scale=(8 / (hi[0] - lo[0]), 4 / (hi[1] - lo[1]), 1))
tower = next(v for k, v in desert.items() if "Tower_Medium_01_0" in k)
for i, (w, d) in enumerate([(16, 16), (24, 16), (24, 24), (32, 24)]):
    parts = []
    coll = []

    def solid_wall(position, yaw):
        placed = move(wall, position, yaw)
        parts.extend(placed)
        coll.append(box(*bounds(placed)))

    for x in np.arange(-w / 2 + 4, w / 2, 8):
        solid_wall((x, 0, d / 2), math.pi)
        if abs(x) > 4:
            solid_wall((x, 0, -d / 2), 0)
    for z in np.arange(-d / 2 + 4, d / 2, 8):
        solid_wall((-w / 2, 0, z), math.pi / 2)
        solid_wall((w / 2, 0, z), -math.pi / 2)
    if i > 0:
        tlo, thi = bounds(tower)
        t = move(tower, scale=(0.35, 0.35, 0.35))
        placed = move(t, (w / 2 - 2, 0, d / 2 - 2))
        parts += placed
        coll.extend(v[:, :3].reshape(-1, 3, 3) for v, m in placed)
    template(
        f"settlement-desert-compound-{i+1}",
        parts,
        "desert",
        entry=[[0, 0, -d / 2 - 1]],
        collision=np.concatenate(coll),
    )
    templates[-1]["roofCoverage"] = False

# A single combined ruin source is split into welded connected components. This
# preserves coherent masonry sections rather than deleting arbitrary triangles.
s = Scene(ROOT / "assets_itch/ancient_ruins_modular/scene.gltf")
license("ancient_ruins_modular")
raw = s.parts(scale=0.01)
v = np.concatenate([a for a, m in raw])
p = v[:, :3]
_, inverse = np.unique(np.round(p, 5), axis=0, return_inverse=True)
parent = list(range(max(inverse) + 1))


def find(x):
    while parent[x] != x:
        parent[x] = parent[parent[x]]
        x = parent[x]
    return x


for a, b, c in inverse.reshape(-1, 3):
    parent[find(b)] = find(a)
    parent[find(c)] = find(a)
groups = {}
for i, tri in enumerate(inverse.reshape(-1, 3)):
    groups.setdefault(find(tri[0]), []).append(i)
fragments = [
    centre([(v.reshape(-1, 3, 8)[ids].reshape(-1, 8), raw[0][1])])
    for ids in sorted(groups.values(), key=len, reverse=True)
    if len(ids) > 40
]
if not fragments:
    raise RuntimeError("No coherent ruins components")
for palette in ["mega", "seahill", "desert", "modern"]:
    for i in range(3):
        parts = []
        if palette == "desert":
            for k in range(2 + i):
                parts += move(
                    fragments[k % len(fragments)],
                    ((k % 2) * 9 - 4.5, 0, (k // 2) * 8),
                    math.pi / 2 * (k % 2),
                )
        else:
            if palette == "mega":
                section = runtime("kit-wall-unevenbrick-straight")
                base = runtime("kit-floor-wooddark")
            elif palette == "seahill":
                section = sea("wall_out_stone")
                base = move(sea("floor_in_planks_dark"), (0, -0.25, 0))
            else:
                section = runtime("fort-solid-wall")
                base = runtime("kit-floor-brick")
            lo, hi = bounds(section)
            section = move(
                centre(section), scale=(2 / (hi[0] - lo[0]), 3 / (hi[1] - lo[1]), 1)
            )
            for x in [-3, -1, 1, 3]:
                for z in [-3, -1, 1, 3]:
                    parts += move(base, (x, 0, z))
            # Connected L-shaped remnants, an open front entrance, and grounded
            # collapsed wall sections. Missing roofs/wall bays are authored groups.
            for k in range(2 + i):
                parts += move(section, (-3 + 2 * k, 0, 4))
            for k in range(2 + i):
                parts += move(section, (-4, 0, -3 + 2 * k), math.pi / 2)
            fallen = []
            for a, m in section:
                v = a.copy()
                v[:, [1, 2]] = v[:, [2, 1]]
                v[:, [4, 5]] = v[:, [5, 4]]
                v = v.reshape(-1, 3, 8)[:, [0, 2, 1], :].reshape(-1, 8)
                fallen.append((v, m))
            parts += move(centre(fallen), (2, 0, 1), i * 0.7)
        template(f"settlement-{palette}-ruin-{i+1}", centre(parts), palette, "ruin")

for pack in [
    "ancient_egypt_modular_museum",
    "MedievalPalace",
    "EurasianTowers",
    "19th-century-obelisc-monument",
    "neogothical pier architectural",
]:
    scene = Scene(ROOT / "assets_itch" / pack / "scene.gltf")
    license(pack)
    # Named parent groups preserve multi-material towers and colonnades.
    for i, owner in enumerate(sorted(set(n[3] for n in scene.nodes))):
        parts = centre(scene.parts(owner))
        lo, hi = bounds(parts)
        if max(hi - lo) > 45:
            reports.append(
                dict(
                    pack=pack,
                    group=owner,
                    excluded="landmark requires authored scale review",
                )
            )
            continue
        palette = "desert" if "egypt" in pack else "mega"
        template(
            f'settlement-landmark-{pack.replace(" ","-").lower()}-{i}',
            parts,
            palette,
            "landmark",
        )

prefabs = generate_doors(prefabs, OUT)
(DEST / "prefabs.json").write_text(json.dumps(prefabs, separators=(",", ":")) + "\n")
(DEST / "templates.json").write_text(json.dumps(templates, indent=2) + "\n")
(DEST / "provenance.json").write_text(
    json.dumps(
        dict(
            version=1,
            tool="tools/import_settlements.py",
            mesh_preparation=json.loads(
                (ROOT / "artifacts/settlement-meshes/report.json").read_text()
            ),
            conversion="World node transforms, Y-up metres, base-centred pivots; UV V and normal green flipped; 2048px shared textures; separate doors; source groups retained",
            sources=[dict(path=p, sha256=h) for p, h in sorted(sources.items())],
            exclusions=reports,
        ),
        indent=2,
    )
    + "\n"
)
print(
    "Settlement assets:",
    len(prefabs),
    "prefabs,",
    len(templates),
    "templates;",
    len(reports),
    "excluded oversize groups",
)

# Keep generated houses on the same scanned masonry as the editable kit.
from import_masonry_materials import install as install_masonry
install_masonry()
