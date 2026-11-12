using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Physics;
using StargateGame.Shared;
namespace StargateGame.Client;
sealed partial class SceneRenderer
{
    sealed class CableVisual : IDisposable
    {
        public required CableRecord Record;
        public required Mesh3D Mesh;
        public required Vector3[] Nodes, Previous, BeforeStep;
        public Vector3[] Positions=[],Normals=[],Controls=[],Centers=[],Tangents=[],Right=[],Up=[];
        public Vector2[] Uvs=[];
        public float[] ArcLengths=[];
        public AxisAlignedBoundingBox Bounds;
        public Vector3 SurfaceA, SurfaceB, NormalA, NormalB;
        public bool Visible, Suspended, Sleeping;
        public float QuietTime;
        public Vector3[] QuietPose=[];
        public Vector3 SleepA, SleepB, SleepNormalA, SleepNormalB;
        public long CollisionRevision;
        public readonly Dictionary<(bool Player,int Id),CableObstacle> SleepObstacles=[];
        public int Sides;
        public double Accumulator;
        public void Dispose()=>Mesh.Dispose();
    }
    readonly record struct CableObstacle(Vector3 Center,Quaternion Rotation,float Radius);
    readonly Dictionary<(bool Player,int Id),CableObstacle> cableObstacles=[];
    Mesh3D? cableMarkers;
    bool cableMarkersVisible;
    readonly Vector3[] cableMarkerPositions=new Vector3[48],cableMarkerNormals=new Vector3[48];
    readonly Vector2[] cableMarkerUvs=new Vector2[48];
    public void UpdateCableMarkers(PhysicsWorld physics,BodyState[] bodies,CableAnchor? start,CableAnchor? hover)
    {
        int index=0;
        foreach(var anchor in new[]{start,hover})
        {
            if(anchor is null || !physics.CablePoint(anchor,out var p,out var n,bodies))continue;
            p+=n*.025f;
            for(int face=0;face<8;face++)
            {
                var a=new Vector3((face&1)== 0 ? .06f : -.06f,0,0);
                var b=new Vector3(0,(face&2)== 0 ? .06f : -.06f,0);
                var c=new Vector3(0,0,(face&4)== 0 ? .06f : -.06f);
                var normal=Vector3.Normalize(a+b+c);
                cableMarkerPositions[index]=p+a;cableMarkerNormals[index++]=normal;
                cableMarkerPositions[index]=p+b;cableMarkerNormals[index++]=normal;
                cableMarkerPositions[index]=p+c;cableMarkerNormals[index++]=normal;
            }
        }
        cableMarkersVisible=index>0;
        if(index==0)return;
        for(int i=index;i<48;i++){cableMarkerPositions[i]=cableMarkerPositions[0];cableMarkerNormals[i]=Vector3.UnitY;}
        cableMarkers??=Own(graphics.CreateMesh3D());cableMarkers.SetVertices(cableMarkerPositions);cableMarkers.SetNormals(cableMarkerNormals);cableMarkers.SetUVs(cableMarkerUvs);
    }
    readonly Dictionary<Guid,CableVisual> cableVisuals=[];
    Guid cableSceneLocation;
    double cableLastTime;
    int cableRoundRobin;
    public bool CableToolEquipped { get; set; }
    public Guid HighlightCable {get;set;}
    public long CableGpuBytes => cableVisuals.Values.Sum(v=>(long)v.Positions.Length*32);
    public int CableRaycasts {get;private set;}
    public void UpdateCables(PhysicsWorld physics,CableRecord[] records,BodyState[] bodies,Camera camera,double now,CableRecord? preview)
    {
        if(cableSceneLocation!=physics.World.Definition.LocationId){ClearCables();cableSceneLocation=physics.World.Definition.LocationId;}
        double dt=Math.Clamp(now-cableLastTime,0,.067);cableLastTime=now;
        var all=preview is null?records:records.Append(preview).ToArray();
        var keep=all.Select(c=>c.Id).ToHashSet();
        foreach(var id in cableVisuals.Keys.Where(id=>!keep.Contains(id)).ToArray()){cableVisuals[id].Dispose();cableVisuals.Remove(id);}
        int budget=4096;CableRaycasts=0;
        cableObstacles.Clear();
        foreach(var body in bodies)
        {
            var prop=physics.World.Definition.Props.FirstOrDefault(p=>p.Id==body.Id);
            if(prop is not null)cableObstacles[(false,body.Id)]=new(body.Position,body.Rotation,prop.HalfExtent.Length());
        }
        foreach(var player in physics.Players)
            cableObstacles[(true,player.Id)]=new(player.Position+Vector3.UnitY*.9f,Quaternion.Identity,1.1f);
        for(int order=0;order<all.Length;order++)
        {
            var record=all[(order+cableRoundRobin)%all.Length];
            if(!physics.CablePoint(record.A,out var a,out var an,bodies)||!physics.CablePoint(record.B,out var b,out var bn,bodies))
            {if(cableVisuals.TryGetValue(record.Id,out var hidden))hidden.Visible=false;continue;}
            var surfaceA=a;var surfaceB=b;
            float radius=record.Diameter*.5f;a+=an*radius;b+=bn*radius;
            float length=Math.Max(record.Length,Vector3.Distance(a,b));
            int desiredSegments=Math.Clamp((int)MathF.Ceiling(record.Length/.5f),8,128);
            if(cableVisuals.TryGetValue(record.Id,out var resized) && resized.Nodes.Length!=desiredSegments+1)
            {resized.Dispose();cableVisuals.Remove(record.Id);}
            if(!cableVisuals.TryGetValue(record.Id,out var v))
            {
                int segments=Math.Clamp((int)MathF.Ceiling(record.Length/.5f),8,128);
                var nodes=new Vector3[segments+1];
                float sag=.5f*MathF.Sqrt(Math.Max(0,record.Length*record.Length-Vector3.DistanceSquared(a,b)));
                for(int i=0;i<=segments;i++){float t=(float)i/segments;nodes[i]=Vector3.Lerp(a,b,t)-Vector3.UnitY*(4*t*(1-t)*sag);}
                v=new(){Record=record,Mesh=graphics.CreateMesh3D(),Nodes=nodes,Previous=(Vector3[])nodes.Clone(),BeforeStep=(Vector3[])nodes.Clone()};cableVisuals.Add(record.Id,v);
            }
            bool attachmentsMoved=v.SurfaceA!=surfaceA||v.SurfaceB!=surfaceB||v.NormalA!=an||v.NormalB!=bn;
            bool shapeChanged=v.Record.Length!=record.Length||v.Record.Diameter!=record.Diameter||v.Record.A!=record.A||v.Record.B!=record.B;
            v.Record=record;
            v.SurfaceA=surfaceA;v.SurfaceB=surfaceB;v.NormalA=an;v.NormalB=bn;
            var lo=Vector3.Min(a,b);var hi=Vector3.Max(a,b);
            foreach(var node in v.Nodes){lo=Vector3.Min(lo,node);hi=Vector3.Max(hi,node);}
            bool nearby=Vector3.Distance(camera.Position,Vector3.Clamp(camera.Position,lo,hi))<150;
            bool resumed=!v.Visible||v.Suspended;v.Visible=true;
            if(!nearby){v.Suspended=true;v.Accumulator=0;if(v.Positions.Length==0||attachmentsMoved)BuildCableTube(v,camera.Position,1);continue;}
            v.Suspended=false;
            if(resumed){Array.Copy(v.Nodes,v.Previous,v.Nodes.Length);Array.Copy(v.Nodes,v.BeforeStep,v.Nodes.Length);}
            bool wake=resumed||shapeChanged||v.CollisionRevision!=physics.CableCollisionRevision;
            v.CollisionRevision=physics.CableCollisionRevision;
            if(v.Sleeping)wake|=Vector3.DistanceSquared(v.SleepA,surfaceA)>2.5e-7f||Vector3.DistanceSquared(v.SleepB,surfaceB)>2.5e-7f
                ||Vector3.DistanceSquared(v.SleepNormalA,an)>1e-6f||Vector3.DistanceSquared(v.SleepNormalB,bn)>1e-6f||CableObstaclesMoved(v,lo,hi);
            if(wake){v.Sleeping=false;v.QuietTime=0;}
            if(v.Sleeping)
            {
                v.Accumulator=0;
                int sides=Vector3.Distance(camera.Position,(a+b)*.5f)<40?12:6;
                if(attachmentsMoved||v.Sides!=sides)BuildCableTube(v,camera.Position,1);
                continue;
            }
            v.Accumulator=Math.Min(v.Accumulator+dt,2d/30);
            int count=v.Nodes.Length;
            while(v.Accumulator>=1d/30 && budget>=count*3)
            {
                v.Accumulator-=1d/30;Array.Copy(v.Nodes,v.BeforeStep,count);
                for(int i=1;i<count-1;i++)
                {
                    var old=v.Nodes[i];v.Nodes[i]+=(v.Nodes[i]-v.Previous[i])*.94f-Vector3.UnitY*(9.81f/900);
                    v.Previous[i]=old;
                }
                float segment=length/(count-1);
                for(int iteration=0;iteration<8;iteration++)
                {
                    v.Nodes[0]=a;v.Nodes[^1]=b;
                    for(int step=0;step<count-1;step++)
                    {
                        int i=(iteration&1)==0?step:count-2-step;
                        var delta=v.Nodes[i+1]-v.Nodes[i];float d=delta.Length();if(d<1e-6f)continue;
                        var correction=delta*((d-segment)/d);
                        if(i==0)v.Nodes[i+1]-=correction;
                        else if(i+1==count-1)v.Nodes[i]+=correction;
                        else{v.Nodes[i]+=correction*.5f;v.Nodes[i+1]-=correction*.5f;}
                    }
                }
                v.Nodes[0]=a;v.Nodes[^1]=b;
                for(int i=1;i<count-1&&budget>0;i++)
                {
                    var motion=v.Nodes[i]-v.BeforeStep[i];
                    if(motion.LengthSquared()>1e-10f)
                    {
                        budget--;CableRaycasts++;
                        // Sweep the leading edge, not just the centre. Otherwise a resting
                        // node sinks by its radius before repeatedly popping back out.
                        if(physics.CableRay(v.BeforeStep[i],v.Nodes[i]+Vector3.Normalize(motion)*(radius+.002f)) is {} contact)
                            ProjectCableContact(v,i,contact.Point,contact.Normal,radius);
                    }
                    // Retain support even when the length solve makes motion horizontal.
                    budget--;CableRaycasts++;
                    if(physics.CableRay(v.Nodes[i]+Vector3.UnitY*.002f,v.Nodes[i]-Vector3.UnitY*(radius+.004f)) is {} support)
                        ProjectCableContact(v,i,support.Point,support.Normal,radius);
                }
                for(int i=0;i<count-1&&budget>0;i++)
                {
                    var from=v.Nodes[i];var to=v.Nodes[i+1];var delta=to-from;
                    if(delta.LengthSquared()<1e-8f)continue;
                    var direction=Vector3.Normalize(delta);
                    // Ignore only the tiny seating region at either attachment.
                    if(i==0)from+=direction*Math.Min(radius*2,.04f);
                    if(i==count-2)to-=direction*Math.Min(radius*2,.04f);
                    budget--;CableRaycasts++;
                    if(physics.CableRay(from,to) is {} contact)
                    {
                        // Keep the tangential positions. Snapping a whole node to the
                        // span hit shortened it and fought the length solve every tick.
                        if(i>0)ProjectCableContact(v,i,contact.Point,contact.Normal,radius);
                        if(i+1<count-1)ProjectCableContact(v,i+1,contact.Point,contact.Normal,radius);
                    }
                }
                if(v.QuietPose.Length!=count)v.QuietPose=new Vector3[count];
                if(v.QuietTime==0)Array.Copy(v.BeforeStep,v.QuietPose,count);
                bool quiet=!shapeChanged;
                for(int i=0;i<count&&quiet;i++)quiet=Vector3.DistanceSquared(v.Nodes[i],v.BeforeStep[i])<4e-6f
                    &&Vector3.DistanceSquared(v.Nodes[i],v.QuietPose[i])<16e-6f;
                v.QuietTime=quiet?v.QuietTime+1f/30:0;
                if(v.QuietTime>=.75f && record.Id!=Guid.Empty)
                {
                    v.Sleeping=true;v.Accumulator=0;
                    Array.Copy(v.Nodes,v.Previous,count);Array.Copy(v.Nodes,v.BeforeStep,count);
                    v.SleepA=surfaceA;v.SleepB=surfaceB;v.SleepNormalA=an;v.SleepNormalB=bn;
                    v.SleepObstacles.Clear();foreach(var obstacle in cableObstacles)v.SleepObstacles.Add(obstacle.Key,obstacle.Value);
                    break;
                }
            }
            // Endpoints always track presented transforms, even when the ray budget is exhausted.
            v.Nodes[0]=a;v.Nodes[^1]=b;
            BuildCableTube(v,camera.Position,(float)Math.Clamp(v.Accumulator*30,0,1));
        }
        if(all.Length>0)cableRoundRobin=(cableRoundRobin+1)%all.Length;
    }
    static void ProjectCableContact(CableVisual v,int node,Vector3 point,Vector3 normal,float radius)
    {
        float correction=radius+.002f-Vector3.Dot(v.Nodes[node]-point,normal);
        if(correction<=0)return;
        var velocity=v.Nodes[node]-v.Previous[node];
        v.Nodes[node]+=normal*correction;
        // Projection is not a physical impulse. Remove inward motion and damp
        // sliding at contacts instead of feeding correction energy into Verlet.
        velocity-=normal*Math.Min(0,Vector3.Dot(velocity,normal));
        v.Previous[node]=v.Nodes[node]-velocity*.2f;
    }
    bool CableObstaclesMoved(CableVisual v,Vector3 lo,Vector3 hi)
    {
        foreach(var entry in cableObstacles)
        {
            var current=entry.Value;
            bool existed=v.SleepObstacles.TryGetValue(entry.Key,out var previous);
            float margin=Math.Max(current.Radius,existed?previous.Radius:0)+v.Record.Diameter+.1f;
            // Swept bounds also wake a cable crossed between presentation frames.
            var min=existed?Vector3.Min(current.Center,previous.Center):current.Center;
            var max=existed?Vector3.Max(current.Center,previous.Center):current.Center;
            if(max.X+margin<lo.X||min.X-margin>hi.X||max.Y+margin<lo.Y||min.Y-margin>hi.Y||max.Z+margin<lo.Z||min.Z-margin>hi.Z)continue;
            if(!existed||Vector3.DistanceSquared(current.Center,previous.Center)>1e-6f
                ||8*current.Radius*current.Radius*(1-Math.Min(1,Math.Abs(Quaternion.Dot(current.Rotation,previous.Rotation))))>1e-6f)return true;
        }
        foreach(var entry in v.SleepObstacles)
            if(!cableObstacles.ContainsKey(entry.Key)&&Vector3.Distance(entry.Value.Center,Vector3.Clamp(entry.Value.Center,lo,hi))<entry.Value.Radius+v.Record.Diameter+.1f)return true;
        return false;
    }
    void BuildCableTube(CableVisual v,Vector3 camera,float alpha)
    {
        int sides=Vector3.Distance(camera,(v.Nodes[0]+v.Nodes[^1])*.5f)<40?12:6;
        // Round each solver corner with a quadratic curve between adjacent
        // midpoints. The curve stays inside its control polygon, avoiding
        // Catmull-Rom overshoot and sharp zigzags. End attachments stay exact.
        const int subdivisions=4;
        // Seat the end rings on their owner surfaces. Extra normal-aligned
        // controls form short straight exits before blending into the rope.
        // Keep collision solver nodes separate: these are visual attachment collars.
        int controlCount=v.Nodes.Length+2;
        if(v.Controls.Length!=controlCount)v.Controls=new Vector3[controlCount];
        float exitLength=Math.Min(Math.Max(v.Record.Diameter*2,.08f),Vector3.Distance(v.SurfaceA,v.SurfaceB)*.25f);
        v.Controls[0]=v.SurfaceA;
        v.Controls[1]=v.SurfaceA+v.NormalA*exitLength;
        for(int i=1;i<v.Nodes.Length-1;i++)v.Controls[i+1]=Vector3.Lerp(v.BeforeStep[i],v.Nodes[i],alpha);
        v.Controls[^2]=v.SurfaceB+v.NormalB*exitLength;
        v.Controls[^1]=v.SurfaceB;
        int count=(controlCount-2)*subdivisions+3, vertices=((count-1)*sides*2+sides*2)*3;
        if(v.Positions.Length!=vertices){v.Positions=new Vector3[vertices];v.Normals=new Vector3[vertices];v.Uvs=new Vector2[vertices];}
        v.Sides=sides;int index=0;float radius=v.Record.Diameter*.5f;
        if(v.Centers.Length!=count){v.Centers=new Vector3[count];v.Tangents=new Vector3[count];v.Right=new Vector3[count];v.Up=new Vector3[count];v.ArcLengths=new float[count];}
        var centers=v.Centers;var tangents=v.Tangents;var right=v.Right;var up=v.Up;
        int sample=0;centers[sample++]=v.Controls[0];
        for(int i=1;i<controlCount-1;i++)
        {
            var a=(v.Controls[i-1]+v.Controls[i])*.5f;
            var b=(v.Controls[i]+v.Controls[i+1])*.5f;
            for(int j=0;j<subdivisions;j++)
            {
                float t=(float)j/subdivisions,u=1-t;
                centers[sample++]=u*u*a+2*u*t*v.Controls[i]+t*t*b;
            }
        }
        centers[sample++]=(v.Controls[^2]+v.Controls[^1])*.5f;
        centers[sample]=v.Controls[^1];
        v.ArcLengths[0]=0;
        for(int i=1;i<count;i++)v.ArcLengths[i]=v.ArcLengths[i-1]+Vector3.Distance(centers[i-1],centers[i]);
        for(int i=0;i<count;i++)
        {
            var delta=centers[Math.Min(count-1,i+1)]-centers[Math.Max(0,i-1)];
            tangents[i]=i==0?v.NormalA:i==count-1?-v.NormalB:
                delta.LengthSquared()>1e-9f?Vector3.Normalize(delta):Vector3.UnitY;
            var r=i==0?Vector3.Cross(tangents[i],Math.Abs(tangents[i].Y)<.9f?Vector3.UnitY:Vector3.UnitX):right[i-1]-tangents[i]*Vector3.Dot(right[i-1],tangents[i]);
            if(r.LengthSquared()<1e-8f)r=Vector3.Cross(tangents[i],Math.Abs(tangents[i].Y)<.9f?Vector3.UnitY:Vector3.UnitX);
            right[i]=Vector3.Normalize(r);up[i]=Vector3.Cross(tangents[i],right[i]);
        }
        Vector3 Radial(int i,int j)=>right[i]*MathF.Cos(j*MathF.Tau/sides)+up[i]*MathF.Sin(j*MathF.Tau/sides);
        void Vertex(int i,int j){var n=Radial(i,j);v.Positions[index]=centers[i]+n*radius;v.Normals[index]=n;v.Uvs[index++]=new((float)j/sides,v.ArcLengths[i]);}
        for(int i=0;i<count-1;i++)for(int j=0;j<sides;j++)
        {Vertex(i,j);Vertex(i+1,j);Vertex(i+1,j+1);Vertex(i,j);Vertex(i+1,j+1);Vertex(i,j+1);}
        foreach(int end in new[]{0,count-1})for(int j=0;j<sides;j++)
        {
            var n=tangents[end]*(end==0?-1:1);
            v.Positions[index]=centers[end];v.Normals[index]=n;v.Uvs[index++]=Vector2.Zero;
            foreach(int k in new[]{j,j+1}){v.Positions[index]=centers[end]+Radial(end,k)*radius;v.Normals[index]=n;v.Uvs[index++]=Vector2.Zero;}
        }
        var min=v.Positions[0];var max=min;foreach(var p in v.Positions){min=Vector3.Min(min,p);max=Vector3.Max(max,p);}v.Bounds=new(min,max);
        v.Mesh.SetVertices(v.Positions);v.Mesh.SetNormals(v.Normals);v.Mesh.SetUVs(v.Uvs);
    }
    void DrawCables(RenderPass pass,ShaderProgram program,ViewFrustum frustum,bool depth)
    {
        program.SetUniform("uTravelPlane",Vector4.Zero);program.SetUniform("uRockSnow",0);program.SetUniform("uDhdMatte",0);program.SetUniform("uRifleFinish",0);program.SetUniform("uArmSkin",0);
        program.SetUniform("uTerrain",0);program.SetUniform("uSkinning",0);program.SetUniform("uTreeMode",0);program.SetUniform("uTreeVertices",0);
        program.SetUniform("uHasModelNormal",0);program.SetUniform("uHasMetallicRoughness",0);program.SetUniform("uModelHeightScale",0f);program.SetUniform("uRoughness",.9f);program.SetUniform("uFoliageCutoff",0f);
        using var state=pass.PushState(pass.State with{CullMode=CullMode.None});
        foreach(var v in cableVisuals.Values)
        {
            if(!v.Visible||v.Positions.Length==0||!frustum.Intersects(v.Bounds)||depth&&v.Record.Id==Guid.Empty)continue;
            program.SetUniform("uTint",v.Record.Id==HighlightCable||v.Record.Id==Guid.Empty?new Vector3(.15f,.9f,.55f):CableRules.Colors[v.Record.Color]);
            Mesh(pass,v.Mesh,white,program);
        }
        if(!depth && cableMarkersVisible && cableMarkers is not null)
        {program.SetUniform("uTint",new Vector3(.1f,.9f,.6f));Mesh(pass,cableMarkers,white,program);}
        program.SetUniform("uTint",Vector3.One);
    }
    public (Guid Id,Vector3 Point)? PickCable(Vector3 origin,Vector3 direction)
    {
        float best=20;Guid id=Guid.Empty;Vector3 point=default;
        foreach(var v in cableVisuals.Values.Where(v=>v.Visible&&v.Record.Id!=Guid.Empty))
        for(int i=0;i<v.Centers.Length-1;i++)
        {
            var a=v.Centers[i];var segment=v.Centers[i+1]-a;var offset=origin-a;
            float c=segment.LengthSquared(),b=Vector3.Dot(direction,segment),d=Vector3.Dot(direction,offset),e=Vector3.Dot(segment,offset);
            float denominator=c-b*b;if(denominator<1e-8f)continue;
            float along=Math.Clamp((e-b*d)/denominator,0,1);var p=a+segment*along;float ray=Vector3.Dot(p-origin,direction);
            if(ray>0&&ray<best&&Vector3.DistanceSquared(origin+direction*ray,p)<MathF.Pow(Math.Max(.06f,v.Record.Diameter),2)){best=ray;id=v.Record.Id;point=p;}
        }
        return id==Guid.Empty?null:(id,point);
    }
    void ClearCables(){foreach(var v in cableVisuals.Values)v.Dispose();cableVisuals.Clear();}
}
