using System.Numerics;
using StargateGame.Shared;
using StargateGame.Persistence;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    sealed class CableState
    {
        public required string File;
        public required Task<(long Revision,CableRecord[] Cables)> Loading;
        public bool Ready, LoadErrorLogged;
        public long Revision;
        public CableRecord[] Records=[];
        public Task? Saving;
        public CableRecord[]? Pending;
        public Session? Requester;
        public Guid Request;
        public readonly Dictionary<Session,CableAnchor> Drafts=[];
        public readonly Dictionary<Session,long> LastRequest=[];
        public readonly Dictionary<Guid,CableReply> Receipts=[];
    }
    readonly Dictionary<Guid,CableState> cableLocations=[];
    CableState GetCables()
    {
        var id=physics.World.Definition.LocationId;
        if(!cableLocations.TryGetValue(id,out var state))
        {
            string file=repository?.DatabasePath ?? fixtureDatabasePath;
            state=new(){File=file,Loading=CableStorage.Load(file,id)};cableLocations.Add(id,state);
        }
        return state;
    }
    void TickCables()
    {
        var state=GetCables();
        if(!state.Ready)
        {
            if(!state.Loading.IsCompleted)return;
            if(!state.Loading.IsCompletedSuccessfully)
            {if(!state.LoadErrorLogged){Console.Error.WriteLine("Cable load failed: "+state.Loading.Exception?.GetBaseException().Message);state.LoadErrorLogged=true;}return;}
            (state.Revision,state.Records)=state.Loading.Result;state.Ready=true;
            // Upgrade the original thin cable set once, preserving newly selected
            // 2 cm cables on subsequent loads through a stored style version.
            if(state.Records.Any(c=>c.StyleVersion==0))
                SaveCables(state,state.Records.Select(c=>c.StyleVersion==0?c with{Diameter=.05f,StyleVersion=1}:c).ToArray(),null,Guid.Empty);
        }
        foreach(var gone in state.Drafts.Keys.Where(s=>!sessions.Contains(s)).ToArray()){state.Drafts.Remove(gone);state.LastRequest.Remove(gone);}
        if(state.Saving is {IsCompleted:true} saving)
        {
            bool success=saving.IsCompletedSuccessfully;
            if(success){state.Records=state.Pending!;state.Revision++;}
            else Console.Error.WriteLine("Cable save failed: "+saving.Exception?.GetBaseException().Message);
            if(state.Requester is {} requester)
            {
                var reply=new CableReply(state.Request,success,success?"Cable saved.":"Cable save failed.");
                state.Receipts[state.Request]=reply;
                requester.Peer.Send(new(){Kind="cable-result",CableReply=reply,LocationId=physics.World.Definition.LocationId});
                if(state.Receipts.Count>256)state.Receipts.Remove(state.Receipts.Keys.First());
            }
            state.Saving=null;state.Pending=null;state.Requester=null;
            if(success)foreach(var peer in sessions.Where(s=>s.PlayerId!=0))SendCables(peer,state);
        }
        if(state.Saving is null && physics.Tick%60==0)
        {
            var kept=state.Records.Where(c=>physics.CableOwnerExists(c.A)&&physics.CableOwnerExists(c.B)).ToArray();
            if(kept.Length!=state.Records.Length)SaveCables(state,kept,null,Guid.Empty);
        }
    }
    void SaveCables(CableState state,CableRecord[] records,Session? requester,Guid request)
    {
        state.Pending=records;state.Requester=requester;state.Request=request;
        state.Saving=CableStorage.Save(state.File,physics.World.Definition.LocationId,state.Revision+1,records);
    }
    void SendCables(Session session,CableState state)
    {
        int count=Math.Max(1,(state.Records.Length+31)/32);
        for(int i=0;i<count;i++)session.Peer.Send(new(){Kind="cables",LocationId=physics.World.Definition.LocationId,Cables=new(state.Revision,i,count,state.Records.Skip(i*32).Take(32).ToArray())});
    }
    bool HandleCables(Session session,Message message)
    {
        if(message.Kind is not ("cable-state" or "cable-command"))return false;
        var state=GetCables();
        if(message.Kind=="cable-state") {if(state.Ready && message.Cables?.Revision != state.Revision)SendCables(session,state);return true;}
        if(message.CableCommand is not {} cmd)return true;
        void Reply(bool ok,string reason)=>session.Peer.Send(new(){Kind="cable-result",LocationId=physics.World.Definition.LocationId,CableReply=new(cmd.Request,ok,reason)});
        if(state.Receipts.TryGetValue(cmd.Request,out var receipt)){session.Peer.Send(new(){Kind="cable-result",CableReply=receipt,LocationId=physics.World.Definition.LocationId});return true;}
        if(state.Saving is not null && state.Request==cmd.Request)return true;
        if(!state.Ready||state.Saving is not null){Reply(false,state.Loading.IsFaulted?"Cable storage failed to load.":"Cable storage is busy.");return true;}
        if(cmd.Request==Guid.Empty||physics.World.Definition.Fixture!="gate-pair"||HomeDebugBusy||physics.Player(session.PlayerId).TransitFrozen){Reply(false,"Cable placement unavailable.");return true;}
        long now=Environment.TickCount64;
        if(now-state.LastRequest.GetValueOrDefault(session)<100){Reply(false,"Please wait before editing another cable.");return true;}
        state.LastRequest[session]=now;
        if(cmd.Action=="cancel"){state.Drafts.Remove(session);Reply(true,"Cable cancelled.");return true;}
        var eye=Movement.EyePosition(physics.Player(session.PlayerId));
        if(cmd.Action=="remove")
        {
            var cable=state.Records.FirstOrDefault(c=>c.Id==cmd.Target);
            if(cable is null||cmd.Anchor is null||!CableRules.Valid(cmd.Anchor)){Reply(false,"No cable selected.");return true;}
            var point=cmd.Anchor.Point;
            if(Vector3.Distance(eye,point)>20||physics.InteractionBlocked(eye,Vector3.Normalize(point-eye),Math.Max(0,Vector3.Distance(eye,point)-.1f),ignorePlayer:session.PlayerId)) {Reply(false,"Cable is out of reach or obstructed.");return true;}
            if(!physics.CablePoint(cable.A,out var a,out _)||!physics.CablePoint(cable.B,out var b,out _)||Vector3.Distance(a,point)+Vector3.Distance(b,point)>Math.Max(cable.Length,Vector3.Distance(a,b))+.5f){Reply(false,"Invalid cable target.");return true;}
            SaveCables(state,state.Records.Where(c=>c.Id!=cmd.Target).ToArray(),session,cmd.Request);return true;
        }
        if(cmd.Action is not ("begin" or "place")||cmd.Anchor is null||!CableRules.Valid(cmd.Anchor)||!physics.CablePoint(cmd.Anchor,out var target,out _)) {Reply(false,"Point at a loaded surface.");return true;}
        float distance=Vector3.Distance(eye,target);
        if(distance<.01f||distance>20.2f){Reply(false,"Surface is out of reach.");return true;}
        var hit=physics.CablePick(eye,Vector3.Normalize(target-eye),session.PlayerId,Math.Min(20.2f,distance+.15f));
        if(hit is null||hit.Kind!=cmd.Anchor.Kind||hit.Owner!=cmd.Anchor.Owner||hit.Entity!=cmd.Anchor.Entity||!physics.CablePoint(hit,out var actual,out _)||Vector3.Distance(actual,target)>.15f){Reply(false,"Surface is obstructed or changed.");return true;}
        if(cmd.Action=="begin"){state.Drafts[session]=hit;Reply(true,"Choose the second surface.");return true;}
        if(!state.Drafts.TryGetValue(session,out var first)||!physics.CableOwnerExists(first)||!physics.CablePoint(first,out var start,out _)){Reply(false,"Start the cable again.");return true;}
        float span=Vector3.Distance(start,actual);
        if(span<.05f||span>100||!float.IsFinite(cmd.Slack)||cmd.Slack<0||cmd.Slack>1||cmd.Color is <0 or >4||!CableRules.Diameters.Contains(cmd.Diameter)||state.Records.Length>=256){Reply(false,"Invalid cable length, style, or cable limit reached.");return true;}
        var placed=new CableRecord(Guid.NewGuid(),first,hit,span*(1+cmd.Slack),cmd.Color,cmd.Diameter,StyleVersion:1);
        state.Drafts.Remove(session);SaveCables(state,[..state.Records,placed],session,cmd.Request);return true;
    }
}
