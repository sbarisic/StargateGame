"""Bake shared hemisphere visibility from the cooked optical-density field."""
import hashlib
import json
from pathlib import Path
import numpy as np
from scipy.ndimage import map_coordinates

ROOT = Path(__file__).resolve().parents[1]


def hemisphere_visibility(density, spacing, reference_distance, steps=32):
    """Eight equal-solid-angle rays per hemisphere, without the originating cell."""
    shape = np.array(density.shape)
    points = np.indices(density.shape, dtype=np.float32).reshape(3, -1)
    output = np.zeros((*density.shape, 2), np.float32)
    if not np.any(density):
        output.fill(1)
        return output
    for hemisphere, sign in enumerate((1, -1)):
        visibility = np.zeros(points.shape[1], np.float32)
        for ray in range(8):
            y = sign * (ray + .5) / 8
            angle = ray * 2.399963229728653
            radius = np.sqrt(1 - y * y)
            # Array coordinates are z,y,x; spacing is in the same order.
            direction = np.array([radius*np.sin(angle), y, radius*np.cos(angle)], np.float32)
            cell_direction = direction / spacing
            start = points + cell_direction[:, None] * float(max(spacing))
            exit_distance = np.full(points.shape[1], np.inf, np.float32)
            for axis in range(3):
                if abs(cell_direction[axis]) > 1e-7:
                    boundary = shape[axis]-.5 if cell_direction[axis] > 0 else -.5
                    exit_distance = np.minimum(exit_distance, (boundary-start[axis])/cell_direction[axis])
            lengths = np.maximum(exit_distance, 0) / steps
            optical_depth = np.zeros(points.shape[1], np.float32)
            for step in range(steps):
                sample = start + cell_direction[:, None] * (lengths*(step+.5))[None, :]
                optical_depth += map_coordinates(density, sample, order=1, mode="constant", cval=0, prefilter=False) * lengths / reference_distance
            visibility += np.exp(-optical_depth)
        output[..., hemisphere] = (visibility / 8).reshape(density.shape)
    return output


def bake(path):
    descriptor = json.loads(path.read_text())
    level = next(m for m in descriptor["levels"] if max(m["size"]) <= 64)
    w,h,d = level["size"]
    opacity = np.stack([np.frombuffer((ROOT/"assets"/slab[1]).read_bytes(), np.uint8).reshape(h,w,4)[...,3] for slab in level["slices"]]).astype(np.float32)/255
    density = -np.log(np.maximum(1-opacity, 1/255))
    extent = np.array(descriptor["maximum"])-np.array(descriptor["minimum"])
    spacing = (extent/np.array([w,h,d]))[::-1]
    visibility = hemisphere_visibility(density, spacing, descriptor["densityReferenceDistance"])
    levels=[]
    total=0
    index=0
    while True:
        d,h,w,_=visibility.shape
        data=np.rint(np.clip(visibility,0,1)*255).astype(np.uint8).tobytes()
        output=path.parent/f"ambient-{index}.bin"
        output.write_bytes(data)
        levels.append(dict(size=[w,h,d],path=output.relative_to(ROOT/"assets").as_posix(),sha256=hashlib.sha256(data).hexdigest()))
        total+=len(data)
        if max(w,h,d)==1:break
        # Filter visibility, never gamma-encode it. Resize to exact mip dimensions.
        dimensions=np.maximum(np.array([d,h,w])//2,1)
        for axis in range(3):
            if visibility.shape[axis] > 1:
                even = np.arange(0, visibility.shape[axis], 2)
                visibility = (np.take(visibility, even, axis=axis) + np.take(visibility, even+1, axis=axis)) * .5
        index+=1
    descriptor["version"]=3
    descriptor["ambient"] = dict(encoding="upper-lower-hemisphere-visibility",directionsPerHemisphere=8,steps=32,bytes=total,levels=levels)
    path.write_text(json.dumps(descriptor,indent=2)+"\n")
    print(f"Ambient {path.parent.name}: {total/1024:.1f} KiB",flush=True)

if __name__=="__main__":
    for path in sorted((ROOT/"assets/vegetation/prototype").rglob("canopy.json")):
        bake(path)
