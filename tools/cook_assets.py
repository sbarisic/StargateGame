"""Reproducible SGA1 model package. Requires Pillow, numpy, Brotli (see requirements-assets.txt).
Sources are never modified. Mips are independently compressed and addressable.
"""

import argparse, hashlib, json, os, struct, tempfile, gzip
from pathlib import Path
import brotli
import numpy as np
from PIL import Image


def encode(value):
    return json.dumps(
        value, sort_keys=True, separators=(",", ":"), allow_nan=False
    ).encode()


def srgb_linear(x):
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def linear_srgb(x):
    return np.where(
        x <= 0.0031308, x * 12.92, 1.055 * np.maximum(x, 0) ** (1 / 2.4) - 0.055
    )


def dilate(pixels):
    result = pixels.copy()
    valid = result[:, :, 3] > 0
    # RGB only; alpha coverage is never expanded.
    for _ in range(8):
        total = np.zeros_like(result[:, :, :3], dtype=np.float32)
        weights = np.zeros(valid.shape, dtype=np.float32)
        for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            shifted = np.roll(valid, (dz, dx), (0, 1))
            rgb = np.roll(result[:, :, :3], (dz, dx), (0, 1))
            if dx == 1:
                shifted[:, 0] = False
            if dx == -1:
                shifted[:, -1] = False
            if dz == 1:
                shifted[0, :] = False
            if dz == -1:
                shifted[-1, :] = False
            total += rgb * shifted[:, :, None]
            weights += shifted
        fill = (~valid) & (weights > 0)
        result[:, :, :3][fill] = (total[fill] / weights[fill, None]).astype(np.uint8)
        valid |= fill
    return result


