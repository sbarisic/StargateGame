"""Volume filtering shared by the canopy cooker and focused regression checks."""

import numpy as np


def normalize_normals(normals):
    length = np.linalg.norm(normals, axis=-1, keepdims=True)
    return np.where(
        length > 1e-6, normals / np.maximum(length, 1e-6), [0, 1, 0]
    ).astype(np.float32)


def pad_attributes(colors, normals, density):
    # Extend attributes only: empty voxels must remain empty in both density channels.
    known = density > 0
    colors, normals = colors.copy(), normals.copy()
    for _ in range(2):
        for axis in range(3):
            for direction in (-1, 1):
                destination = [slice(None)] * 3
                source = [slice(None)] * 3
                destination[axis] = (
                    slice(1, None) if direction == 1 else slice(None, -1)
                )
                source[axis] = slice(None, -1) if direction == 1 else slice(1, None)
                destination, source = tuple(destination), tuple(source)
                fill = ~known[destination] & known[source]
                colors[destination][fill] = colors[source][fill]
                normals[destination][fill] = normals[source][fill]
                known[destination][fill] = True
    return colors, normals


def downsample_canopy(colors, normals, density, occupancy):
    def children(values):
        padding = [(0, size % 2) for size in values.shape[:3]] + [(0, 0)] * (
            values.ndim - 3
        )
        padded = np.pad(values, padding, mode="edge")
        return padded.reshape(
            padded.shape[0] // 2,
            2,
            padded.shape[1] // 2,
            2,
            padded.shape[2] // 2,
            2,
            *padded.shape[3:]
        )

    axes = (1, 3, 5)
    mean_density = children(density).mean(axes)
    color_sum = children(colors * density[..., None]).mean(axes)
    normal_sum = children(normals * density[..., None]).mean(axes)
    divisor = np.maximum(mean_density[..., None], 1e-6)
    return (
        color_sum / divisor,
        normalize_normals(normal_sum / divisor),
        mean_density,
        children(occupancy).max(axes),
    )
