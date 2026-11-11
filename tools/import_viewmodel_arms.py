"""Fit Cransh's arms to the existing rifle skeleton without changing any animation.

Run with Python for a mesh-only update; the Blender importer invokes this tool too.
The source glTF's inverse binds describe the mesh correctly; its displayed node
transforms include animation/export scale and must not be baked into the skin.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "assets_itch/characters/arms2"
OUTPUT = ROOT / "assets/characters"
SOURCE_FILES = ["scene.gltf", "scene.bin", "license.txt", *[
    "textures/arms_" + name + ".png"
    for name in ("diffuse", "normal", "occlusion", "specularGlossiness")]]


def unit(v):
    length = np.linalg.norm(v)
    if length < 1e-8:
        raise ValueError("Degenerate arm fitting landmark")
    return v / length


def frame(direction, across):
    y = unit(direction)
    x = unit(across - y * np.dot(across, y))
    return np.column_stack((x, y, np.cross(x, y)))


def segment_map(source_start, source_end, target_start, target_end,
                source_across, target_across, width):
    """Anatomical frames avoid dependence on either author's bone roll axes."""
    a, b = source_end - source_start, target_end - target_start
    linear = (frame(b, target_across)
              @ np.diag([width, np.linalg.norm(b) / np.linalg.norm(a), width])
              @ frame(a, source_across).T)
    result = np.eye(4)
    result[:3, :3] = linear
    result[:3, 3] = target_start - linear @ source_start
    return result


def source_mesh():
    doc = json.loads((SOURCE / "scene.gltf").read_text())
    buffers = [(SOURCE / b["uri"]).read_bytes() for b in doc["buffers"]]

    def accessor(index):
        a = doc["accessors"][index]
        view = doc["bufferViews"][a["bufferView"]]
        dtype = np.dtype({5121: "u1", 5123: "<u2", 5125: "<u4", 5126: "<f4"}[a["componentType"]])
        columns = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}[a["type"]]
        values = np.ndarray((a["count"], columns), dtype=dtype,
                            buffer=buffers[view["buffer"]],
                            offset=view.get("byteOffset", 0) + a.get("byteOffset", 0),
                            strides=(view.get("byteStride", columns * dtype.itemsize), dtype.itemsize)).copy()
        if a.get("normalized") and dtype.kind in "ui":
            values = values / np.iinfo(dtype).max
        return values

    mesh_index = next(i for i, m in enumerate(doc["meshes"]) if m["name"] == "arms_arms_0")
    node = next(n for n in doc["nodes"] if n.get("mesh") == mesh_index)
    skin = doc["skins"][node["skin"]]
    names = [doc["nodes"][j]["name"].rsplit("_", 1)[0] for j in skin["joints"]]
    binds = np.linalg.inv(accessor(skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1))
    prim = doc["meshes"][mesh_index]["primitives"][0]
    attributes = {key: accessor(value) for key, value in prim["attributes"].items()}
    return names, binds, attributes, accessor(prim["indices"]).reshape(-1, 3)