def mip_chain(pixels, kind, cutoff):
    pixels = dilate(pixels) if cutoff > 0 else pixels
    target = float(np.mean(pixels[:, :, 3] / 255.0 >= cutoff)) if cutoff > 0 else 0
    linear = pixels.astype(np.float32) / 255.0
    if kind == "color":
        linear[:, :, :3] = srgb_linear(linear[:, :, :3])
    while True:
        output = linear.copy()
        if cutoff > 0 and 0 < target < 1:
            # Preserve the fraction passing the shader cutoff; never compound the adjustment.
            alpha = output[:, :, 3]
            coverage = float(np.mean(alpha >= cutoff))
            # Leave opaque texels opaque when coverage already matches. Scaling a
            # binary base image down to the cutoff makes bilinear leaf edges vanish.
            if abs(coverage - target) > max(1 / alpha.size, 0.002):
                lo, hi = (1.0, 16.0) if coverage < target else (0.0, 1.0)
                best = 1.0
                error = abs(coverage - target)
                for _ in range(24):
                    scale = (lo + hi) * 0.5
                    actual = float(np.mean(alpha * scale >= cutoff))
                    difference = abs(actual - target)
                    if difference < error:
                        best, error = scale, difference
                    if actual < target:
                        lo = scale
                    else:
                        hi = scale
                output[:, :, 3] = np.clip(alpha * best, 0, 1)
        if kind == "normal":
            n = output[:, :, :3] * 2 - 1
            n /= np.maximum(np.linalg.norm(n, axis=2, keepdims=True), 1e-6)
            output[:, :, :3] = n * 0.5 + 0.5
        if kind == "color":
            output[:, :, :3] = linear_srgb(output[:, :, :3])
        yield np.clip(np.rint(output * 255), 0, 255).astype(np.uint8)
        h, w = linear.shape[:2]
        if max(w, h) == 1:
            break
        linear = np.stack(
            [
                np.asarray(
                    Image.fromarray(linear[:, :, i], mode="F").resize(
                        (max(1, w // 2), max(1, h // 2)), Image.Resampling.BOX
                    )
                )
                for i in range(4)
            ],
            axis=2,
        )


class Package:
    def __init__(self, path):
        self.path = path
        self.stream = open(path, "w+b")
        self.stream.write(bytes(48))
        self.index = {}
        self.content = {}
        self.previous = None
        self.old = {}
        self.old_content = {}
        published = path.with_suffix("")
        if published.exists():
            self.previous = open(published, "rb")
            header = self.previous.read(48)
            magic, offset, length = struct.unpack("<4sqi", header[:16])
            if magic == b"SGA1":
                self.previous.seek(offset)
                index = self.previous.read(length)
                if hashlib.sha256(index).digest() == header[16:]:
                    self.old = json.loads(index)
                    self.old_content = {e["Hash"]: e for e in self.old.values()}

    def reuse(self, key):
        if key not in self.old:
            return False
        entry = dict(self.old[key])
        self.previous.seek(entry["Offset"])
        stored = self.previous.read(entry["Stored"])
        entry["Offset"] = self.stream.tell()
        self.stream.write(stored)
        self.index[key] = entry
        self.content[entry["Hash"]] = entry
        return True

    def add(self, key, data):
        if key in self.index:
            return
        digest = hashlib.sha256(data).hexdigest()
        if digest in self.content:
            self.index[key] = self.content[digest]
            return
        if digest in self.old_content:
            old = self.old_content[digest]
            self.previous.seek(old["Offset"])
            stored = self.previous.read(old["Stored"])
            entry = dict(old)
            entry["Offset"] = self.stream.tell()
            self.stream.write(stored)
            self.index[key] = entry
            self.content[digest] = entry
            return
        compressed = brotli.compress(data, quality=4)
        use = len(compressed) < len(data)
        stored = compressed if use else data
        entry = dict(
            Offset=self.stream.tell(),
            Stored=len(stored),
            Size=len(data),
            Hash=digest,
            Brotli=use,
        )
        self.stream.write(stored)
        self.index[key] = entry
        self.content[digest] = entry

    def finish(self):
        index = encode(self.index)
        offset = self.stream.tell()
        self.stream.write(index)
        self.stream.seek(0)
        self.stream.write(
            struct.pack("<4sqi", b"SGA1", offset, len(index))
            + hashlib.sha256(index).digest()
        )
        self.stream.flush()
        os.fsync(self.stream.fileno())
        self.stream.close()
        if self.previous:
            self.previous.close()


def cook(root, output, vegetation, *, sink=None, model_paths=None, character_paths=None,
         include_foliage=True, include_prototypes=True):
    output.parent.mkdir(parents=True, exist_ok=True)
    staging = output.with_suffix(".sga.tmp")
    package = sink if sink is not None else Package(staging)
    textures = {}

    def texture(path, kind="color", cutoff=0, repeat=False):
        identity = (str(path), kind, float(cutoff), bool(repeat))
        if identity in textures:
            return textures[identity]
        source = path.read_bytes()
        legacy = (
            "textures/"
            + hashlib.sha256(source + encode(identity[1:])).hexdigest()
            + ".json"
        )
        key = (
            "textures/"
            + hashlib.sha256(
                source
                + encode(identity[1:])
                + (b"bottom-up-alpha-v3" if cutoff > 0 else b"bottom-up-v2")
                + (b"bc7-bc5-v1" if sink is not None else b"")
            ).hexdigest()
            + ".json"
        )
        textures[identity] = key
        if key in package.old:
            for oldkey in package.old:
                if oldkey == key or oldkey.startswith(key + "."):
                    package.reuse(oldkey)
            return key
        # Upgrade a previously cooked top-down chain without redoing its colour filtering.
        if cutoff <= 0 and legacy in package.old:

            def previous(key):
                entry = package.old[key]
                package.previous.seek(entry["Offset"])
                data = package.previous.read(entry["Stored"])
                return brotli.decompress(data) if entry["Brotli"] else data

            descriptor = json.loads(previous(legacy))
            mips = []
            for i, old in enumerate(descriptor["mips"]):
                pixels = np.frombuffer(previous(old), np.uint8).reshape(
                    max(1, descriptor["height"] >> i),
                    max(1, descriptor["width"] >> i),
                    4,
                )
                mip = key + f".{i}"
                package.add(mip, np.flipud(pixels).tobytes())
                mips.append(mip)
            descriptor["mips"] = mips
            descriptor["rowOrder"] = "bottom-up"
            package.add(key, encode(descriptor))
            return key
        pixels = np.array(Image.open(path).convert("RGBA"))
        mips = []
        gpu_mips = []
        gpu_format = ("BC5" if kind == "normal" else "BC7") if sink is not None and kind in ("normal", "color") else None
        for i, p in enumerate(mip_chain(pixels, kind, cutoff)):
            mip = key + f".{i}"
            package.add(mip, np.flipud(p).tobytes())
            mips.append(mip)
            if gpu_format:
                from texture_compression import encode_blocks
                gpu_key = mip + '.' + gpu_format.lower()
                package.add(gpu_key, encode_blocks(np.flipud(p), kind))
                gpu_mips.append(gpu_key)
        package.add(
            key,
            encode(
                dict(
                    width=pixels.shape[1],
                    height=pixels.shape[0],
                    kind=kind,
                    cutoff=cutoff,
                    repeat=repeat,
                    rowOrder="bottom-up",
                    mips=mips,
                    gpuFormat=gpu_format,
                    gpuMips=gpu_mips,
                )
            ),
        )
        return key

    manifests = model_paths if model_paths is not None else sorted((root / "models").glob("*.json")) + sorted(
        vegetation.glob("*.json")
    )

    def model_file(name):
        return (
            vegetation / name
            if (vegetation / name).exists()
            else root / "models" / name
        )

    for number, path in enumerate(manifests):
        doc = json.loads(path.read_text())
        dependencies = []
        for part in doc.get("parts", []):
            mesh = model_file(part["mesh"])
            raw = mesh.read_bytes()
            data = np.frombuffer(raw, dtype="<f4", offset=8).reshape(-1, 8)
            if raw[:4] != b"SGM1" or len(data) != struct.unpack_from("<i", raw, 4)[0]:
                raise ValueError(mesh)
            part["boundsMin"] = data[:, :3].min(axis=0).tolist()
            part["boundsMax"] = data[:, :3].max(axis=0).tolist()
            low = data[data[:, 1] < 0.75, :3]
            part["baseMin"] = (
                low.min(axis=0) if len(low) else np.array([-0.5, 0, -0.5])
            ).tolist()
            part["baseMax"] = (
                low.max(axis=0) if len(low) else np.array([0.5, 0.5, 0.5])
            ).tolist()
            package.add("models/" + part["mesh"], raw)
            dependencies.append("models/" + part["mesh"])
            cutoff = part.get("alphaCutoff", 0)
            for field, kind in (
                ("texture", "color"),
                ("normal", "normal"),
                ("metallicRoughness", "data"),
                ("emissionTexture", "color"),
            ):
                if field in part:
                    part[field] = texture(
                        model_file(part[field]),
                        kind,
                        cutoff if field == "texture" else 0,
                        (
                            part.get("repeat", False)
                            if field != "emissionTexture"
                            else False
                        ),
                    )
                    dependencies.append(part[field])
        doc["dependencies"] = dependencies
        package.add("models/" + path.name, encode(doc))
        if number % 25 == 0:
            print(f"Cooked {number}/{len(manifests)} models", flush=True)
    # Preserve character rig schema while removing texture decode and gzip/JSON nesting at runtime.
    for path in (character_paths if character_paths is not None else sorted((root / "characters").glob("*.gz"))):
        try:
            doc = json.loads(gzip.decompress(path.read_bytes()))
        except (ValueError, OSError):
            continue
        if not isinstance(doc, dict) or "parts" not in doc:
            continue
        for part in doc["parts"]:
            for field, kind in (
                ("albedo", "color"),
                ("normal", "normal"),
                ("metallicRoughness", "data"),
            ):
                if field in part:
                    part[field] = texture(path.parent / part[field], kind)
            for field in (
                "positions",
                "normals",
                "uvs",
                "joints",
                "weights",
                "indices",
            ):
                values = np.asarray(
                    part[field], dtype="<u4" if field == "indices" else "<f4"
                )
                key = (
                    "characters/buffers/" + hashlib.sha256(values.tobytes()).hexdigest()
                )
                package.add(key, values.tobytes())
                part[field] = key
        for clip in doc.get("clips", []):
            values = np.asarray(clip["matrices"], dtype="<f4")
            key = "characters/buffers/" + hashlib.sha256(values.tobytes()).hexdigest()
            package.add(key, values.tobytes())
            clip["matrices"] = key
        package.add("characters/" + path.name, encode(doc))
    foliage = json.loads((root / "foliage/manifest.json").read_text()) if include_foliage else {"materials": []}
    for material in foliage["materials"]:
        raw = gzip.decompress((root / "foliage" / material["file"]).read_bytes())
        magic, side, levels = struct.unpack_from("<4sii", raw)
        assert magic == b"TPB1"
        offset = 12
        layers = []
        for mip in range(levels):
            maps = []
            for channel in range(3):
                length = (max(1, side >> mip) ** 2) * 4
                pixels = raw[offset : offset + length]
                offset += length
                key = "foliage/mips/" + hashlib.sha256(pixels).hexdigest()
                package.add(key, pixels)
                maps.append(key)
            layers.append(maps)
        material["cookedMips"] = layers
    if include_foliage:
        package.add("foliage/manifest.json", encode(foliage))
    for path in (sorted((root / "foliage").glob("*.mesh")) if include_foliage else []):
        package.add("foliage/" + path.name, path.read_bytes())
    for prototype in (sorted((root / "vegetation/prototype").rglob("canopy.json")) if include_prototypes else []):
        descriptor = json.loads(prototype.read_text())
        package.add(prototype.relative_to(root).as_posix(), encode(descriptor))
        for ambient in descriptor.get("ambient", {}).get("levels", []):
            key = ambient["path"]
            package.add(key, (root / key).read_bytes())
        for level in descriptor["levels"]:
            for slab in level["slices"]:
                for key in slab:
                    package.add(key, (root / key).read_bytes())
    if sink is not None:
        return
    package.add(
        "provenance.json",
        encode(
            dict(
                version=1,
                cooker="cook_assets.py",
                textures=len(textures),
                models=len(manifests),
                source="Original importer outputs; licenses retained in distribution.",
            )
        ),
    )
    package.finish()
    # Validate the complete temporary artifact, including reused blocks, before publication.
    with open(staging, "rb") as check:
        checked = set()
        for entry in package.index.values():
            if entry["Offset"] in checked:
                continue
            checked.add(entry["Offset"])
            check.seek(entry["Offset"])
            data = check.read(entry["Stored"])
            if entry["Brotli"]:
                data = brotli.decompress(data)
            if (
                len(data) != entry["Size"]
                or hashlib.sha256(data).hexdigest() != entry["Hash"]
            ):
                raise ValueError("Cooked block verification failed")
    os.replace(staging, output)
    print(
        f"Published {output}: {output.stat().st_size/1048576:.1f} MiB, {len(package.index)} blocks, {len(textures)} texture variants",
        flush=True,
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets", type=Path, default=Path("assets"))
    parser.add_argument(
        "--output", type=Path, default=Path("artifacts/cooked/models.sga")
    )
    parser.add_argument(
        "--vegetation", type=Path, default=Path("artifacts/vegetation/models")
    )
    args = parser.parse_args()
    cook(args.assets, args.output, args.vegetation)
