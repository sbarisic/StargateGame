using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    // Match the same convex hull and gate plane used by the native prop body.
    // Completed transit proxies have an empty collider and cannot block a door.
    public IEnumerable<PropGeometry.Hull> DoorPropHulls(BodyState body, PropDefinition definition)
        => PropGeometry.Parts(definition).Select(hull => DoorPropHull(body, hull)).OfType<PropGeometry.Hull>();
    PropGeometry.Hull? DoorPropHull(BodyState body,PropGeometry.Hull original)
    {
        if(completedProps.ContainsKey(body.Id))return null;
        var world=original.Vertices.Select(v=>Vector3.Transform(v,body.Rotation)+body.Position).ToArray();
        if(!propClips.TryGetValue(body.Id,out var gate))return new(world,original.Edges,original.Faces);
        List<Vector3> vertices=[],cap=[];List<(int,int,int)> faces=[];List<(int,int)> edges=[];
        void Polygon(List<Vector3> polygon)
        {
            if(polygon.Count<3)return;
            int start=vertices.Count;vertices.AddRange(polygon);
            for(int i=0;i<polygon.Count;i++)edges.Add((start+i,start+(i+1)%polygon.Count));
            for(int i=1;i+1<polygon.Count;i++)faces.Add((start,start+i,start+i+1));
        }
        foreach(var (a,b,c) in original.Faces)
        {
            Vector3[] triangle=[world[a],world[b],world[c]];List<Vector3> polygon=[];
            for(int i=0;i<3;i++)
            {
                var x=triangle[i];var y=triangle[(i+1)%3];float dx=gate.Distance(x),dy=gate.Distance(y);
                if(dx>=0)polygon.Add(x);
                if((dx>=0)!=(dy>=0)){var point=Vector3.Lerp(x,y,dx/(dx-dy));polygon.Add(point);cap.Add(point);}
            }
            Polygon(polygon);
        }
        if(cap.Count>=3)
        {
            var points=cap.Distinct().ToArray();var center=points.Aggregate(Vector3.Zero,(sum,p)=>sum+p)/points.Length;
            var u=Vector3.Normalize(Vector3.Cross(gate.Normal,Math.Abs(gate.Normal.Y)<.9f?Vector3.UnitY:Vector3.UnitX));
            var v=Vector3.Cross(gate.Normal,u);
            Polygon(points.OrderBy(p=>MathF.Atan2(Vector3.Dot(p-center,v),Vector3.Dot(p-center,u))).ToList());
        }
        return vertices.Count<4?null:new(vertices.ToArray(),edges.ToArray(),faces.ToArray());
    }
}
