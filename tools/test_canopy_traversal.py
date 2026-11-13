"""Dense-reference checks for the canopy shader's conservative empty-region traversal."""
import math
import numpy as np
from scipy.ndimage import map_coordinates

rng=np.random.default_rng(7319)
def pyramid(base,maximum):
    result=[base]
    while max(result[-1].shape)>1:
        a=result[-1]; shape=a.shape
        # Cooked canopies are power-of-two per axis, including singleton axes.
        b=np.pad(a,[(0,s%2) for s in shape],mode="edge")
        b=b.reshape(b.shape[0]//2,2,b.shape[1]//2,2,b.shape[2]//2,2)
        result.append(b.max((1,3,5)) if maximum else b.mean((1,3,5)))
    return result

def warp(p,amplitude):
    q=p.copy(); h=np.clip(p[1],0,1); q[[0,2]]-=amplitude*h*h; return q

def fetch(chain,uv,mip):
    a=chain[mip]; point=uv*np.array(a.shape)-.5
    return float(map_coordinates(a,point[:,None],order=1,mode="nearest",prefilter=False)[0])
def density_at(chain,uv,mip):
    if np.any(uv<0) or np.any(uv>1): return 0.
    lo=int(math.floor(mip)); hi=min(lo+1,len(chain)-1)
    return fetch(chain,uv,lo)*(1-mip+lo)+fetch(chain,uv,hi)*(mip-lo)

def empty_distance(occ,density,p,ray,mip,amplitude):
    uv=warp(p,amplitude)
    if np.any(uv<0) or np.any(uv>1):return 0.
    dm=int(math.ceil(min(mip,len(occ)-1)))
    halo=1/np.array(density[dm].shape)
    speed=np.abs(ray)+np.array([abs(amplitude[0])*2*abs(ray[1]),0,abs(amplitude[1])*2*abs(ray[1])])
    first=min(len(occ)-1,dm+5)
    for level in range(first,max(dm,first-3),-1):
        size=np.array(occ[level].shape); cell=np.clip(np.floor(uv*size).astype(int),0,size-1)
        if occ[level][tuple(cell)]!=0:continue
        margin=np.minimum(uv-cell/size-halo,(cell+1)/size-halo-uv)
        if np.any(margin<=0):continue
        return max(0,float(np.min(margin/np.maximum(speed,1e-10)))*.999)
    return 0.

def integrate(occ,density,origin,ray,mip,amplitude,optimized):
    spacing=.65/density[0].shape[0]*2**mip
    t=0.; end=1.5; ro=float(origin@ray)
    plane=(math.floor(ro/spacing)+1)*spacing-ro
    optical=0.; hit=None; skipped=0
    # Same plane lattice, threshold localization and bounded interval as the shader.
    for _ in range(2048):
        if t>=end:break
        segment=min(max(plane-t,1e-6),end-t);plane+=spacing
        midpoint=warp(origin+ray*(t+segment*.5),amplitude)
        om=min(len(occ)-1,int(math.floor(mip))+2)
        if optimized and np.all(midpoint>=0) and np.all(midpoint<=1) and fetch(occ,midpoint,om)==0:
            # Fast local bound plus the shader's coarse-to-fine extension.
            local=1/density[0].shape[0]*1.5*2**max(0,om-2)/(1+np.linalg.norm(amplitude)*2)
            safe=max(local,empty_distance(occ,density,origin+ray*t,ray,mip,amplitude)) if _ % 8 == 0 else local
            if safe>=segment:
                extra=math.floor((safe-segment)/spacing)*spacing
                target=min(end,t+segment+extra)
                # Compare every omitted integration midpoint, not just the endpoints.
                q=t+segment*.5
                while q<target:
                    assert density_at(density,warp(origin+ray*q,amplitude),mip)==0, "false empty skip"
                    q+=spacing
                t=target;plane+=extra;skipped+=1;continue
        sigma=density_at(density,warp(origin+ray*(t+segment*.5),amplitude),mip)
        threshold=-math.log(.65)
        if hit is None and optical+sigma*segment>=threshold:hit=t+(threshold-optical)/max(sigma,1e-8)
        optical+=sigma*segment;t+=segment
    else:raise AssertionError("Traversal failed to terminate")
    return optical,hit,skipped

checks=skips=0
for kind in ["empty","sparse","thin","boundary"]:
    a=np.zeros((32,32,32))
    if kind=="sparse":a[14:18,14:18,14:18]=8
    if kind=="thin":a[16,4:28,4:28]=12
    if kind=="boundary":a[[0,31],:,:]=4;a[:,[0,31],:]=4
    full_d=pyramid(a,False);full_o=pyramid((a>0).astype(float),True)
    for first in [0,1,2]:
        d=full_d[first:];o=full_o[first:]
        for mip in [0.,.4,1.,1.6]:
            for amp in [np.zeros(2),np.array([.12,-.07])]:
                for index in range(12):
                    origin=rng.uniform(.05,.95,3);origin[0]=-.1
                    ray=np.array([1.,0.,0.]) if index==0 else rng.uniform(-.25,.25,3)+np.array([1,0,0]);ray/=np.linalg.norm(ray)
                    reference=integrate(o,d,origin,ray,mip,amp,False)
                    actual=integrate(o,d,origin,ray,mip,amp,True)
                    assert abs(actual[0]-reference[0])<1e-8,(kind,first,mip,reference,actual)
                    assert (actual[1] is None)==(reference[1] is None)
                    if actual[1] is not None:assert abs(actual[1]-reference[1])<1e-8
                    checks+=1;skips+=actual[2]
assert skips>0
print(f"PASS: {checks} dense-reference rays; {skips} proven-empty advances; no missed hits")
