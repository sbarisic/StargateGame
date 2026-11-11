"""Regression for BC5 channel packing and BC edge-mip dimensions."""
import numpy as np
from texture_compression import encode_blocks


def channel(block):
    high, low = block[:2]
    table = [high, low]
    if high > low:
        table += [((7-i)*high+i*low)/7 for i in range(1, 7)]
    else:
        table += [((5-i)*high+i*low)/5 for i in range(1, 5)] + [0, 255]
    indices = int.from_bytes(block[2:8], 'little')
    return np.array([table[(indices >> (i*3)) & 7] for i in range(16)]).reshape(4, 4)


for width, height in [(1,1), (3,5), (8,8)]:
    pixels = np.full((height,width,4), (72,183,200,255), np.uint8)
    data = encode_blocks(pixels, 'normal')
    assert len(data) == ((width+3)//4)*((height+3)//4)*16
    assert abs(channel(data[:8]).mean()-72) < 2
    assert abs(channel(data[8:16]).mean()-183) < 2
    assert len(encode_blocks(pixels, 'color')) == len(data)
pixels = np.zeros((8,8,4),np.uint8)
pixels[:,:,0] = np.arange(8)[None,:]*30
pixels[:,:,1] = np.arange(8)[:,None]*30
pixels[:,:,2:] = 255
data = encode_blocks(pixels, 'normal')
for y in range(2):
    for x in range(2):
        block=data[(y*2+x)*16:(y*2+x+1)*16]
        decoded=np.stack([channel(block[:8]),channel(block[8:])],axis=2)
        assert np.abs(decoded-pixels[y*4:y*4+4,x*4:x*4+4,:2]).max() < 9
print('PASS: BC5 packed channels, row orientation, gradients and small/odd BC7/BC5 mip extents')
