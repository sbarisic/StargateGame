"""Deterministic 2m grass patches, using the existing curved grass blade geometry and atlas.
The near meshes and voxel fields are baked together; no separate silhouette generator.
"""
from pathlib import Path
import json, hashlib, struct
import numpy as np
from PIL import Image
from tree_volume_data import downsample_canopy, pad_attributes, normalize_normals
from bake_canopy_ambient import hemisphere_visibility
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'assets/vegetation/grass-volume'
CELL=2/128

def bake(variant):
    tall=variant>=4
    extent=1.0 if tall else .5
    cell=np.array([CELL,extent/32,CELL],np.float32)
    rng=np.random.default_rng(0x613AB+variant)
    atlas=np.asarray(Image.open(ROOT/'assets/vegetation/grass-blades.png').convert('RGB'),np.float32)/255
    vertices=[]; normals=[]; uvs=[]
    # Same four-segment curved profile as grass.vert; wider blades give a full,
    # porous sward while the 0.10-0.28m root-to-tip heights remain unchanged.
    profile=[(-.45,0),(-1,.22),(.45,0),(.45,0),(-1,.22),(1,.22),(-1,.22),(-.8,.48),(1,.22),(1,.22),(-.8,.48),(.8,.48),(-.8,.48),(-.46,.76),(.8,.48),(.8,.48),(-.46,.76),(.46,.76),(-.46,.76),(0,1),(.46,.76)]
    for tuft in range(88):
        root=rng.uniform(0,2,2); yaw=rng.uniform(0,np.pi*2); height=rng.uniform(.35,.75) if tall else rng.uniform(.10,.28)
        for blade in range(8):
            angle=yaw+blade*2.39996; side=np.array([np.cos(angle),0,np.sin(angle)]); facing=np.array([-side[2],0,side[0]])
            base=np.array([root[0],0,root[1]])+side*(.035+.012*(blade%3))
            r=rng.random(); h=np.clip(height*(.55+.70*r),.35,.75) if tall else np.clip(height*(.55+.70*r),.10,.28); width=.008+.009*r; bend=.35+.65*r; tex=int(rng.integers(0,7))
            for x,y in profile:
                pos=base+side*x*width+np.array([0,h*(y-.22*r*y*y*y),0])+facing*h*bend*y*y
                along=np.array([0,1-.66*r*y*y,0])+facing*(2*bend*y)
                vertices.append(pos); normals.append(np.cross(along,side)); uvs.append([(tex+(x+1)*.46+.04)/8,y])
    vertices=np.asarray(vertices,np.float32); normals=normalize_normals(np.asarray(normals,np.float32)); uvs=np.asarray(uvs,np.float32)
    # Periodic geometry: keep the portions of edge-crossing blades on the opposite
    # side too. Clipping without these copies leaves a visible square gutter.
    original=np.concatenate([vertices,normals,uvs],axis=1).reshape(-1,3,8)
    copies=[]
    for dx in (-2,0,2):
        for dz in (-2,0,2):
            moved=original.copy();moved[:,:,0]+=dx;moved[:,:,2]+=dz
            inside=(moved[:,:,0].max(1)>=0)&(moved[:,:,0].min(1)<=2)&(moved[:,:,2].max(1)>=0)&(moved[:,:,2].min(1)<=2)
            copies.append(moved[inside])
    mesh=np.concatenate(copies).reshape(-1,8).astype('<f4')
    meshfile=f'variant-{variant}.mesh.bin'; (OUT/meshfile).write_bytes(struct.pack('<I',len(mesh))+mesh.tobytes())
    shape=(128,32,128); colors=np.zeros((*shape,3),np.float32); ns=np.zeros_like(colors); weights=np.zeros(shape,np.float32)
    for start in range(0,len(vertices),3):
        tri=vertices[start:start+3]; uv=uvs[start:start+3]; nn=normals[start:start+3]
        steps=max(2,int(np.max(np.linalg.norm(tri-np.roll(tri,1,axis=0),axis=1))/(CELL*.35))+1)
        a,b=np.meshgrid(np.arange(steps+1)/steps,np.arange(steps+1)/steps); a=a.ravel();b=b.ravel();ok=a+b<=1;a=a[ok];b=b[ok]
        bary=np.stack([1-a-b,a,b],1); pts=bary@tri; st=bary@uv
        ids=np.floor(pts/cell).astype(int); ids[:,0]%=128;ids[:,2]%=128
        valid=(ids[:,1]>=0)&(ids[:,1]<32)
        ids=ids[valid]; st=st[valid]; bary=bary[valid]; idx=(ids[:,2],ids[:,1],ids[:,0])
        rgb=atlas[np.clip(((1-st[:,1])*(atlas.shape[0]-1)).astype(int),0,atlas.shape[0]-1),np.clip((st[:,0]*(atlas.shape[1]-1)).astype(int),0,atlas.shape[1]-1)]
        np.add.at(weights,idx,1);np.add.at(colors,idx,rgb);np.add.at(ns,idx,bary@nn)
    colors/=np.maximum(weights[...,None],1);ns=normalize_normals(ns)
    density=np.where(weights>0,np.minimum(.8,.24+weights*.018),0).astype(np.float32); occupancy=(weights>0).astype(np.float32)
    # Reuse canopy's upper/lower hemisphere bake, on an 8x smaller grid.
    small=density.reshape(32,4,8,4,32,4).mean((1,3,5))
    ambient=hemisphere_visibility(-np.log(np.maximum(1-small,1e-5)),cell*4,CELL,steps=16)
    ambientfile=f'variant-{variant}.ambient.bin';(OUT/ambientfile).write_bytes(np.round(np.clip(ambient,0,1)*255).astype('u1').tobytes())
    levels=[]
    for mip in range(8):
        padded,pn=pad_attributes(colors,ns,density)
        c=np.concatenate([np.clip(padded,0,1),occupancy[...,None]],axis=3)
        n=np.concatenate([pn*.5+.5,density[...,None]],axis=3)
        files=[]
        for kind,values in [('color',c),('normal',n)]:
            name=f'variant-{variant}.{mip}.{kind}.bin'; (OUT/name).write_bytes(np.round(np.clip(values,0,1)*255).astype('u1').tobytes()); files.append(name)
        levels.append(dict(size=[density.shape[2],density.shape[1],density.shape[0]],files=files))
        if mip!=7: colors,ns,density,occupancy=downsample_canopy(colors,ns,density,occupancy)
    return dict(extent=extent,tip=.75 if tall else .28,mesh=meshfile,vertices=len(mesh),levels=levels,ambient=ambientfile)

def main():
    OUT.mkdir(parents=True,exist_ok=True)
    variants=[]
    for i in range(8):
        variants.append(bake(i));print('Baked grass variant',i,flush=True)
    deps=[f'vegetation/grass-volume/{p.name}' for p in sorted(OUT.glob('*.bin'))]
    doc=dict(version=2,extent=[2,.5,2],resolution=[128,32,128],referenceDistance=CELL,variants=variants,dependencies=deps,
        source='Existing grass.vert curved blades and grass-blades.png; see grass-blades.provenance.json',
        sourceHash=hashlib.sha256((ROOT/'assets/vegetation/grass-blades.png').read_bytes()+Path(__file__).read_bytes()).hexdigest())
    (OUT/'grass.json').write_text(json.dumps(doc,separators=(',',':'))+'\n')
if __name__=='__main__':main()
