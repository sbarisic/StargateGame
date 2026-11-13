using System.Numerics;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    static readonly float[] npcAvoidAngles=[0,25,-25,50,-50,75,-75,100,-100,130,-130];

    Vector3 NpcSteering(NpcLocation location,NpcActor actor,Vector3 position,Vector3 waypoint,Vector3 preferred,Vector3 forward)
    {
        float distance=new Vector2(waypoint.X-position.X,waypoint.Z-position.Z).Length();
        float horizon=Math.Min(1.2f,distance);
        bool Clear(Vector3 direction)
        {
            var end=position+direction*horizon;
            return location.Navigation.TrySurfacePath(position,end,out var surface)&&
                physics.NpcObstacleFraction(actor.Record.Id,position,surface)>=.999f;
        }
        if(Clear(preferred)){actor.AvoidSide=0;return preferred;}
        // Separation must not push an otherwise valid route into the wall.
        if(Vector3.Dot(preferred,forward)<.999f&&Clear(forward)){actor.AvoidSide=0;return forward;}
        Vector3 best=Vector3.Zero;float score=float.NegativeInfinity;int side=0;
        foreach(float degrees in npcAvoidAngles)
        {
            if(degrees==0)continue;
            float angle=degrees*MathF.PI/180;
            var candidate=new Vector3(forward.X*MathF.Cos(angle)-forward.Z*MathF.Sin(angle),0,forward.X*MathF.Sin(angle)+forward.Z*MathF.Cos(angle));
            if(!Clear(candidate))continue;
            // Keep the chosen passing side while the direct route stays blocked.
            int candidateSide=Math.Sign(degrees);
            float value=Vector3.Dot(candidate,forward)+(actor.AvoidSide==candidateSide?.3f:0);
            if(value>score){score=value;best=candidate;side=candidateSide;}
        }
        if(side!=0)actor.AvoidSide=side;
        return best;
    }
}
