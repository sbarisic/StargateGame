"""Pack the three supplied paving sets into the streamed terrain palette."""
import gzip, hashlib, json, struct
from pathlib import Path
import numpy as np
from PIL import Image
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'assets/terrain/pbr'

def install():
    manifest=json.loads((OUT/'manifest.json').read_text())
    paving=[]
    for index in range(1,4):
        name=f'paving_floor_{index}'
        source=ROOT/'assets_itch/textures'/name
        provenance={}
        def read(suffix,mode):
            path=next(source.rglob('*_'+suffix+'.jpg'))
            provenance[str(path.relative_to(ROOT))]=hashlib.sha256(path.read_bytes()).hexdigest()
            return np.asarray(Image.open(path).convert(mode).resize((2048,2048),Image.Resampling.LANCZOS),dtype=np.float32)/255
        color=read('baseColor','RGB')
        color=np.where(color<=.04045,color/12.92,((color+.055)/1.055)**2.4)
        normal=read('normal','RGB')*2-1; normal[:,:,1]*=-1
        masks=np.stack([read('ao','L'),read('roughness','L'),read('displacement','L'),np.zeros((2048,2048))],axis=-1)
        path=OUT/(name+'.tpb.gz')
        with path.open('wb') as raw, gzip.GzipFile(filename='',mode='wb',fileobj=raw,mtime=0) as stream:
            stream.write(b'TPB1'+struct.pack('<II',2048,12))
            for level in range(12):
                norm=normal/np.maximum(np.linalg.norm(normal,axis=-1,keepdims=True),1e-8)
                rgb=np.where(color<=.0031308,color*12.92,1.055*np.maximum(color,0)**(1/2.4)-.055)
                for data in [np.dstack([rgb,np.ones(color.shape[:2])]),np.dstack([norm*.5+.5,np.ones(color.shape[:2])]),masks]:
                    stream.write(np.flipud(np.rint(np.clip(data,0,1)*255).astype('uint8')).tobytes())
                if level<11:
                    def shrink(a): return (a[::2,::2]+a[1::2,::2]+a[::2,1::2]+a[1::2,1::2])*.25
                    color,normal,masks=shrink(color),shrink(normal),shrink(masks)
        manifest['materials'][name]=dict(file=path.name,sha256=hashlib.sha256(path.read_bytes()).hexdigest(),normalConvention='DirectX',normalStrength=1.,heightBlendStrength=.1,parallaxDepthMetres=.025,grassCoverage=0.,albedoGain=1.,tool='tools/import_paving_materials.py',source=provenance,credits=(source/'CREDITS.md').read_text())
        paving.append(dict(role='paving',material=name,metres=2.))
    manifest['paving']=paving
    (OUT/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    print('Packed three paving PBR sets with height maps and mip chains.',flush=True)
if __name__=='__main__': install()
