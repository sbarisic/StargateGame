"""Install the user-selected local building materials and metre-scaled UVs."""
import hashlib, json
from pathlib import Path
import numpy as np
from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT/'assets_itch/textures'
MODELS = ROOT/'assets/models'
SETS = {'rustic_stone_wall': (2., .08), 'stone_brick': (2., .02), 'stone_wall': (2., .175),
        'wood060': (1., .03), 'roofing_tiles_007': (1., .035)}
MATERIALS = {'9f1cc6de8499':'rustic_stone_wall', '58ea5d91576a':'stone_brick',
             'cc30a6e9e789':'rustic_stone_wall', '8da135c0d73f':'stone_wall',
             '0e247fa5a9e9':'wood060', 'c7aa97380b55':'roofing_tiles_007'}
PREVIOUS = {'Bricks095':'rustic_stone_wall', 'PavingStones036':'stone_brick', 'Rock030':'rustic_stone_wall'}

def install(download=False):
    provenance=[]
    for name in SETS:
        folder=SOURCE/name
        files={}
        def read(suffix, default=None):
            matches=list(folder.rglob('*_'+suffix+'.jpg'))
            if not matches:
                if default is None: raise FileNotFoundError(f'{name}: {suffix}')
                return Image.new('L',(1024,1024),default)
            path=matches[0]
            files[str(path.relative_to(ROOT))]=hashlib.sha256(path.read_bytes()).hexdigest()
            return Image.open(path).resize((1024,1024),Image.Resampling.LANCZOS)
        read('baseColor').convert('RGB').save(MODELS/f'masonry-{name}-color.png')
        # Poly Haven normals are OpenGL; the game stores DirectX normals.
        r,g,b=read('normal').convert('RGB').split()
        Image.merge('RGB',(r,ImageOps.invert(g),b)).save(MODELS/f'masonry-{name}-normal.png')
        Image.merge('RGBA',(read('ao',255).convert('L'),read('roughness').convert('L'),
                           Image.new('L',(1024,1024),0),read('displacement').convert('L'))).save(MODELS/f'masonry-{name}-mrh.png')
        provenance.append(dict(asset=name,credits=(folder/'CREDITS.md').read_text(),sources=files))
    (ROOT/'assets/building/masonry-materials.json').write_text(json.dumps(dict(license='CC0-1.0',assets=provenance,tool='tools/import_masonry_materials.py',resolution=1024,height='Displacement in material alpha; bounded parallax relief.'),indent=2)+'\n')
    changed=0
    for path in MODELS.glob('*.json'):
        if not path.stem.startswith(('kit-','settlement-mega-house-')): continue
        model=json.loads(path.read_text())
        touched=False
        for part in model.get('parts',[]):
            texture=part.get('texture','')
            name=next((v for k,v in MATERIALS.items() if texture==f'build-{k}-color.png'),None)
            name=name or next((v for k,v in PREVIOUS.items() if texture==f'masonry-{k}-color.png'),None)
            name=name or next((n for n in SETS if texture==f'masonry-{n}-color.png'),None)
            if name is None: continue
            if path.stem == "kit-stone-foundation": name = "stone_brick"
            metres,relief=SETS[name]
            relief=min(relief, part.get('heightScaleLimit', relief))
            # Keep the authored grain/tile orientation on beams, door panels and
            # curved roofing. Masonry uses the existing metre-scaled projection.
            if name not in ('wood060', 'roofing_tiles_007'):
                mesh=MODELS/part['mesh']; raw=mesh.read_bytes()
                vertices=np.frombuffer(raw[8:],dtype='<f4').reshape(-1,3,8).copy()
                normal=np.cross(vertices[:,1,:3]-vertices[:,0,:3],vertices[:,2,:3]-vertices[:,0,:3])
                axes=np.argmax(abs(normal),axis=1)
                for axis in range(3):
                    selected=axes==axis
                    uv_axes=[2,1] if axis==0 else [0,2] if axis==1 else [0,1]
                    vertices[selected,:,6:8]=vertices[selected,:,:3][:,:,uv_axes]/metres
                mesh.write_bytes(raw[:8]+vertices.astype('<f4').tobytes())
            part.update(texture=f'masonry-{name}-color.png',normal=f'masonry-{name}-normal.png',
                        metallicRoughness=f'masonry-{name}-mrh.png',heightScale=relief,repeat=True)
            changed+=1
            touched=True
        if touched: path.write_text(json.dumps(model,indent=2)+'\n')
    print(f'Applied building PBR materials and height relief to {changed} model parts.',flush=True)

if __name__=='__main__':
    import sys
    install('--download' in sys.argv)
