"""Offline 2D terrain experiment. Never writes game assets, caches or runtime code.

Adapted from SLTerrainGeneration, Michael Tuck (2024), MIT:
FBMGenerator, USPED, fillSinksWangLiu, Strahler/channel classification and
terrain-dependent cover. See the companion README for deliberate differences.
Run with numpy, scipy, numba and Pillow; output goes exclusively to artifacts.
"""
from pathlib import Path
import argparse
import hashlib
import heapq
import json
import time
import numpy as np
from numba import njit
from scipy.ndimage import gaussian_filter, uniform_filter, binary_propagation
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/terrain-library-prototype'
CELL = 8.0
N = 513


@njit(cache=True)
def route(z, spacing):
    """Wang-Liu style priority flood + metric D8; stable heap tie-breaking.

    Filled heights are a ROUTING surface, not a replacement for the terrain.
    Upstream recursion is replaced by iterative topological accumulation.
    """
    rows, cols = z.shape
    size = z.size
    filled = z.ravel().copy()
    seen = np.zeros(size, np.uint8)
    rank = np.empty(size, np.int32)
    order = np.empty(size, np.int32)
    downstream = np.full(size, -1, np.int32)
    queue = [(0.0, 0)]
    queue.pop()
    for i in range(size):
        y, x = i // cols, i % cols
        if y == 0 or x == 0 or y == rows-1 or x == cols-1:
            seen[i] = 1
            heapq.heappush(queue, (filled[i], i))
    k = 0
    while queue:
        level, i = heapq.heappop(queue)
        order[k], rank[i] = i, k
        k += 1
        y, x = i // cols, i % cols
        for dy in range(-1, 2):
            for dx in range(-1, 2):
                if (dy == 0 and dx == 0) or not (0 <= y+dy < rows and 0 <= x+dx < cols):
                    continue
                j = (y+dy)*cols+x+dx
                if seen[j]:
                    continue
                seen[j] = 1
                filled[j] = max(filled[j], level + 1e-7)
                downstream[j] = i
                heapq.heappush(queue, (filled[j], j))
    area = np.ones(size, np.int64)
    stream = np.ones(size, np.int16)
    max_in = np.zeros(size, np.int16)
    duplicates = np.zeros(size, np.int16)
    for k in range(size-1, -1, -1):
        i = order[k]
        y, x = i // cols, i % cols
        if downstream[i] >= 0:
            best = -1.0
            for dy in range(-1, 2):
                for dx in range(-1, 2):
                    if (dx == 0 and dy == 0) or not (0 <= y+dy < rows and 0 <= x+dx < cols):
                        continue
                    j = (y+dy)*cols+x+dx
                    if rank[j] >= rank[i]:
                        continue
                    slope = (filled[i]-filled[j]) / (spacing*np.sqrt(dx*dx+dy*dy))
                    if slope > best:
                        best, downstream[i] = slope, j
        stream[i] = max(1, max_in[i] + (1 if duplicates[i] >= 2 else 0))
        j = downstream[i]
        if j >= 0:
            area[j] += area[i]
            if stream[i] > max_in[j]:
                max_in[j], duplicates[j] = stream[i], 1
            elif stream[i] == max_in[j]:
                duplicates[j] += 1
    return filled.reshape(z.shape), downstream, area.reshape(z.shape), stream.reshape(z.shape), order


@njit(cache=True)
def distributed_runoff(filled, order, spacing):
    """Custom MFD extension: split runoff by downhill gradient^1.1.
    SLTerrain warns about D8 artifacts; retain D8 for river hierarchy, but
    avoid concentrating all hillslope erosion along eight compass bearings.
    """
    rows,cols=filled.shape
    heights=filled.ravel()
    area=np.ones(heights.size,np.float64)
    weights=np.zeros(9,np.float64)
    for k in range(order.size-1,-1,-1):
        i=order[k]; y,x=i//cols,i%cols
        if y==0 or x==0 or y==rows-1 or x==cols-1:continue
        total=0.0
        for dy in range(-1,2):
            for dx in range(-1,2):
                if dx==0 and dy==0:continue
                j=(y+dy)*cols+x+dx
                s=max(0.0,(heights[i]-heights[j])/(spacing*np.sqrt(dx*dx+dy*dy)))
                weights[(dy+1)*3+dx+1]=s**1.1
                total+=s**1.1
        if total<=0:continue
        for dy in range(-1,2):
            for dx in range(-1,2):
                if dx==0 and dy==0:continue
                j=(y+dy)*cols+x+dx
                area[j]+=area[i]*weights[(dy+1)*3+dx+1]/total
    return area.reshape(filled.shape)


