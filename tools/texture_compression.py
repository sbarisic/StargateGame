"""Offline BC encoders. RGBA fallback chains remain available to older GPUs."""
from pathlib import Path
import sys
import numpy as np

VERSION = 'ispc-1.0.1-bc7-alpha-basic-bc5-rg-v2'


def encode_blocks(pixels, kind):
    try:
        import ispc_texcomp as encoder
    except ImportError:
        sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'artifacts/asset-tools'))
        try:
            import ispc_texcomp as encoder
        except ImportError as error:
            raise RuntimeError('Install the cooker dependencies: python -m pip install -r tools/requirements-assets.txt') from error
    height, width = pixels.shape[:2]
    pixels = np.pad(pixels, ((0, (-height) % 4), (0, (-width) % 4), (0, 0)), mode='edge')
    # ISPC BC5 consumes packed RG pairs despite the binding's RGBA surface name.
    if kind == 'normal':
        surface = encoder.RGBASurface(pixels[:, :, :2].tobytes(), pixels.shape[1], pixels.shape[0], pixels.shape[1] * 2)
    else:
        surface = encoder.RGBASurface(pixels.tobytes(), pixels.shape[1], pixels.shape[0])
    data = encoder.compress_blocks_bc5(surface) if kind == 'normal' else encoder.compress_blocks_bc7(surface, encoder.BC7EncSettings.from_profile('alpha_basic'))
    expected = ((width + 3)//4)*((height + 3)//4)*16
    if len(data) != expected:
        raise ValueError('Invalid compressed texture output length')
    return data
