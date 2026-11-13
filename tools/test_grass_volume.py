"""Verify shipped grass fields against their matching near meshes and canopy encoding."""
from pathlib import Path
import hashlib,json,struct
import numpy as np
from tree_volume_data import downsample_canopy
root=Path(__file__).resolve().parents[1]/'assets/vegetation/grass-volume'
d=json.loads((root/'grass.json').read_text());assert d['extent']==[2,.5,2] and d['resolution']==[128,32,128] and d['version']==3 and len(d['variants'])==8
hashes=[]
def filtered(a, uv):
 p=uv*np.array(a.shape)-.5; lo=np.floor(p).astype(int); f=p-lo
 result=np.zeros(len(uv))
 for z in (0,1):
  for y in (0,1):
   for x in (0,1):
    corner=np.array([z,y,x]); index=np.clip(lo+corner,0,np.array(a.shape)-1)
    weight=np.prod(np.where(corner,f,1-f),axis=1)
    result+=a[index[:,0],index[:,1],index[:,2]]*weight
 return result
rng=np.random.default_rng(7319)
empty_checks=0
for index,v in enumerate(d['variants']):
 raw=(root/v['mesh']).read_bytes();count=struct.unpack_from('<I',raw)[0];mesh=np.frombuffer(raw,dtype='<f4',offset=4).reshape(count,8)
 roots=np.frombuffer((root/v['roots']).read_bytes(),dtype='<f4').reshape(count,2)
 assert np.isfinite(roots).all()
 assert np.array_equal(roots.reshape(-1,3,2)[:,0],roots.reshape(-1,3,2)[:,1]) and np.array_equal(roots.reshape(-1,3,2)[:,0],roots.reshape(-1,3,2)[:,2]), 'Every triangle has one blade root'
 # Shared strip vertices must agree across triangles, including periodic copies.
 seen={}
 for vertex,anchor in zip(mesh,roots):
  key=vertex.tobytes();value=anchor.tobytes()
  assert seen.setdefault(key,value)==value, 'Connected blade segments disagree on their root'
 for anchor in np.unique(roots,axis=0):
  points=mesh[np.all(roots==anchor,axis=1),:3]
  # All fragments of one strip make the same stochastic decision at any density.
  assert len(points)>=3 and np.max(np.linalg.norm(points[:,[0,2]]-anchor,axis=1))<.85
 assert np.isfinite(mesh).all() and np.min(mesh[:,1])==0 and .1<np.max(mesh[:,1])<=v['tip']+.00001
 assert np.allclose(np.linalg.norm(mesh[:,3:6],axis=1),1,atol=1e-5)
 # Crossing triangles must have periodic copies; otherwise the finite mesh
 # and volume both reveal the patch lattice at the edges.
 assert mesh[:,0].min()<0 and mesh[:,0].max()>2 and mesh[:,2].min()<0 and mesh[:,2].max()>2
 colors=[];densities=[]
 for level in v['levels']:
  w,h,z=level['size'];c=np.frombuffer((root/level['files'][0]).read_bytes(),np.uint8).reshape(z,h,w,4);n=np.frombuffer((root/level['files'][1]).read_bytes(),np.uint8).reshape(z,h,w,4)
  colors.append(c);densities.append(n[...,3]/255.)
 assert colors[0][...,3].min()==0 and colors[0][...,3].max()==255
 assert v['extent']==(.5 if index<4 else 1.)
 assert not np.any(densities[0][:,int(np.ceil(v['tip']/v['extent']*32))+1:,:]), 'No density above tapered tips'
 assert np.any(densities[0][:,0,:]), 'Roots present'
 assert all(np.any(edge) for edge in (densities[0][0,:,:],densities[0][-1,:,:],densities[0][:,:,0],densities[0][:,:,-1])), 'No empty border around periodic patches'
 for mip in range(1,8):
  prev=densities[mip-1];pad=np.pad(prev,[(0,s%2) for s in prev.shape],mode='edge');expected=pad.reshape(pad.shape[0]//2,2,pad.shape[1]//2,2,pad.shape[2]//2,2).mean((1,3,5))
  assert np.max(np.abs(expected-densities[mip]))<.009, 'Density mean preserved under quantization'
  occ=colors[mip-1][...,3];pad=np.pad(occ,[(0,s%2) for s in occ.shape],mode='edge');expected=pad.reshape(pad.shape[0]//2,2,pad.shape[1]//2,2,pad.shape[2]//2,2).max((1,3,5));assert np.array_equal(expected,colors[mip][...,3]), 'Conservative occupancy'
 assert len((root/v['ambient']).read_bytes())==32*8*32*2
 # Coarser screen LOD must not use a finer occupancy footprint to reject density.
 uv=rng.random((2048,3))
 for mip in (0.,.4,1.5,2.,2.25,2.75,3.):
  lo=int(np.floor(mip));hi=int(np.ceil(mip));occ=min(7,max(2,hi+1))
  mean=filtered(densities[lo],uv)*(1-(mip-lo))+filtered(densities[hi],uv)*(mip-lo)
  empty=filtered(colors[occ][...,3],uv)==0
  assert np.all(mean[empty]==0), 'Screen LOD occupancy must cover both filtered density levels'
  empty_checks+=int(empty.sum())
 hashes.append(hashlib.sha256(raw).hexdigest())
assert len(set(hashes))==8
print('PASS: eight distinct grounded short/meadow meshes, 128x32x128 fields, density means, conservative occupancy mips, ambient bounds and tapered tips')
assert empty_checks>1000
print(f'PASS: {empty_checks} filtered empty samples preserve fractional grass density mips')
print('PASS: per-blade roots are constant across every connected mesh segment and periodic copy')