def perlin(x, y, permutation):
    ix, iy = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x-ix, y-iy
    u, v = fx**3*(fx*(fx*6-15)+10), fy**3*(fy*(fy*6-15)+10)
    def gradient(dx, dy, ox, oy):
        h = permutation[(permutation[(ix+ox) & 255]+iy+oy) & 255] & 15
        a = np.where(h < 8, dx, dy)
        b = np.where(h < 4, dy, np.where((h == 12) | (h == 14), dx, 0))
        return np.where((h & 1) == 0, a, -a) + np.where((h & 2) == 0, b, -b)
    a = gradient(fx, fy, 0, 0)*(1-u)+gradient(fx-1, fy, 1, 0)*u
    b = gradient(fx, fy-1, 0, 1)*(1-u)+gradient(fx-1, fy-1, 1, 1)*u
    return (a*(1-v)+b*v)*0.5+0.5


def fbm(x, y, seed, wavelength=2000, octaves=7, hurst=1.0):
    """SLTerrain's normalized layered Perlin, gain=2^-H and lacunarity=2.
    A local seeded permutation replaces the upstream global RNG state.
    """
    rng = np.random.default_rng(seed)
    p = rng.permutation(256)
    ox, oy = rng.uniform(20, 200, 2)
    result = np.zeros_like(x)
    weight, total = 1.0, 0.0
    for octave in range(octaves):
        result += perlin(x/wavelength*2**octave+ox, y/wavelength*2**octave+oy, p)*weight
        total += weight
        weight *= 2**(-hurst)
    return result/total


def initial(seed, kind):
    y, x = np.mgrid[-2048:2049:8, -2048:2049:8].astype(float)
    # Upstream fBm power shaping, modified in physical metres. Broad regional
    # envelope is OUR composition layer, not a claim about the library.
    n = fbm(x, y, seed, 2800, 7, .95)
    n = np.clip((n-.23)/.48, 0, 1)
    peaks = n**2.7
    envelope = .25 + .75*np.exp(-((x-450)/1950)**2-((y+450)/1900)**2)
    z = 30 + 900*peaks*envelope + 60*(y+2048)/4096
    z = (z-z.min())/(z.max()-z.min())*660+24
    if kind == 'desert':
        # Custom plateau shaping + sand mantle. Erode exposed bedrock first.
        plateau = np.clip((n-.40)/.25, 0, 1)
        plateau = plateau**2*(3-2*plateau)
        z = 25+plateau*210 + 22*fbm(x,y,seed+31,1300,5)
    elif kind == 'coastal':
        # A connected coast, not a threshold applied to every enclosed basin.
        coast_shape=(fbm(x,y,seed+43,1800,4)-.5)*2300
        coast_shape+=210*np.sin(y/640)+120*np.sin(y/270)
        coast = np.clip((x+700+coast_shape)/1750, 0, 1)
        coast = coast*coast*(3-2*coast)
        z = z*(1-coast)-45*coast
        z += 95*np.exp(-((x-1650)/260)**2-((y+950)/370)**2)
    return z.astype(np.float64)