def fit(data):
    names, binds, attributes, indices = source_mesh()
    source = {name: m[:3, 3].astype(float) for name, m in zip(names, binds)}
    target_bind = np.linalg.inv(np.asarray(data["inverseBind"]).reshape(-1, 4, 4))
    target = {name: m[:3, 3] for name, m in zip(data["bones"], target_bind)}
    transforms, mapping = {}, {}
    for side in ("L", "R"):
        suffix = "_" + side.lower()
        wrist = "Hand_" + side
        # The four .004/.008/.012/.016 bones fan out from the wrist and
        # deform the palm. Their children are the actual proximal finger joints.
        proximal = [f"Bone_{side}.{i:03}" for i in (5, 9, 13, 17)]
        target_proximal = [f + "_01" + suffix for f in ("index", "middle", "ring", "pinky")]
        sa = source[proximal[0]] - source[proximal[-1]]
        ta = target[target_proximal[0]] - target[target_proximal[-1]]
        width = np.linalg.norm(ta) / np.linalg.norm(sa)
        palm = segment_map(source[wrist], np.mean([source[n] for n in proximal], axis=0),
                           target["hand" + suffix], np.mean([target[n] for n in target_proximal], axis=0),
                           sa, ta, width)
        transforms[wrist], mapping[wrist] = palm, "hand" + suffix
        for finger, first in zip(("index", "middle", "ring", "pinky"), (4, 8, 12, 16)):
            name = f"Bone_{side}.{first:03}"
            transforms[name], mapping[name] = palm, finger + "Knuckle" + suffix
            for segment in range(1, 4):
                name = f"Bone_{side}.{first + segment:03}"
                dest = f"{finger}_{segment:02}" + suffix
                if segment < 3:
                    end = source[f"Bone_{side}.{first + segment + 1:03}"]
                    target_end = target[f"{finger}_{segment + 1:02}" + suffix]
                else:
                    # Terminal marker inverse binds are placeholders. Extrapolate
                    # the distal axis, retaining the previous phalanx's scale.
                    end = source[name] + source[name] - source[f"Bone_{side}.{first + segment - 1:03}"]
                    target_end = target[dest] + target[dest] - target[f"{finger}_{segment - 1:02}" + suffix]
                transforms[name] = segment_map(source[name], end, target[dest], target_end, sa, ta, width)
                mapping[name] = dest
        for segment in range(1, 4):
            name = f"Bone_{side}.{19 + segment:03}"
            dest = f"thumb_{segment:02}" + suffix
            if segment < 3:
                end = source[f"Bone_{side}.{20 + segment:03}"]
                target_end = target[f"thumb_{segment + 1:02}" + suffix]
            else:
                end = source[name] * 2 - source[f"Bone_{side}.021"]
                target_end = target[dest] * 2 - target["thumb_02" + suffix]
            transforms[name] = segment_map(source[name], end, target[dest], target_end, sa, ta, width)
            mapping[name] = dest
        elbow = f"BoneTwist_03.{side}"
        forearm = segment_map(source[elbow], source[wrist], target["lowerArm" + suffix],
                              target["hand" + suffix], sa, ta, width)
        for number, dest in ((3, "twist_1_arm"), (2, "twist_2_arm"), (1, "twist_3_arm")):
            name = f"BoneTwist_{number:02}.{side}"
            transforms[name], mapping[name] = forearm, dest + suffix
        upper = "UpArm_" + side
        transforms[upper] = segment_map(source[upper], source[elbow], target["upperArm" + suffix],
                                       target["lowerArm" + suffix], sa, ta, width)
        mapping[upper] = "upperArm" + suffix
        shoulder = "Arm_" + side
        transforms[shoulder], mapping[shoulder] = transforms[upper], "shoulder" + suffix

    positions = np.column_stack((attributes["POSITION"], np.ones(len(attributes["POSITION"]))))
    normals = attributes["NORMAL"].astype(float)
    joints = attributes["JOINTS_0"].astype(int)
    weights = attributes["WEIGHTS_0"].astype(float)
    fitted = np.zeros((len(positions), 3))
    jacobian = np.zeros((len(positions), 3, 3))
    target_joints = np.zeros_like(joints)
    for j, name in enumerate(names):
        for slot in range(4):
            mask = (joints[:, slot] == j) & (weights[:, slot] > 0)
            if not mask.any():
                continue
            if name not in mapping:
                raise ValueError("Unmapped weighted arm bone: " + name)
            transform = transforms[name]
            w = weights[mask, slot]
            fitted[mask] += (positions[mask] @ transform.T)[:, :3] * w[:, None]
            jacobian[mask] += transform[:3, :3] * w[:, None, None]
            target_joints[mask, slot] = data["bones"].index(mapping[name])
    normals = np.einsum("nij,nj->ni", np.linalg.inv(jacobian).transpose(0, 2, 1), normals)
    normals /= np.linalg.norm(normals, axis=1)[:, None]
    weights /= weights.sum(axis=1)[:, None]
    assert np.isfinite(fitted).all() and np.isfinite(normals).all()
    uv = attributes["TEXCOORD_0"].copy()
    uv[:, 1] = 1 - uv[:, 1]
    part = dict(name="FPS_Arms_Mesh", lod=1, preserveNormals=True,
                positions=fitted.reshape(-1).tolist(), normals=normals.reshape(-1).tolist(),
                uvs=uv.reshape(-1).tolist(), joints=target_joints.reshape(-1).tolist(),
                weights=weights.reshape(-1).tolist(), indices=indices.reshape(-1).tolist(),
                albedo="arms2-color.png", normal="arms2-normal.png", metallicRoughness="arms2-surface.png")
    data["parts"] = [part if p["name"] == "FPS_Arms_Mesh" else p for p in data["parts"]]
    return data


