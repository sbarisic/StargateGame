using System.Numerics;
using StargateGame.Shared;
namespace StargateGame.WorldGen;

/// <summary>Render-only quadtree tiles. Never requests canonical chunks or scenery.</summary>
public static class RegionalMesher
{
    public static TerrainMeshData Build(PlanetPlan plan, FarTileKey key, CancellationToken cancellation = default)
    {
        if (!key.Valid) throw new ArgumentException("Invalid regional tile.");
        // A 64-128 m interior grid aliases the approved ~185 m dunes and eroded
        // tributary ridges. Keep their geometry even in large streaming tiles;
        // hierarchy still controls requests/culling, not an undersampled surface.
        int size=key.Size, step=Math.Min(8<<key.Level,16), ox=key.Origin.X*64, oz=key.Origin.Z*64;
        List<Vector3> p=[], n=[], w=[]; List<Vector4> m=[],landscape=[]; List<Vector2> ecology=[]; List<float> depths=[];
        var cache=new Dictionary<(int,int),(Vector3 P,Vector3 N,Vector4 M,float Water)>();
        (Vector3 P,Vector3 N,Vector4 M,float Water) Sample(int x,int z)
        {
            if(cache.TryGetValue((x,z),out var v)) return v;
            var s=plan.Sample(ox+x,oz+z);
            float dx=(plan.Sample(ox+x+1,oz+z).Height-plan.Sample(ox+x-1,oz+z).Height)/2000f;
            float dz=(plan.Sample(ox+x,oz+z+1).Height-plan.Sample(ox+x,oz+z-1).Height)/2000f;
            float slope=MathF.Sqrt(dx*dx+dz*dz), wet=plan.SurfaceWetness(ox+x,oz+z,s.Water!=int.MinValue);
            var e=plan.Landscape.Environment(ox+x,oz+z,slope,wet);
            float surface=s.Water==int.MinValue?float.NaN:s.Water/1000f;
            // Match canonical river-bank tapering; standing water keeps its plane.
            if(plan.WaterBodyAt(ox+x,oz+z,s.Water).Kind==WaterKind.River){
                for(int dz2=-1;dz2<=1;dz2++)for(int dx2=-1;dx2<=1;dx2++){
                    var neighbor=plan.Sample(ox+x+dx2,oz+z+dz2);if(neighbor.Water==int.MinValue)surface=Math.Min(surface,neighbor.Height/1000f);
                }
                surface=Math.Max(s.Height/1000f,surface);
            }
            v=(new(ox+x,s.Height/1000f,oz+z),Vector3.Normalize(new(-dx,1,-dz)),new(plan.Settlements.Road(ox+x,oz+z)/255f,Math.Clamp(slope*1.2f,0,1),Math.Max(wet,e.Moisture*.55f),e.Snow),surface);
            cache[(x,z)]=v;return v;
        }
        void Triangle((int X,int Z) a,(int X,int Z) b,(int X,int Z) c)
        {
            var va=Sample(a.X,a.Z);var vb=Sample(b.X,b.Z);var vc=Sample(c.X,c.Z);
            foreach(var v in new[]{va,vb,vc}) {p.Add(v.P);n.Add(v.N);m.Add(v.M);var e=plan.Landscape.Environment(v.P.X,v.P.Z);ecology.Add(new(e.Habitat==Habitat.Beach?-1:e.Forest,e.Sediment));landscape.Add(new(plan.Landscape.Drainage(v.P.X,v.P.Z),e.Sediment,(float)e.Geology,(float)e.Habitat));}
            if(!float.IsFinite(va.Water)&&!float.IsFinite(vb.Water)&&!float.IsFinite(vc.Water))return;
            float level=new[]{va.Water,vb.Water,vc.Water}.Where(float.IsFinite).Average();
            var poly=new List<(Vector3 P,float Depth)>();
            foreach(var v in new[]{va,vb,vc}) {float y=v.Water;if(!float.IsFinite(y)){
                float sum=0;int count=0;for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++){var sample=plan.Sample((int)v.P.X+dx,(int)v.P.Z+dz);if(sample.Water!=int.MinValue){sum+=sample.Water/1000f;count++;}}
                // Negative depth at dry banks is essential: zero would include
                // the entire dry half of the triangle instead of clipping it.
                y=Math.Min(v.P.Y-.001f,count>0?sum/count:level);
            }poly.Add((new(v.P.X,y,v.P.Z),y-v.P.Y));}
            var clipped=new List<(Vector3 P,float Depth)>();
            for(int i=0;i<3;i++) {var u=poly[i];var v=poly[(i+1)%3];if(u.Depth>=0)clipped.Add(u);if((u.Depth>=0)!=(v.Depth>=0)){float t=u.Depth/(u.Depth-v.Depth);clipped.Add((Vector3.Lerp(u.P,v.P,t),0));}}
            for(int i=1;i+1<clipped.Count;i++)foreach(var v in new[]{clipped[0],clipped[i],clipped[i+1]}){w.Add(v.P);depths.Add(Math.Max(0,v.Depth));}
        }
        for(int z=0;z<size;z+=step) {cancellation.ThrowIfCancellationRequested();for(int x=0;x<size;x+=step)
        {
            // All tile boundaries retain metre samples, including water intersections.
            // Adjacent hierarchy levels therefore share exactly the same edge polyline.
            List<(int,int)> ring=[];
            for(int dz=0;dz<step;dz+=x==0?1:step)ring.Add((x,z+dz));
            for(int dx=0;dx<step;dx+=z+step==size?1:step)ring.Add((x+dx,z+step));
            for(int dz=step;dz>0;dz-=x+step==size?1:step)ring.Add((x+step,z+dz));
            for(int dx=step;dx>0;dx-=z==0?1:step)ring.Add((x+dx,z));
            for(int i=0;i<ring.Count;i++)Triangle((x+step/2,z+step/2),ring[i],ring[(i+1)%ring.Count]);
        }}
        return new(p.ToArray(),n.ToArray(),m.ToArray(),w.ToArray()){Ecology=ecology.ToArray(),Landscape=landscape.ToArray(),WaterFlow=new Vector2[w.Count],WaterDepth=depths.ToArray()};
    }
}