def erode(z, seed, iterations, kind='mountain', full=True):
    z = z.copy()
    start = z.copy()
    total_usped = np.zeros_like(z)
    total_channel = np.zeros_like(z)
    rng = np.random.default_rng(seed+82)
    jitter = rng.uniform(.75, 1, z.shape)  # fixed in space; reproducible
    soil = .035+.025*fbm(*np.meshgrid(np.arange(N)*CELL, np.arange(N)*CELL),seed+55,700,4)
    t = time.perf_counter()
    for iteration in range(iterations):
        filled, down, area, stream, order = route(z, CELL)
        runoff = distributed_runoff(filled,order,CELL)
        gy, gx = np.gradient(z, CELL)
        slope = np.hypot(gx, gy)
        sine = slope/np.sqrt(1+slope*slope)
        # Upstream cover feedback: forest .3, meadow .6, rock .5, plateau .7.
        moist = gaussian_filter(np.log1p(runoff), 3)
        cover = np.where((moist>2.4)&(slope<.35), .3, np.where(slope>.5,.5,.6))
        if kind == 'desert':
            cover = np.full_like(z,.7)
        rain = 280*(.3 if kind == 'desert' else 1)
        a = gaussian_filter(runoff, .7)*CELL
        rill = iteration % 2 == 0
        capacity = soil*cover*rain*(a**1.6*sine**1.3 if rill else a*sine*10)
        # Continuous aspect and metric flux derivatives replace eight-angle
        # aspect/derivative estimates. Positive divergence removes ground.
        qx, qy = -gx/np.maximum(slope,1e-8)*capacity, -gy/np.maximum(slope,1e-8)*capacity
        divergence = np.gradient(qx,CELL,axis=1)+np.gradient(qy,CELL,axis=0)
        change = gaussian_filter(-divergence, .8)*.00038*jitter
        change = np.clip(change,-.6,.35)
        change *= np.clip(1-(filled-z)/.5,0,1)
        if kind=='coastal':change[z<0]=0
        # No artificial border erosion (upstream does subtract R*0.1 there).
        change[[0,-1],:] = 0
        change[:,[0,-1]] = 0
        total_usped += change
        z += change
        if full:
            # Stream-order selection is adapted from the library. Its ad hoc
            # eight-direction bank offsets are replaced by isotropic spreading
            # and bounded stream-power incision (custom physical adaptation).
            zflat = z.ravel()
            receiver = np.maximum(down,0)
            rows, cols = z.shape
            indices = np.arange(z.size)
            length = CELL*np.maximum(1,np.hypot(indices%cols-receiver%cols,indices//cols-receiver//cols))
            ds = np.maximum(0,(zflat-zflat[receiver])/length).reshape(z.shape)
            incision = np.minimum(.9,.025*np.sqrt(runoff)*ds)
            channel_support=gaussian_filter(((stream>=2)&(area>12)).astype(float),1.5)
            incision *= np.clip((runoff-8)/12,0,1)*np.clip(channel_support*3,0,1)
            incision = gaussian_filter(incision, .8)
            incision = np.minimum(incision, np.maximum(0,z-filled+.8))
            incision[[0,-1],:] = 0
            incision[:,[0,-1]] = 0
            if kind=='coastal':incision[z<0]=0
            total_channel -= incision
            z -= incision
            # Custom small hillslope relaxation; does not replace whole map
            # with smoothed noise. Preserve flatter depositional surfaces.
            mean = uniform_filter(z,3)
            relaxation = np.clip(mean-z,-.3,.3)*np.clip((slope-.65)*.25,0,.25)
            z += relaxation
        if (iteration+1)%40 == 0:
            print(f'{kind}: {iteration+1}/{iterations}, {time.perf_counter()-t:.1f}s',flush=True)
    return z, total_usped, total_channel, time.perf_counter()-t


def dunes(z, seed):
    """Custom wind-aligned sand, deliberately separate from library erosion."""
    y,x = np.mgrid[-2048:2049:8,-2048:2049:8].astype(float)
    u,v=x*.88+y*.47,-x*.47+y*.88
    warp = (fbm(x,y,seed+91,900,4)-.5)*430+55*np.sin(v/175)
    phase = ((u+warp)/185)%1
    profile = np.where(phase<.8,phase/.8,(1-phase)/.2)**1.4
    slope = np.hypot(*np.gradient(z,CELL))
    cover = np.clip((115-z)/55,0,1)*np.clip(1-slope/.35,0,1)
    dune_amplitude=np.clip((fbm(x,y,seed+92,520,3)-.30)/.30,0,1)*13
    return z+gaussian_filter(profile*dune_amplitude*gaussian_filter(cover,3),.6)


def colorize(z, kind='mountain', water=True, gray=False):
    """2D analytic hillshade at 1x vertical scale, sun NW at 40 degrees.
    Colors are map illustration, not game materials or vegetation rendering.
    """
    gy,gx=np.gradient(z,CELL)
    slope=np.hypot(gx,gy)
    sun=np.array([-.542,-.542,.643])
    light=(-gx*sun[0]-gy*sun[1]+sun[2])/np.sqrt(1+slope*slope)
    shade=np.clip(.30+.80*light,.24,1.12)
    if gray:
        colors=np.ones((*z.shape,3))*.68
    else:
        stops = [0,100,250,430,570,700]
        palette = np.array([[.25,.35,.25],[.36,.43,.28],[.49,.50,.34],[.56,.53,.44],[.65,.63,.59],[.84,.84,.80]])
        if kind=='desert':
            stops=[0,60,110,160,210,270]
            palette=np.array([[.63,.48,.30],[.77,.63,.42],[.68,.49,.30],[.57,.37,.24],[.73,.54,.35],[.80,.66,.46]])
        colors=np.stack([np.interp(z,stops,palette[:,i]) for i in range(3)],axis=-1)
        rock=np.clip((slope-.3)/.9,0,.8)[...,None]
        rock_color=np.array([.57,.55,.50]) if kind!='desert' else np.array([.60,.41,.27])
        colors=colors*(1-rock)+rock_color*rock
    colors*=shade[...,None]
    if water:
        filled,_,area,order,_=route(z,CELL)
        ocean=np.zeros_like(z,dtype=bool)
        ocean[[0,-1],:]=z[[0,-1],:]<0
        ocean[:,[0,-1]]=z[:,[0,-1]]<0
        ocean=binary_propagation(ocean,mask=z<0)
        colors[ocean]=[.12,.27,.34]
        lake=(filled-z>1)&~ocean
        if kind!='desert':
            colors[lake]=[.19,.38,.43]
    return Image.fromarray(np.uint8(np.clip(colors,0,1)*255))


def font(size,bold=False):
    return ImageFont.truetype('C:/Windows/Fonts/'+('segoeuib.ttf' if bold else 'segoeui.ttf'),size)


def sheet(title, subtitle, panels, path, footer, size=620):
    gap,margin=26,34
    w=margin*2+len(panels)*size+(len(panels)-1)*gap
    h=126+size+115
    image=Image.new('RGB',(w,h),'#111c24')
    d=ImageDraw.Draw(image)
    d.text((margin,20),title,font=font(29,True),fill='#f0e9dc')
    d.text((margin,62),subtitle,font=font(17),fill='#aabbc6')
    for i,(name,desc,pic) in enumerate(panels):
        x=margin+i*(size+gap)
        d.text((x,97),name,font=font(20,True),fill='#e6be73')
        image.paste(pic.resize((size,size),Image.Resampling.LANCZOS),(x,128))
        d.text((x,139+size),desc,font=font(16),fill='#b7c5cc')
    d.text((margin,h-38),footer,font=font(16),fill='#b7c5cc')
    image.save(path)


def diagnostics(z, delta, kind, path):
    filled,down,area,stream,order=route(z,CELL)
    base=np.asarray(colorize(z,kind,False,True)).copy()
    # Do not draw priority-flood routing diagonals across lake surfaces.
    channel=(area>80)&(stream>=3)&(filled-z<.5)
    for k,c in [(3,[75,159,182]),(4,[58,181,215]),(5,[128,221,248]),(6,[215,244,255])]:
        base[channel&(stream==k)]=c
    base[channel&(stream>=7)]=[255,255,255]
    scale=30
    t=np.clip(delta/scale,-1,1)[...,None]
    neutral=np.array([.80,.79,.74])
    red=np.array([.70,.20,.12])
    blue=np.array([.12,.43,.65])
    color=neutral*(1-abs(t))+np.where(t<0,red,blue)*abs(t)
    sheet('Drainage and terrain change',
          'Mountain proposal | same 4.096 km extent | channels are potential drainage, not all permanent rivers',
          [('Stream hierarchy','Blue: tributaries | pale: larger downstream channels',Image.fromarray(base)),
           ('Height change during erosion','Red: lowered | blue: raised | saturated at +/-30 m',Image.fromarray(np.uint8(color*255)))],
          path,'North is up. Map spacing: 8 m. Water availability and detailed river carving remain separate.',size=700)


def checks(z):
    filled,down,area,stream,order=route(z,CELL)
    assert np.isfinite(z).all()
    rank=np.empty(z.size,int);rank[order]=np.arange(z.size)
    valid=down>=0
    assert np.all(rank[down[valid]]<rank[valid])
    assert np.all(filled.ravel()[valid]>filled.ravel()[down[valid]])
    assert area.ravel()[~valid].sum()==z.size
    plane=np.add.outer(np.arange(9,dtype=float),np.arange(9,dtype=float))
    a=route(plane,CELL);b=route(plane,CELL)
    assert all(np.array_equal(v,w) for v,w in zip(a,b))
    assert np.isclose(np.gradient(plane,CELL)[0][4,4],.125)
    return {'finite':True,'acyclic_downhill_routing':True,'outlet_area_conserved':True,
            'routing_repeatability':True,'slope_uses_metres':True,
            'min_m':float(z.min()),'max_m':float(z.max()),
            'area_below_10_degrees_pct':float(np.mean(np.hypot(*np.gradient(z,CELL))<np.tan(np.deg2rad(10)))*100),
            'max_strahler_order':int(stream.max()),
            'potential_lake_area_pct':float(np.mean(filled-z>1)*100)}


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--iterations',type=int,default=160)
    parser.add_argument('--render-only',action='store_true')
    args=parser.parse_args()
    OUT.mkdir(parents=True,exist_ok=True)
    results={}
    cases=[('mountain',7319),('desert',7320),('coastal',7321)]
    for kind,seed in cases:
        path=OUT/(kind+'.npz')
        if args.render_only:
            data=np.load(path)
            start,z,usped,channel,seconds=[data[k] for k in ('initial','height','usped','channel','seconds')]
        else:
            start=initial(seed,kind)
            z,usped,channel,seconds=erode(start,seed,args.iterations,kind)
            if kind=='desert':z=dunes(z,seed)
            np.savez_compressed(path,initial=start,height=z,usped=usped,channel=channel,seconds=seconds,seed=seed,cell_m=CELL)
        results[kind]=checks(z)|{'seconds':float(seconds),'seed':seed,'iterations':args.iterations,
             'height_sha256':hashlib.sha256(z.tobytes()).hexdigest(),
             'mean_absolute_erosion_m':float(abs(usped+channel).mean())}
        colorize(z,kind).save(OUT/(kind+'-relief.png'))
        # Lossless height values and a viewable 16-bit elevation map.
        Image.fromarray(np.uint16((z-z.min())/(z.max()-z.min())*65535)).save(OUT/(kind+'-height16.png'))
        if kind=='mountain':
            baseline_path=OUT/'baseline-input.npy'
            if not baseline_path.exists():
                baseline=np.fromfile(ROOT/'artifacts/realistic-planets/cache/EFF758680055BEC1EB2593D9FAAC40F719C092F7ADD3AF66EAC5B86F15326DF8.rgn',dtype='<f4',offset=8,count=N*N).reshape(N,N).astype(float)
                np.save(baseline_path,baseline)
            baseline=np.load(baseline_path)
            abpath=OUT/'baseline-eroded.npz'
            if args.render_only:
                enhanced=np.load(abpath)['height']
            else:
                enhanced,u,c,s=erode(baseline,seed,args.iterations,'existing')
                np.savez_compressed(abpath,height=enhanced,usped=u,channel=c,seconds=s)
            sheet('Terrain study / existing landforms versus new proposal',
                  'Actual generated height fields | 4.096 x 4.096 km per panel | same colors, sun angle and 1x height scale',
                  [('A / Current cached terrain','Existing regional generator (seed 7319)',colorize(baseline)),
                   ('B / More erosion, same base','USPED + channel shaping on the existing terrain',colorize(enhanced)),
                   ('C / Library-based proposal','Layered Perlin base + full prototype erosion',colorize(z))],
                  OUT/'01-comparison.png','2D hillshade, not in-game materials. Blue marks potential filled basins; lake water budgets are not simulated.')
            sheet('Mountain proposal / before and after erosion',
                  f'Seed 7319 | unchanged height palette and lighting | {args.iterations} erosion iterations at 8 m spacing',
                  [('Initial height field','Power-shaped fBm with a broad regional envelope',colorize(start)),
                   ('Eroded height field','USPED flux divergence + stream-order channel shaping',colorize(z))],
                  OUT/'02-mountain.png','Map extent 4.096 km square. NW light, 40 degrees above horizon; no vertical exaggeration.',700)
            diagnostics(z,z-start,'mountain',OUT/'04-drainage.png')
    sheet('Climate studies / desert and coast',
          'Both 4.096 km square, 8 m grid | library-derived erosion with custom desert/coastal composition',
          [('Desert / mesas and sand','Eroded bedrock plus separate wind-aligned dunes',Image.open(OUT/'desert-relief.png')),
           ('Coast / connected sea','Eroded uplands, coastal gradient and basin water',Image.open(OUT/'coastal-relief.png'))],
          OUT/'03-climates.png','2D previews only. River/lake water budgets, habitat placement and game materials are not validated here.',700)
    (OUT/'metrics.json').write_text(json.dumps(results,indent=2))
    print(json.dumps(results,indent=2),flush=True)


if __name__=='__main__':main()
