"""Author tileable water slope/normal mips; publish through the existing cooker.

The incremental cooker reruns this only when its inputs change. No source
textures are modified. NumPy is already in requirements-assets.txt.
"""
from pathlib import Path
import hashlib
import json
import os
import numpy as np

ROOT = Path(__file__).resolve().parents[1]


def publish(path, data):
    if path.exists() and path.read_bytes() == data:
        return
    temp = path.with_name(path.name + '.tmp')
    with temp.open('wb') as file:
        file.write(data)
        file.flush()
        os.fsync(file.fileno())
    os.replace(temp, path)


def generate(output=ROOT / 'assets/terrain/water', size=256):
    if size < 16 or size > 512 or size & (size - 1):
        raise ValueError('Water normal size must be a power of two from 16 to 512.')
    output.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(0x57415445)
    frequency = np.fft.fftfreq(size) * size
    x, y = np.meshgrid(frequency, frequency)
    radius = np.sqrt(x*x + y*y)
    # A broad, mildly directional spectrum, without a dominant repeating wave.
    spectrum = np.fft.fft2(rng.normal(size=(size, size)))
    band = np.exp(-((radius - 9) / 7)**2) * (1 - np.exp(-radius*radius / 9))
    band *= .55 + .45 * (x*.8 + y*.6)**2 / np.maximum(radius*radius, 1)
    spectrum *= band
    height = np.fft.ifft2(spectrum).real
    sx = np.fft.ifft2(spectrum * (2j*np.pi*x)).real
    sy = np.fft.ifft2(spectrum * (2j*np.pi*y)).real
    amplitude = max(np.quantile(np.sqrt(sx*sx + sy*sy), .999), 1e-9)
    slopes = np.stack((sx/amplitude, sy/amplitude), axis=-1)
    foam = np.clip(.5 + height / max(height.std()*4, 1e-9), 0, 1)
    names = []
    while True:
        normal = np.concatenate((slopes, np.ones((*slopes.shape[:2], 1))), axis=-1)
        normal /= np.linalg.norm(normal, axis=-1, keepdims=True)
        # RG encode slopes, B stores normal Z for inspection, A a smooth foam mask.
        rgba = np.concatenate((slopes*.5+.5, normal[..., 2:3], foam[..., None]), axis=-1)
        pixels = np.rint(np.clip(rgba, 0, 1)*255).astype(np.uint8).tobytes()
        name = 'ripples-' + hashlib.sha256(pixels).hexdigest()[:20] + '.rgba'
        publish(output / name, pixels)
        names.append(name)
        if slopes.shape[0] == 1:
            break
        n = slopes.shape[0] // 2
        slopes = slopes.reshape(n, 2, n, 2, 2).mean(axis=(1, 3))
        foam = foam.reshape(n, 2, n, 2).mean(axis=(1, 3))
    # Payloads precede the descriptor; interrupted generation leaves the previous
    # descriptor and its content-addressed mips intact. No automatic deletion.
    publish(output / 'ripples.json', (json.dumps(dict(version=1, size=size, mips=names,
        source='Procedural water spectrum, seed 0x57415445', license='Project-authored'), indent=2)+'\n').encode())


if __name__ == '__main__':
    generate()
