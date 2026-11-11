"""Rebuild DHD albedo and per-key emission from source materials and source UVs.
Run in Blender against the read-only assembled blend, with -- --output assets/models.
Original baked atlases remain inputs; repeated runs never compound the colour change.
"""

import argparse, bpy, hashlib, json, struct, sys
import numpy as np
from pathlib import Path
from mathutils import Vector

parser = argparse.ArgumentParser()
parser.add_argument("--output", required=True)
root = Path(parser.parse_args(sys.argv[sys.argv.index("--") + 1 :]).output).resolve()
manifest = json.loads((root / "dhd.json").read_text())
source_dir = (
    Path(bpy.data.filepath).parent.parent / "models/Technology/Ancient/DHD/Milkyway"
)
glyph_path = source_dir / "T_DHD_Touhes_GyphBorder.png"
glyph = bpy.data.images.load(str(glyph_path), check_existing=False)
glyph.colorspace_settings.name = "Non-Color"
gw, gh = glyph.size
gp = np.array(glyph.pixels[:], dtype=np.float32).reshape(gh, gw, 4)[:, :, 0]
report = {
    "source_sha256": hashlib.sha256(Path(bpy.data.filepath).read_bytes()).hexdigest(),
    "glyph_mask_sha256": hashlib.sha256(glyph_path.read_bytes()).hexdigest(),
    "method": "source material geometry plus source glyph-mask UVs, mapped to existing runtime atlases",
    "parts": [],
}


def save(name, pixels, linear):
    h, w, _ = pixels.shape
    image = bpy.data.images.new(name, width=w, height=h, alpha=False)
    image.colorspace_settings.name = "Non-Color" if linear else "sRGB"
    image.pixels.foreach_set(pixels.ravel())
    image.filepath_raw = str(root / name)
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)


orb_path = source_dir / "T_DHD_BigButton_E.png"
orb_image = bpy.data.images.load(str(orb_path), check_existing=False)
orb_image.colorspace_settings.name = "Non-Color"
ow, oh = orb_image.size
op = np.array(orb_image.pixels[:], dtype=np.float32).reshape(oh, ow, 4)[:, :, 0]
report["orb_source_sha256"] = hashlib.sha256(orb_path.read_bytes()).hexdigest()
report["orb_finish"] = (
    "static model-space mottling, 88/190 cycles per model metre; soft red mottling with restrained pale pink highlights"
)


def noise3(p):
    cell = np.floor(p)
    f = p - cell
    f = f * f * (3 - 2 * f)
    result = np.zeros(len(p))
    for x in (0, 1):
        for y in (0, 1):
            for z in (0, 1):
                h = (
                    np.sin((cell + [x, y, z]) @ np.array([127.1, 311.7, 74.7]))
                    * 43758.5453
                )
                value = h - np.floor(h)
                weight = np.prod(np.where(np.array([x, y, z]), f, 1 - f), axis=1)
                result += value * weight
    return result


