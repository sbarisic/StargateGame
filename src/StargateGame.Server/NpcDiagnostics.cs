using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.Building;
namespace StargateGame.Server;
public sealed partial class GameServer
{
    object[] NpcMovementDiagnostics()=>GetNpcs().Actors.Values.Select(a=>
    {
        var position=a.Record.Position;
        var waypoint=a.Path.Length>0?a.Path[Math.Clamp(a.Corner,0,a.Path.Length-1)]:position;
        var delta=waypoint-position;delta.Y=0;
        var end=position+(delta.Length()>1.2f?Vector3.Normalize(delta)*1.2f:delta);
        bool surface=GetNpcs().Navigation.TrySurfacePath(position,end,out var projected);
        var hits=new List<object>();
        float fraction=physics.NpcObstacleFraction(a.Record.Id,position,projected,(body,axis,t)=>hits.Add(new{body,axis,t}));
        return (object)new{a.Record.Id,position,a.Corner,waypoint,a.Intent,a.Retries,a.WaitingDoor,surface,projected,fraction,hits};
    }).ToArray();
    void SendNpcDebug(Session session,NpcLocation l,Vector3 eye)
    {
        var tiles=l.Navigation.Inspect();var lines=new List<NpcDebugLine>();
        void Line(Vector3 a,Vector3 b,Vector3 color)=>lines.Add(new(a,b,color));
        foreach(var t in tiles)
        {
            var c=new Vector3((t.X+.5f)*l.Navigation.TileSize,eye.Y,(t.Z+.5f)*l.Navigation.TileSize);
            if(Vector3.DistanceSquared(eye,c)>80*80)continue;
            var color=t.State=="Ready"?new Vector3(0,.8f,.3f):new Vector3(1,.4f,0);
            for(int i=0;i+1<t.Edges.Length;i+=2)Line(t.Edges[i]+Vector3.UnitY*.04f,t.Edges[i+1]+Vector3.UnitY*.04f,color);
            float size=l.Navigation.TileSize;var a=new Vector3(t.X*size,eye.Y-1.5f,t.Z*size);
            Line(a,a+Vector3.UnitX*size,color);Line(a,a+Vector3.UnitZ*size,color);
        }
        foreach(var door in physics.NavigationDoors())Line(door.A+Vector3.UnitY*.3f,door.B+Vector3.UnitY*.3f,door.Open?new(0,1,0):new(1,.5f,0));
        foreach(var a in l.Actors.Values)
        {
            var p=a.Record.Position;var eyeNpc=p+Vector3.UnitY*1.6f;
            for(int i=1;i<a.Path.Length;i++)Line(a.Path[i-1]+Vector3.UnitY*.15f,a.Path[i]+Vector3.UnitY*.15f,new(0,1,1));
            float start=a.Yaw-55*MathF.PI/180;
            var prev=eyeNpc;
            for(int i=0;i<=22;i++){float angle=start+i*5*MathF.PI/180;var end=eyeNpc+new Vector3(MathF.Sin(angle),0,-MathF.Cos(angle))*40;Line(prev,end,new(.4f,.4f,.8f));prev=end;}
            Line(prev,eyeNpc,new(.4f,.4f,.8f));
            foreach(var m in a.Memory)Line(eyeNpc,m.Point,m.Danger?new(1,0,0):new(1,1,0));
            foreach(var player in physics.Players.Where(p=>Vector3.DistanceSquared(p.Position,a.Record.Position)<1600))
                Line(eyeNpc,Movement.EyePosition(player),physics.NpcSight(a.Record.Id,eyeNpc,Movement.EyePosition(player),player.Id)?new(0,.7f,0):new(.7f,0,0));
            if(a.Outcome==NavigationOutcome.Blocked){Line(p-Vector3.UnitX*.4f,p+Vector3.UnitX*.4f,new(1,0,0));Line(p-Vector3.UnitZ*.4f,p+Vector3.UnitZ*.4f,new(1,0,0));}
            Line(p+Vector3.UnitY*.3f,p+a.Intent+Vector3.UnitY*.3f,new(0,1,0));
            if(a.WaitingDoor!=Guid.Empty&&CurrentChanges.Pieces.FirstOrDefault(p=>p.Id==a.WaitingDoor) is {} door)
            {var s=CurrentChanges.Structures.Single(s=>s.Id==door.StructureId);Line(p,Vector3.Transform(Vector3.Zero,DoorGeometry.Root(s,door)),new(1,.5f,0));}
        }
        // Bounded fragments avoid the normal gameplay frame-size limit. Harness-only.
        int count=Math.Max(1,(lines.Count+199)/200);
        for(int i=0;i<count;i++)session.Peer.Send(new(){Kind="npc-debug",LocationId=l.Id,NpcDebug=new(physics.Tick,i,count,l.Navigation.CellSize,
            i==0?tiles.Select(t=>new NpcTileMetric(t.X,t.Z,t.Revision,t.State,t.Milliseconds,t.Bytes)).ToArray():[],lines.Skip(i*200).Take(200).ToArray())});
    }
}
