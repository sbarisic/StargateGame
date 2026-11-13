"""Author fitted leaf collision profiles, keeping the jamb in its parent assembly.

Called by assemble_building_prefabs.py and usable after settlement imports.
Profiles are narrow convex columns following the leaf's upper silhouette, not
the bounding box of the assembly. Glass belongs to the moving leaf too.
"""
import json
from pathlib import Path
import numpy as np

ROOT = Path(__file__).resolve().parents[1]

def clip(poly, axis, value, greater):
    result = []
    for a, b in zip(poly, poly[1:] + poly[:1]):
        ia, ib = (a[axis] >= value) == greater, (b[axis] >= value) == greater
        if ia: result.append(a)
        if ia != ib: result.append(a + (b-a) * ((value-a[axis])/(b[axis]-a[axis])))
    return result

def generate_doors(catalog, models):
    for p in catalog:
        if p['role'] != 'door': continue
        triangles = np.array(p['triangles'], dtype=float)
        if p['id'] == 'kit-door-1-flat' and p.get('doorFitVersion') != 3:
            # Fit within the stone jamb, threshold and lowest lintel edge.
            # The assembly offset is -0.53 m; retain 4-5 mm working clearance.
            lo = triangles.reshape(-1,3).min(0)
            hi = triangles.reshape(-1,3).max(0)
            target_lo = np.array([-.020, .052, lo[2]])
            target_hi = np.array([1.080, 2.129, hi[2]])
            scale = (target_hi-target_lo)/(hi-lo)
            triangles = target_lo + (triangles-lo)*scale
            model = json.loads((models/(p['id']+'.json')).read_text())
            for part in model['parts']:
                path=models/part['mesh']; raw=path.read_bytes()
                v=np.frombuffer(raw[8:],dtype='<f4').reshape(-1,8).copy()
                v[:,:3]=target_lo+(v[:,:3]-lo)*scale
                v[:,3:6]/=scale
                v[:,3:6]/=np.maximum(np.linalg.norm(v[:,3:6],axis=1)[:,None],1e-12)
                path.write_bytes(raw[:8]+v.astype('<f4').tobytes())
                if part.get('normal'):
                    part['heightScaleLimit'] = .002
                    part['heightScale'] = min(part.get('heightScale', .002), .002)
            (models/(p['id']+'.json')).write_text(json.dumps(model,indent=2)+'\n')
            p['doorFitVersion']=3
            p['triangles']=triangles.tolist()
            p['min']=triangles.reshape(-1,3).min(0).tolist()
            p['max']=triangles.reshape(-1,3).max(0).tolist()
        lo,hi=triangles.reshape(-1,3).min(0),triangles.reshape(-1,3).max(0)
        sections=[]
        for a,b in zip(np.linspace(lo[0],hi[0],33)[:-1],np.linspace(lo[0],hi[0],33)[1:]):
            points=[]
            for tri in triangles:
                poly=clip(list(tri),0,a,True)
                if poly: poly=clip(poly,0,b,False)
                points.extend(poly)
            if not points: continue
            v=np.array(points); low=v.min(0); high=v.max(0)
            low[0]=a; high[0]=b
            # Two millimetres of floor/header contact clearance; no widened jamb exemption.
            low[1]+= .002; high[1]-= .002
            sections.append([low.tolist(),high.tolist()])
        p['doorSections']=sections
        p['doorPivot']=[float(lo[0]),0,float(lo[2])]
        # Only the hinge axle seat of this specific assembly may touch its own jamb.
        p['doorSeat']=[[float(lo[0]-.005),float(lo[1]),float(lo[2]-.005)],
                       [float(lo[0]+.025),float(hi[1]),float(lo[2]+.025)]]
    return catalog

if __name__=='__main__':
    for relative in ['assets/building/prefabs.json','assets/settlements/prefabs.json']:
        path=ROOT/relative
        data=generate_doors(json.loads(path.read_text()),ROOT/'assets/models')
        path.write_text(json.dumps(data,separators=(',',':'))+'\n')
    print('Generated fitted door sections and explicit hinge seats.')
