"""Cook Wooden Lantern 01 at authored scale; run with Python, or Blender for its icon.

Source: James Ray Cock / Poly Haven, CC0; repack by Patch. No source modification.
"""
import io, json, math, struct, hashlib
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'assets_itch/props/wooden_lantern'
OUT = ROOT / 'assets/models'
raw = (SOURCE / 'assets/models/woodenLantern01.glb').read_bytes()
length = struct.unpack_from('<I', raw, 12)[0]
doc = json.loads(raw[20:20+length])
data = raw[28+length:]
# Physics origin is the centre of the complete authored bounds (including handle).
centre = np.array([0., .26555, -.00012])

def accessor(i):
    a = doc['accessors'][i]; v = doc['bufferViews'][a['bufferView']]
    dtype = np.dtype({5123:'<u2',5125:'<u4',5126:'<f4'}[a['componentType']])
    n = {'SCALAR':1,'VEC2':2,'VEC3':3,'VEC4':4}[a['type']]
    return np.ndarray((a['count'],n),dtype,buffer=data,offset=v.get('byteOffset',0)+a.get('byteOffset',0),strides=(v.get('byteStride',n*dtype.itemsize),dtype.itemsize)).copy()

def transform(node):
    if 'matrix' in node: return np.array(node['matrix']).reshape(4,4,order='F')
    x,y,z,w = node.get('rotation',[0,0,0,1])
    m = np.eye(4)
    m[:3,:3] = np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)], [2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)], [2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]]) @ np.diag(node.get('scale',[1,1,1]))
    m[:3,3] = node.get('translation',[0,0,0])
    return m

materials = []
for index, mat in enumerate(doc['materials']):
    pbr = mat['pbrMetallicRoughness']; entry = {'doubleSided':mat.get('doubleSided',False),'alphaMode':mat.get('alphaMode','OPAQUE')}
    for key, ref in [('texture',pbr.get('baseColorTexture')),('normal',mat.get('normalTexture')),('metallicRoughness',pbr.get('metallicRoughnessTexture'))]:
        if ref is None: continue
        image = doc['images'][doc['textures'][ref['index']]['source']]; view = doc['bufferViews'][image['bufferView']]
        pixels = np.array(Image.open(io.BytesIO(data[view.get('byteOffset',0):view.get('byteOffset',0)+view['byteLength']])).convert('RGBA' if key=='texture' else 'RGB'))
        if key=='normal': pixels[:,:,1] = 255-pixels[:,:,1]
        if key=='texture': pixels = np.round(pixels.astype(float)*np.array(pbr.get('baseColorFactor',[1,1,1,1]))).clip(0,255).astype('uint8')
        if key=='metallicRoughness':
            pixels[:,:,0]=255 # AO is deliberately unused by surface lighting.
            pixels[:,:,1]=(pixels[:,:,1]*pbr.get('roughnessFactor',1)).astype('uint8')
            pixels[:,:,2]=(pixels[:,:,2]*pbr.get('metallicFactor',1)).astype('uint8')
        filename=f'wooden-lantern-{index}-{key}.png'; Image.fromarray(pixels).save(OUT/filename); entry[key]=filename
    materials.append(entry)

parts=[]; bounds=[]
def write_mesh(vertices, entry):
    filename=f'wooden-lantern-{len(parts)}.mesh'
    vertices=np.array(vertices,dtype='<f4')
    (OUT/filename).write_bytes(b'SGM1'+struct.pack('<I',len(vertices))+vertices.tobytes())
    parts.append(dict(mesh=filename,**entry)); bounds.extend(vertices[:,:3])

def visit(index,parent):
    node=doc['nodes'][index]; world=parent@transform(node)
    if 'mesh' in node:
        for primitive in doc['meshes'][node['mesh']]['primitives']:
            attrs=primitive['attributes']; p=accessor(attrs['POSITION'])
            p=(np.c_[p,np.ones(len(p))]@world.T)[:,:3]-centre
            n=accessor(attrs['NORMAL'])@np.linalg.inv(world[:3,:3]); n/=np.linalg.norm(n,axis=1)[:,None]
            uv=accessor(attrs['TEXCOORD_0']); uv[:,1]=1-uv[:,1]
            indices=accessor(primitive['indices']).ravel()
            if np.linalg.det(world[:3,:3])<0: indices=indices.reshape(-1,3)[:,::-1].ravel()
            write_mesh(np.c_[p,n,uv][indices],materials[primitive['material']])
    for child in node.get('children',[]): visit(child,world)
for root in doc['scenes'][doc.get('scene',0)]['nodes']: visit(root,np.eye(4))

# Small steady teardrop emitter, independent of the supplied non-emissive materials.
flame=[]; marker=np.array([0.,.235,-.0068])-centre
rings=[(-.022,0.),(-.018,.009),(-.006,.012),(.009,.007),(.025,0.)]
for j in range(len(rings)-1):
    for i in range(16):
        def vertex(r,k):
            y,radius=rings[r]; a=k*math.tau/16
            return [*(marker+np.array([radius*math.cos(a),y,radius*math.sin(a)])),math.cos(a),.2,math.sin(a),k/16,r/(len(rings)-1)]
        a,b,c,d=vertex(j,i),vertex(j,i+1),vertex(j+1,i),vertex(j+1,i+1)
        flame.extend([a,c,b,b,c,d])
Image.new('RGB',(4,4),(255,255,255)).save(OUT/'wooden-lantern-flame.png')
write_mesh(flame,dict(texture='wooden-lantern-flame.png',emissionTexture='wooden-lantern-flame.png',emissionColor=[1,.55,.25],emissionIntensity=12,lightPosition=marker.tolist()))
(OUT/'wooden-lantern.json').write_text(json.dumps(dict(version=1,parts=parts),indent=2)+'\n')
(OUT/'wooden-lantern-credits.txt').write_bytes((SOURCE/'CREDITS.md').read_bytes())
(OUT/'wooden-lantern-license.txt').write_bytes((SOURCE/'LICENSES/CC0-1.0.txt').read_bytes())
(OUT/'wooden-lantern-provenance.json').write_text(json.dumps(dict(source='assets_itch/props/wooden_lantern/assets/models/woodenLantern01.glb',sha256=hashlib.sha256(raw).hexdigest(),assetToBodyOffset=centre.tolist(),lightPosition=marker.tolist(),bounds=[np.min(bounds,axis=0).tolist(),np.max(bounds,axis=0).tolist()],conversion='Node TRS baked; authored metres; body-centred; UV V and normal green flipped'),indent=2)+'\n')
print('Cooked Wooden Lantern 01, height:', np.ptp(np.array(bounds)[:,1]))
