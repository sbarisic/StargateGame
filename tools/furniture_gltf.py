"""Read-only glTF helpers for the furnishing importer and source inspection."""

import json, math
from pathlib import Path
from urllib.parse import unquote
import numpy as np


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
        self.path = Path(path)
        self.doc = json.loads(self.path.read_text())
        self.buffers = [
            (self.path.parent / unquote(b["uri"])).read_bytes()
            for b in self.doc["buffers"]
        ]
        self.world = {}

        def visit(i, parent):
            n = self.doc["nodes"][i]
            self.world[i] = parent @ matrix(n)
            for child in n.get("children", []):
                visit(child, self.world[i])

        for i in self.doc["scenes"][self.doc.get("scene", 0)]["nodes"]:
            visit(i, np.eye(4))

    def accessor(self, index):
        a = self.doc["accessors"][index]
        v = self.doc["bufferViews"][a["bufferView"]]
        dt = np.dtype(
            {
                5120: "i1",
                5121: "u1",
                5122: "<i2",
                5123: "<u2",
                5125: "<u4",
                5126: "<f4",
            }[a["componentType"]]
        )
        count = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a["type"]]
        result = np.ndarray(
            (a["count"], count),
            dt,
            buffer=self.buffers[v["buffer"]],
            offset=v.get("byteOffset", 0) + a.get("byteOffset", 0),
            strides=(v.get("byteStride", dt.itemsize * count), dt.itemsize),
        ).copy()
        if a.get("normalized") and dt.kind in "iu":
            result = np.maximum(-1, result.astype(float) / np.iinfo(dt).max)
        return result

    def descendants(self, index):
        yield index
        for c in self.doc["nodes"][index].get("children", []):
            yield from self.descendants(c)

    def parts(self, roots=None):
        selected = (
            sorted(self.world)
            if roots is None
            else sorted({i for r in roots for i in self.descendants(r)})
        )
        result = []
        for i in selected:
            node = self.doc["nodes"][i]
            if "mesh" not in node:
                continue
            m = self.world[i]
            for prim in self.doc["meshes"][node["mesh"]]["primitives"]:
                if prim.get("mode", 4) != 4:
                    raise ValueError("Only triangle glTF primitives supported")
                attrs = prim["attributes"]
                p = self.accessor(attrs["POSITION"])
                p = (np.c_[p, np.ones(len(p))] @ m.T)[:, :3]
                ids = (
                    self.accessor(prim["indices"]).ravel()
                    if "indices" in prim
                    else np.arange(len(p))
                )
                if np.linalg.det(m[:3, :3]) < 0:
                    ids = ids.reshape(-1, 3)[:, [0, 2, 1]].ravel()
                uv = (
                    self.accessor(attrs["TEXCOORD_0"])
                    if "TEXCOORD_0" in attrs
                    else np.zeros((len(p), 2))
                )
                uv[:, 1] = 1 - uv[:, 1]
                if "NORMAL" in attrs:
                    n = self.accessor(attrs["NORMAL"]) @ np.linalg.inv(m[:3, :3])
                    n /= np.maximum(1e-9, np.linalg.norm(n, axis=1))[:, None]
                    vertices = np.c_[p, n, uv][ids]
                else:
                    tri = p[ids].reshape(-1, 3, 3)
                    n = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
                    n /= np.maximum(1e-9, np.linalg.norm(n, axis=1))[:, None]
                    vertices = np.c_[p[ids], np.repeat(n, 3, axis=0), uv[ids]]
                result.append((vertices, prim.get("material", 0)))
        return result


def bounds(parts):
    vertices = np.concatenate([v[:, :3] for v, _ in parts])
    return vertices.min(0), vertices.max(0)


def transform(parts, scale=1, yaw=0, offset=(0, 0, 0)):
    c, s = math.cos(yaw), math.sin(yaw)
    r = np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])
    result = []
    for v, material in parts:
        a = v.copy()
        a[:, :3] = (a[:, :3] * scale) @ r.T + offset
        a[:, 3:6] = a[:, 3:6] @ r.T
        result.append((a, material))
    return result


def centre(parts):
    lo, hi = bounds(parts)
    return transform(parts, offset=(-(lo[0] + hi[0]) / 2, -lo[1], -(lo[2] + hi[2]) / 2))


def crop(parts, region):
    """Select complete triangles inside a curated source-space bounding box."""
    lo, hi = np.array(region[:3]), np.array(region[3:])
    result = []
    for v, material in parts:
        tri = v.reshape(-1, 3, 8)
        mask = np.all(
            (tri[:, :, :3] >= lo - 1e-6) & (tri[:, :, :3] <= hi + 1e-6), axis=(1, 2)
        )
        if mask.any():
            result.append((tri[mask].reshape(-1, 8), material))
    if not result:
        raise ValueError("Empty source selection")
    return result