for part in manifest["parts"]:
    orb = part["name"] == "SM_DHD_Dome"
    if not orb and not part["name"].startswith("SM_Key_"):
        continue
    key = 0 if orb else int(part["name"].split("_")[-1])
    source = bpy.data.objects[part["name"]]
    mesh = source.data
    mesh.calc_loop_triangles()
    raw = (root / part["mesh"]).read_bytes()
    vertices = np.array(list(struct.iter_unpack("<8f", raw[8:])))
    assert len(vertices) == len(mesh.loop_triangles) * 3
    original = part.setdefault("sourceTexture", part["texture"])
    image = bpy.data.images.load(str(root / original), check_existing=False)
    w, h = image.size
    albedo = np.array(image.pixels[:], dtype=np.float32).reshape(h, w, 4)
    emission = np.zeros((h, w), dtype=np.float32)
    marking = np.zeros((h, w), dtype=np.float32)
    occupied = np.zeros((h, w), dtype=bool)
    for index, tri in enumerate(mesh.loop_triangles):
        vs = vertices[index * 3 : index * 3 + 3]
        for li, v in zip(tri.loops, vs):
            p = source.matrix_world @ mesh.vertices[
                mesh.loops[li].vertex_index
            ].co - Vector((3, -2.5, 0))
            assert (
                Vector((p.x, p.z, -p.y)) - Vector(v[:3])
            ).length < 0.0001, "Source/runtime placement mismatch"
        uv = vs[:, 6:8] * [w, h]
        a, b, c = uv
        ab = b - a
        ac = c - a
        area = ab[0] * ac[1] - ab[1] * ac[0]
        if abs(area) < 1e-8:
            continue
        lo = np.maximum(0, np.floor(uv.min(axis=0)).astype(int))
        hi = np.minimum([w, h], np.ceil(uv.max(axis=0)).astype(int))
        yy, xx = np.mgrid[lo[1] : hi[1], lo[0] : hi[0]]
        dx = xx + 0.5 - a[0]
        dy = yy + 0.5 - a[1]
        u = (dx * ac[1] - dy * ac[0]) / area
        v = (ab[0] * dy - ab[1] * dx) / area
        inside = (u >= 0) & (v >= 0) & (u + v <= 1)
        ys = yy[inside]
        xs = xx[inside]
        occupied[ys, xs] = True
        mat = source.material_slots[tri.material_index].material.name
        if orb and mat == "MI_DHD_Dome":
            src = np.array([mesh.uv_layers.active.data[li].uv[:] for li in tri.loops])
            sample = (
                src[0]
                + u[inside, None] * (src[1] - src[0])
                + v[inside, None] * (src[2] - src[0])
            )
            ox = np.clip((sample[:, 0] * ow).astype(int), 0, ow - 1)
            oy = np.clip((sample[:, 1] * oh).astype(int), 0, oh - 1)
            # Fixed physical scale, continuous across the original UV islands.
            p = (
                vs[0, :3]
                + u[inside, None] * (vs[1, :3] - vs[0, :3])
                + v[inside, None] * (vs[2, :3] - vs[0, :3])
            )
            warp = noise3(p * 28 + 11)[:, None] * 0.006
            flecks = 0.65 * noise3((p + warp) * 88) + 0.35 * noise3(p * 190 + 7)
            hot = np.clip((flecks - 0.10) / 0.80, 0, 1)
            hot = hot * hot * (3 - 2 * hot)
            emission[ys, xs] = (0.25 + 0.75 * op[oy, ox]) * (0.20 + 0.80 * hot)
        elif mat == "MI_Glyph_NoGlow":
            marking[ys, xs] = 1
            emission[ys, xs] = 1
        elif mat == "MI_Touches_Base":
            src = np.array([mesh.uv_layers.active.data[li].uv[:] for li in tri.loops])
            sample = (
                src[0]
                + u[inside, None] * (src[1] - src[0])
                + v[inside, None] * (src[2] - src[0])
            )
            gx = np.clip((sample[:, 0] * gw).astype(int), 0, gw - 1)
            gy = np.clip((sample[:, 1] * gh).astype(int), 0, gh - 1)
            marking[ys, xs] = gp[gy, gx]
            emission[ys, xs] = gp[gy, gx]
        elif mat == "MI_Touches_NoGlow" and vs[:, 4].mean() > 0.35:
            # Raised upper key rim, not the surrounding housing or underside.
            emission[ys, xs] = 0.55
    # Pad the recolouring as well as emission: original bright bake margins must
    # not bleed back into fine glyph edges through bilinear/mipmap sampling.
    for _ in range(4):
        neighbours = np.maximum.reduce(
            [
                np.roll(marking, (dy, dx), (0, 1))
                for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0))
            ]
        )
        marking = np.where(occupied, marking, np.maximum(marking, neighbours))
    # A warm, subdued metal marking; preserve a little of the baked surface wear.
    target = np.array([0.105, 0.093, 0.073], dtype=np.float32)
    albedo[:, :, :3] = (
        albedo[:, :, :3] * (1 - marking[:, :, None] * 0.92)
        + target * marking[:, :, None] * 0.92
    )
    # Pad only unoccupied atlas texels, never neighbouring metal triangles.
    for _ in range(3):
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            moved = np.roll(emission, (dy, dx), (0, 1))
            emission = np.where(occupied, emission, np.maximum(emission, moved))
    rgba = np.ones((h, w, 4), dtype=np.float32)
    rgba[:, :, :3] = emission[:, :, None]
    stem = Path(part["mesh"]).stem
    if orb:
        # Cooler bright patches within a red body, rather than a yellow wash.
        rgba[:, :, 1] = emission**1.15 * 0.38
        rgba[:, :, 2] = emission**1.25 * 0.27
        part.update(
            emissionTexture=stem + "-emission.png",
            emissionColor=[1, 1, 1],
            emissionIntensity=6.5,
            dhdOrb=True,
        )
    else:
        part.update(
            texture=stem + "-weathered.png",
            emissionTexture=stem + "-emission.png",
            emissionColor=[1, 0.30, 0.055],
            emissionIntensity=2.5,
            dhdKeyId=key,
            hidden=(key == 13),
        )
        save(part["texture"], albedo, False)
    save(part["emissionTexture"], rgba, True)
    report["parts"].append(
        {
            "key": key,
            "mesh": part["mesh"],
            "original_sha256": hashlib.sha256(
                (root / original).read_bytes()
            ).hexdigest(),
            "albedo_sha256": hashlib.sha256(
                (root / part["texture"]).read_bytes()
            ).hexdigest(),
            "emission_sha256": hashlib.sha256(
                (root / part["emissionTexture"]).read_bytes()
            ).hexdigest(),
        }
    )
    bpy.data.images.remove(image)
    print("DHD", key, "emission pixels", int((emission > 0).sum()), flush=True)
(root / "dhd.json").write_text(json.dumps(manifest, indent=2) + "\n")
(root / "dhd-material-provenance.json").write_text(json.dumps(report, indent=2) + "\n")