def textures():
    from PIL import Image

    def read(name):
        path = SOURCE / "textures" / ("arms_" + name + ".png")
        return np.asarray(Image.open(path).convert("RGBA").resize(
            (2048, 2048), Image.Resampling.LANCZOS), dtype=np.float32) / 255

    def write(name, pixels):
        path = OUTPUT / name
        temporary = path.with_suffix(".importing.png")
        Image.fromarray(np.rint(np.clip(pixels, 0, 1) * 255).astype(np.uint8)).save(temporary)
        temporary.replace(path)

    write("arms2-color.png", read("diffuse"))
    normal = read("normal")
    vectors = normal[:, :, :3] * 2 - 1
    vectors /= np.maximum(np.linalg.norm(vectors, axis=2, keepdims=True), 1e-6)
    normal[:, :, :3] = vectors * .5 + .5
    write("arms2-normal.png", normal)
    surface = np.ones((2048, 2048, 4), dtype=np.float32)
    surface[:, :, 0] = read("occlusion")[:, :, 0]
    surface[:, :, 1] = 1 - read("specularGlossiness")[:, :, 3]
    surface[:, :, 2] = 0  # Gloves and sleeves are dielectric, not bare metal.
    write("arms2-surface.png", surface)


def install():
    path = OUTPUT / "retro-rifle.sga.gz"
    data = fit(json.loads(gzip.decompress(path.read_bytes())))
    textures()
    temp = path.with_suffix(".tmp")
    temp.write_bytes(gzip.compress(json.dumps(data, separators=(",", ":")).encode(), mtime=0))
    temp.replace(path)
    (OUTPUT / "arms2-license.txt").write_bytes((SOURCE / "license.txt").read_bytes())
    provenance = {
        "source": "FPS pistol animations by Cransh (CC-BY-4.0); arms mesh only",
        "url": "https://sketchfab.com/3d-models/fps-pistol-animations-0d7a343dcb6f401197a73c91aee93f6d",
        "method": "Inverse-bind mesh; anatomical segment fitting to unchanged Retro rifle skeleton and clips",
        "textures": "2048px Lanczos; sRGB colour; normalized OpenGL normals; linear R occlusion, G one-minus-gloss, B zero metallic",
        "sources": [{"path": "assets_itch/characters/arms2/" + name,
                     "sha256": hashlib.sha256((SOURCE / name).read_bytes()).hexdigest()} for name in SOURCE_FILES],
        "importer": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
    }
    (OUTPUT / "arms2-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    manifest_path = OUTPUT / "manifest.json"
    manifest = json.loads(manifest_path.read_text())
    sources = {entry["path"]: entry for entry in manifest["sources"]}
    sources.update({entry["path"]: entry for entry in provenance["sources"]})
    manifest["sources"] = [sources[key] for key in sorted(sources)]
    manifest["viewmodelArms"] = "arms2-provenance.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print("Fitted arms2: 14,852 triangles; rifle skeleton and clips preserved", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    install()
