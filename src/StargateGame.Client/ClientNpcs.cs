using System.Numerics;
using StargateGame.Shared;
namespace StargateGame.Client;
sealed partial class ClientSession
{
    public NpcSnapshot Npcs {get;private set;}=new(-1,0,[],[]);
    public NpcReply? LastNpcReply {get;private set;}
    public Guid LastSpawnedNpc {get;private set;}
    string npcAction="";
    public void ClearNpcFeedback(){if(!NpcPending)LastNpcReply=null;}
    public bool NpcPending=>npcRequest!=Guid.Empty;
    public NpcDebugSnapshot? NpcDiagnostics {get;private set;}
    public NpcDebugLine[] NpcDebugLines {get;private set;}=[];
    readonly List<NpcDebugLine> npcDebugReceiving=[];
    int npcDebugPart;
    readonly SortedDictionary<long,NpcMotion[]> npcFrames=[];
    Guid npcLocation,npcRequest;
    double nextNpcPoll,npcDeadline;
    public void Npc(NpcCommand command)
    {
        if(NpcPending||TransitLocked)return;
        npcRequest=command.Request;npcDeadline=Now+10;LastNpcReply=null;npcAction=command.Action;
        connection.Peer.Send(new(){Kind="npc-command",NpcCommand=command,LocationId=World.Definition.LocationId});
    }
    void PollNpcs()
    {
        if(npcLocation!=World.Definition.LocationId)
        {npcLocation=World.Definition.LocationId;Npcs=new(-1,0,[],[]);npcFrames.Clear();npcRequest=Guid.Empty;nextNpcPoll=0;LastNpcReply=null;LastSpawnedNpc=Guid.Empty;npcAction="";NpcDebugLines=[];NpcDiagnostics=null;npcDebugReceiving.Clear();}
        if(NpcPending&&Now>npcDeadline){LastNpcReply=new(npcRequest,false,"NPC request timed out; refresh before retrying.");npcRequest=Guid.Empty;}
        if(Now<nextNpcPoll)return;nextNpcPoll=Now+2;
        connection.Peer.Send(new(){Kind="npc-state",LocationId=npcLocation});
    }
    bool HandleNpcs(Message m)
    {
        if(m.Kind is not ("npcs" or "npc-result" or "npc-debug"))return false;
        if(m.LocationId!=World.Definition.LocationId)return true;
        if(m.NpcDebug is {} debug)
        {
            if(debug.Lines is null||debug.Lines.Length>200||debug.Count is <1 or >4096||debug.Part<0||debug.Part>=debug.Count)throw new InvalidDataException("Invalid NPC diagnostics.");
            if(debug.Part==0){npcDebugReceiving.Clear();npcDebugPart=0;NpcDiagnostics=debug;}
            if(debug.Part!=npcDebugPart++)throw new InvalidDataException("NPC diagnostics fragment order.");
            npcDebugReceiving.AddRange(debug.Lines);
            if(npcDebugPart==debug.Count)NpcDebugLines=npcDebugReceiving.ToArray();
            return true;
        }
        if(m.NpcReply is {} reply&&reply.Request==npcRequest)
        {LastNpcReply=reply;npcRequest=Guid.Empty;if(reply.Accepted&&npcAction=="spawn")LastSpawnedNpc=reply.Target;}
        if(m.Npcs is {} state)
        {
            if(state.Records is null||state.Records.Length>NpcRules.Limit||state.Records.Any(n=>n is null||!NpcRules.Valid(n)||n.Location!=m.LocationId)||state.Records.Select(n=>n.Id).Distinct().Count()!=state.Records.Length||state.Status is null||state.Status.Length>NpcRules.Limit||state.Status.Any(s=>s.Path is null||s.Path.Length>32||s.Path.Any(p=>!NpcRules.Finite(p))))
                throw new InvalidDataException("Invalid NPC lifecycle stream.");
            if(state.Revision>=Npcs.Revision)Npcs=state;
        }
        return true;
    }
    void RecordNpcFrame(PhysicsSnapshot state)
    {
        npcFrames[state.Tick]=state.Npcs??[];
        while(npcFrames.Count>120)npcFrames.Remove(npcFrames.Keys.First());
    }
    public NpcMotion[] PresentedNpcs()
    {
        double tick=clock.Estimate(Now)-6;
        if(npcFrames.Count==0)return LastSnapshot.Npcs??[];
        var before=npcFrames.LastOrDefault(p=>p.Key<=tick);
        if(before.Value is null)return [];
        var after=npcFrames.FirstOrDefault(p=>p.Key>tick);
        if(after.Value is null)return before.Value;
        float alpha=(float)Math.Clamp((tick-before.Key)/(after.Key-before.Key),0,1);
        return before.Value.Where(n=>n.EffectiveTick<=tick).Select(n=>
        {
            var b=after.Value.FirstOrDefault(b=>b.Id==n.Id);
            if(b.Id!=n.Id)return n;
            return n with{Position=Vector3.Lerp(n.Position,b.Position,alpha),Velocity=Vector3.Lerp(n.Velocity,b.Velocity,alpha),Yaw=n.Yaw+MathF.IEEERemainder(b.Yaw-n.Yaw,MathF.Tau)*alpha};
        }).ToArray();
    }
}
