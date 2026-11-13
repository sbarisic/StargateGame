using System.Numerics;
using StargateGame.Shared;
using StargateGame.Physics;
namespace StargateGame.Client;
sealed partial class GameApp
{
    bool npcDebug;
    object NavigationOverlayState()=>new {
        enabled=session?.NavigationOverlay==true,
        tick=session?.NavigationDiagnostics?.Tick,
        ready=session?.NavigationDiagnostics is {Tiles.Length:>0} nav && nav.Tiles.All(t=>t.State=="Ready"),
        tiles=session?.NavigationDiagnostics?.Tiles,
        edges=session?.NavigationLines.Length??0
    };
    IEnumerable<DebugLine> NavigationDebugLines()
    {
        if(session?.NavigationOverlay!=true||session.TransitLocked)yield break;
        foreach(var line in session.NavigationLines)yield return new(line.A,line.B,line.Color);
    }
    IEnumerable<DebugLine> NpcDebugLines()
    {
        if(!npcDebug||session is null)yield break;
        foreach(var line in session.NpcDebugLines)yield return new(line.A,line.B,line.Color);
        foreach(var n in session.Physics.NpcMotions)
        {
            foreach(var line in Capsule(n.Position,new(1,0,1)))yield return line;
            var rendered=session.PresentedNpcs().FirstOrDefault(p=>p.Id==n.Id);
            if(rendered.Id==n.Id)foreach(var line in Capsule(rendered.Position,new(0,1,1)))yield return line;
        }
        static IEnumerable<DebugLine> Capsule(Vector3 p,Vector3 color)
        {
            for(int i=0;i<20;i++)
            {
                float a=i*MathF.Tau/20,b=(i+1)*MathF.Tau/20;
                var v=new Vector3(MathF.Cos(a)*Movement.Radius,0,MathF.Sin(a)*Movement.Radius);
                var w=new Vector3(MathF.Cos(b)*Movement.Radius,0,MathF.Sin(b)*Movement.Radius);
                yield return new(p+v+Vector3.UnitY*.32f,p+w+Vector3.UnitY*.32f,color);
                yield return new(p+v+Vector3.UnitY*1.48f,p+w+Vector3.UnitY*1.48f,color);
                if(i%5==0)yield return new(p+v+Vector3.UnitY*.32f,p+v+Vector3.UnitY*1.48f,color);
            }
        }
    }
}
