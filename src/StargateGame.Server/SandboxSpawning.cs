using System.Numerics;
using StargateGame.Shared;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    void SpawnSandboxProp(Session session, BuildCommand cmd)
    {
        void Reply(bool success,string reason) => BuildReply(session,new(cmd.RequestId,success,reason,CurrentChanges.Revision));
        if (physics.World.Definition.Fixture != "gate-pair" || cmd.Prefab is not ("wooden-crate" or "barrel-red"))
        { Reply(false,"Spawning is available only in Sandbox."); return; }
        var player=physics.Player(session.PlayerId);
        var eye=player.Position+Vector3.UnitY*Movement.ViewHeight(player);
        if (cmd.HitPoint is not { } target || !float.IsFinite(target.X+target.Y+target.Z+cmd.Anchor.X+cmd.Anchor.Y+cmd.Anchor.Z) ||
            Vector3.DistanceSquared(eye,cmd.Anchor)>2.25f || Vector3.DistanceSquared(target,cmd.Anchor)<.01f)
        { Reply(false,"Invalid spawn ray."); return; }
        var offset=cmd.Anchor-eye;
        if (offset.Length()>.03f && physics.InteractionBlocked(eye,Vector3.Normalize(offset),offset.Length(),ignorePlayer:session.PlayerId))
        { Reply(false,"Spawn view is obstructed."); return; }
        var aim=Vector3.Normalize(target-cmd.Anchor);
        if (physics.SpawnSurface(cmd.Anchor,aim,40,session.PlayerId) is not { } hit)
        { Reply(false,"Point at a nearby surface."); return; }
        var registry=physics.World.Definition.Props;
        if (registry.Length>=64){Reply(false,"Sandbox prop limit reached (64).");return;}
        int id=registry.Select(p=>p.Id).DefaultIfEmpty(0).Max()+1;
        var prop=cmd.Prefab=="barrel-red" ? PropDefinition.Barrel(id,hit,false) : new PropDefinition(id,hit,new(.5f),10);
        prop=prop with { PersistentId=Guid.NewGuid() };
        // Keep props upright. Back away from walls if the first resting pose overlaps.
        for(int attempt=0;attempt<9;attempt++)
        {
            var position=hit+Vector3.UnitY*(prop.HalfExtent.Y+.05f)-aim*(.03f+attempt*.2f);
            var state=new BodyState(id,position,Quaternion.Identity,Vector3.Zero,Vector3.Zero,false);
            if(!physics.CanPlaceProp(prop,state))continue;
            physics.InsertProp(prop with {Position=position},state);
            physics.Epoch++;
            Reply(true,"Prop spawned."); return;
        }
        Reply(false,"Not enough room to spawn here.");
    }
}
