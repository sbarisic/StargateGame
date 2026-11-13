using System.Numerics;
using System.Security.Cryptography;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.Persistence;
using StargateGame.Building;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    sealed record NpcObservation(Vector3 Point,long Seen,bool Danger,long Expires);
    sealed class NpcActor(NpcRecord record)
    {
        public NpcRecord Record=record;
        public NpcSchedule Schedule=NpcSchedule.Hold;
        public NpcTaskState TaskState;
        public string Task="Hold",Reason="";
        public NavigationOutcome Outcome;
        public Vector3[] Path=[];
        public Vector3 Destination,LastPosition,Intent;
        public long RouteRevision=-1,CorridorRevision,NextPlan,ProgressTick,WaitUntil,LastDanger=-1000;
        public int Corner,PatrolIndex,Retries;
        public readonly List<NpcObservation> Memory=[];
        public Guid WaitingDoor;
        public float Yaw=record.Yaw;
        public float BestRemaining=float.PositiveInfinity;
        public int AvoidSide;
    }
    sealed class NpcLocation
    {
        public required string File;
        public required Guid Id;
        public required Task<(long Revision,NpcRecord[] Records)> Loading;
        public readonly Dictionary<Guid,NpcActor> Actors=[];
        public readonly Dictionary<Guid,NpcReply> Receipts=[];
        public readonly Dictionary<Session,long> LastCommand=[];
        public readonly Dictionary<Session,long> LastNavigationDebug=[];
        public NpcNavigation Navigation=new();
        public PhysicsWorld? Physics;
        public long Revision,Published=-1,LastSavedTick,GeometryCommit=-1;
        public bool Ready,LoadFailed,Dirty;
        public Task? Saving;
        public readonly HashSet<int> HostilePlayers=[];
    }
    readonly Dictionary<Guid,NpcLocation> npcLocations=[];
    NpcLocation GetNpcs() => GetNpcs(context);
    NpcLocation GetNpcs(LocationContext target)
    {
        var id=target.Physics.World.Definition.LocationId;
        if(!npcLocations.TryGetValue(id,out var n))
        {
            string file=repository?.DatabasePath??fixtureDatabasePath;
            npcLocations[id]=n=new(){File=file,Id=id,Loading=NpcStorage.Load(file,id)};
        }
        return n;
    }
    static string NpcOwner(Session s)=>s.SavedPlayer?.Id.ToString()??Convert.ToHexString(SHA256.HashData(s.IdentityKey??[]));
    void TickNpcs()
    {
        var location=GetNpcs();
        if(!location.Ready)
        {
            if(!location.Loading.IsCompleted||location.LoadFailed)return;
            if(!location.Loading.IsCompletedSuccessfully){location.LoadFailed=true;Console.Error.WriteLine("NPC load failed: "+location.Loading.Exception?.GetBaseException().Message);return;}
            var loaded=location.Loading.Result;location.Revision=loaded.Revision;
            foreach(var r in loaded.Records)location.Actors.Add(r.Id,new(r with{EffectiveTick=physics.Tick,
                Order=r.Order.Kind==NpcOrderKind.Follow?new(NpcOrderKind.Hold,[r.Position]):r.Order}));location.Ready=true;
        }
        if(location.Physics!=physics)
        {
            location.Physics=physics;var replacement=location.Navigation.Replacement();location.Navigation.Dispose();location.Navigation=replacement;
            foreach(var a in location.Actors.Values){a.Path=[];a.RouteRevision=-1;}
        }
        foreach(var a in location.Actors.Values)
            if(!npcFlights.ContainsKey(a.Record.Id)&&!physics.NpcMotions.Any(m=>m.Id==a.Record.Id)&&physics.HasTerrain(a.Record.Position))
                physics.AddNpc(a.Record.Id,a.Record.Position,a.Record.Yaw);
        if((physics.Tick%6==0||location.GeometryCommit!=physics.NavigationCommitVersion)&&physics.Players.Any(p=>!p.TransitFrozen))
        {location.Navigation.Pump(physics);location.GeometryCommit=physics.NavigationCommitVersion;}
        foreach(var motion in physics.NpcMotions)
        {
            if(!location.Actors.TryGetValue(motion.Id,out var a))continue;
            a.Record=a.Record with{Position=motion.Position,Yaw=motion.Yaw};
            if(a.Path.Length>0&&location.Navigation.CorridorRevision(a.Path)==long.MinValue)
            {a.Path=[];a.NextPlan=0;StopNpc(a,"Waiting for committed navigation",NavigationOutcome.PendingNavigation);}
            if(a.Record.Order.Kind==NpcOrderKind.Follow&&!physics.Players.Any(p=>p.Id==a.Record.Order.FollowPlayer))
            {a.Record=a.Record with{Order=new(NpcOrderKind.Hold,[motion.Position])};location.Revision++;location.Dirty=true;CancelNpcTask(a);}
            if(!physics.Players.Any(p=>!p.TransitFrozen&&Vector3.DistanceSquared(p.Position,motion.Position)<150*150))
            {a.Intent=Vector3.Zero;a.Task="Suspended";physics.SetNpcIntent(motion.Id,Vector3.Zero,a.Yaw);continue;}
            if((physics.Tick+motion.Slot)%6==0)DecideNpc(location,a,motion);
            physics.SetNpcIntent(motion.Id,a.Intent,a.Yaw);
        }
        if(location.Saving is {IsCompleted:true} saving)
        {
            if(saving.IsFaulted){Console.Error.WriteLine("NPC save failed: "+saving.Exception?.GetBaseException().Message);location.Dirty=true;}
            location.Saving=null;
        }
        if(location.Saving is null&&(location.Dirty||physics.Tick-location.LastSavedTick>=300))SaveNpcs(location);
        if(location.Published!=location.Revision||physics.Tick%30==0)
        {foreach(var s in sessions.Where(s=>s.PlayerId!=0))SendNpcs(s,location);location.Published=location.Revision;}
    }
    static void CancelNpcTask(NpcActor a)
    {a.TaskState=NpcTaskState.Cancelled;a.Path=[];a.Intent=Vector3.Zero;a.Retries=0;a.NextPlan=0;a.WaitingDoor=Guid.Empty;a.AvoidSide=0;a.BestRemaining=float.PositiveInfinity;}
    void SaveNpcs(NpcLocation location)
    {
        location.Dirty=false;location.LastSavedTick=physics.Tick;
        var records=location.Actors.Values.Select(a=>a.Record).ToArray();
        location.Saving=NpcStorage.Save(location.File,location.Id,location.Revision,records);
    }
    void SendNpcs(Session s,NpcLocation l)=>s.Peer.Send(new(){Kind="npcs",LocationId=l.Id,Npcs=new(l.Revision,physics.Tick,
        l.Actors.Values.Where(a=>!npcFlights.ContainsKey(a.Record.Id)).Select(a=>a.Record).ToArray(),l.Actors.Values.Where(a=>!npcFlights.ContainsKey(a.Record.Id)).Select(a=>new NpcStatus(a.Record.Id,a.Schedule,a.Task,a.Outcome,a.Reason,a.Path.Take(8).ToArray(),a.Memory.LastOrDefault(m=>m.Danger)?.Point)).ToArray())});
    bool HandleNpcs(Session s,Message m)
    {
        if(m.Kind is not ("npc-state" or "npc-command" or "navigation-debug"))return false;
        var l=GetNpcs();
        if(m.LocationId!=l.Id)return true;
        if(m.Kind=="npc-state"){if(l.Ready)SendNpcs(s,l);return true;}
        if(m.Kind=="navigation-debug")
        {
            // Diagnostics never share NPC command receipts or rate limits.
            if(!devPlanets||!s.Loopback||s.PlayerId==0||!l.Ready||HomeDebugBusy)return true;
            long debugNow=Environment.TickCount64;
            if(debugNow-l.LastNavigationDebug.GetValueOrDefault(s)<500)return true;
            l.LastNavigationDebug[s]=debugNow;
            var viewer=physics.Player(s.PlayerId);
            if(viewer.TransitFrozen)return true;
            _=l.Navigation.Route(viewer.Position,viewer.Position);
            SendNpcDebug(s,l,Movement.EyePosition(viewer),navigationOnly:true);
            return true;
        }
        if(m.NpcCommand is not {} cmd)return true;
        void Reply(bool ok,string text,Guid target=default)
        {
            var reply=new NpcReply(cmd.Request,ok,text,target);l.Receipts[cmd.Request]=reply;
            if(l.Receipts.Count>256)l.Receipts.Remove(l.Receipts.Keys.First());
            s.Peer.Send(new(){Kind="npc-result",LocationId=l.Id,NpcReply=reply});
        }
        if(l.Receipts.TryGetValue(cmd.Request,out var receipt)){s.Peer.Send(new(){Kind="npc-result",LocationId=l.Id,NpcReply=receipt});return true;}
        if(!l.Ready||HomeDebugBusy||physics.World.Definition.Fixture!="gate-pair"||cmd.Request==Guid.Empty)
        {Reply(false,l.LoadFailed?"NPC storage failed to load.":"NPC service is preparing.");return true;}
        long now=Environment.TickCount64;
        if(now-l.LastCommand.GetValueOrDefault(s)<100){Reply(false,"Wait before issuing another NPC command.");return true;}l.LastCommand[s]=now;
        var player=physics.Player(s.PlayerId);var eye=Movement.EyePosition(player);
        if(player.TransitFrozen){Reply(false,"NPC orders unavailable during travel.");return true;}
        if(npcFlights.ContainsKey(cmd.Target)||npcFlights.Values.Any(f=>f.Commit is not null&&(f.Source==context||f.Destination==context)))
        {Reply(false,"NPC transfer is in progress.");return true;}
        if(cmd.Action is "configure" or "debug" or "probe" or "noise" or "gunshot")
        {
            if(!devPlanets||!s.Loopback){Reply(false,"Harness developer authority required.");return true;}
            if(cmd.Action is "noise" or "gunshot")
            {if(!NpcRules.Finite(cmd.Point)){Reply(false,"Invalid stimulus position.");return true;}HearNpcs(cmd.Point,cmd.Action=="gunshot");Reply(true,"Authoritative stimulus emitted.");return true;}
            if(cmd.Action=="probe")
            {
                if(!NpcRules.Finite(cmd.Origin)||!NpcRules.Finite(cmd.Point)||Vector3.Distance(cmd.Origin,cmd.Point)>100){Reply(false,"Invalid route probe.");return true;}
                var route=l.Navigation.Route(cmd.Origin,cmd.Point);
                s.Peer.Send(new(){Kind="npc-result",LocationId=l.Id,NpcReply=new(cmd.Request,true,route.Reason,Navigation:route.Outcome,Path:route.Points)});
                return true;
            }
            if(cmd.Action=="configure")
            {
                if(cmd.CellSize is not (.1f or .15f or .2f)){Reply(false,"Navigation cell size must be 0.10, 0.15, or 0.20 m.");return true;}
                var replacement=l.Navigation.Replacement(cmd.CellSize);l.Navigation.Dispose();l.Navigation=replacement;l.HostilePlayers.Clear();
                if(cmd.HostilePlayer>0)l.HostilePlayers.Add(cmd.HostilePlayer);
                foreach(var actor in l.Actors.Values)CancelNpcTask(actor);
            }
            SendNpcDebug(s,l,eye);Reply(true,"NPC harness configuration/diagnostics ready.");return true;
        }
        if(cmd.Action=="spawn")
        {
            if(l.Actors.Count>=NpcRules.Limit){Reply(false,"Limit: 16 NPCs per location.");return true;}
            var delta=cmd.Point-eye;
            if(!NpcRules.Finite(cmd.Point)||delta.LengthSquared()<.001f){Reply(false,"Aim at a standing surface.");return true;}
            var anchor=physics.CablePick(eye,Vector3.Normalize(delta),s.PlayerId,20);
            if(anchor is null||anchor.Kind=="prop"||!physics.CablePoint(anchor,out var point,out var normal)||normal.Y<.7071f||!physics.CanPlacePlayer(point+Vector3.UnitY*.06f,s.PlayerId,crouched:false))
            {Reply(false,"No clear standing surface within 20 metres.");return true;}
            if(!physics.NpcStandingSurface(point)||physics.Players.Any(p=>Math.Abs(p.Position.Y-point.Y)<Movement.Height&&Vector2.Distance(new(p.Position.X,p.Position.Z),new(point.X,point.Z))<Movement.Radius*2))
            {Reply(false,"Standing surface is underwater or occupied by a player.");return true;}
            point+=Vector3.UnitY*.06f;
            var record=new NpcRecord(Guid.NewGuid(),"military",l.Id,NpcOwner(s),"neutral",point,player.Yaw,point,new(NpcOrderKind.Hold,[point]),physics.Tick);
            l.Actors.Add(record.Id,new(record));physics.AddNpc(record.Id,point,record.Yaw);l.Revision++;l.Dirty=true;
            _=l.Navigation.Route(point,point);Reply(true,"Soldier spawned; navigation preparing.",record.Id);return true;
        }
        if(!l.Actors.TryGetValue(cmd.Target,out var a)){Reply(false,"NPC no longer exists.");return true;}
        if(a.Record.Owner!=NpcOwner(s)&&!(devPlanets&&s.Loopback)){Reply(false,"Only the owner can order or remove this NPC.");return true;}
        var target=a.Record.Position+Vector3.UnitY*.9f;var aim=target-eye;
        if(aim.Length()>20||physics.InteractionBlocked(eye,Vector3.Normalize(aim),Math.Max(0,aim.Length()-.45f),ignorePlayer:s.PlayerId,ignoreNpc:a.Record.Id))
        {Reply(false,"NPC is out of reach or obstructed.");return true;}
        if(cmd.Action=="remove")
        {physics.RemoveNpc(a.Record.Id);l.Actors.Remove(a.Record.Id);l.Revision++;l.Dirty=true;Reply(true,"NPC removed.");return true;}
        if(cmd.Action!="order"||!NpcRules.ValidOrder(cmd.Order)||cmd.Order!.Points.Any(p=>Vector3.Distance(p,a.Record.Position)>100)||cmd.Order.Kind==NpcOrderKind.Follow&&cmd.Order.FollowPlayer!=s.PlayerId)
        {Reply(false,"Invalid NPC order or destination beyond 100 metres.");return true;}
        var order=cmd.Order.Kind==NpcOrderKind.Hold?new NpcOrder(NpcOrderKind.Hold,[a.Record.Position]):cmd.Order;
        a.Record=a.Record with{Order=order,Home=order.Kind==NpcOrderKind.Wander?order.Points[0]:a.Record.Home,EffectiveTick=physics.Tick};
        CancelNpcTask(a);l.Revision++;l.Dirty=true;Reply(true,"Order accepted.",a.Record.Id);return true;
    }
    void HearNpcs(Vector3 point,bool danger,int sourcePlayer=0,float strength=1,Guid sourceNpc=default)
    {
        var l=GetNpcs();if(!l.Ready)return;
        foreach(var a in l.Actors.Values)
        {
            if(a.Record.Id==sourceNpc)continue;
            if(a.Memory.Any(m=>m.Danger==danger&&physics.Tick-m.Seen<12))continue;
            float range=danger?50:15;if(!danger&&strength<.25f)continue;
            var eye=a.Record.Position+Vector3.UnitY*1.6f;
            if(!physics.NpcSight(a.Record.Id,eye,point))range*=.25f;
            if(Vector3.DistanceSquared(eye,point)>range*range)continue;
            // Stable uncertainty per event, with no actor tracking after a sound.
            float angle=(physics.Tick%97)*2.39996f;
            var estimate=point+new Vector3(MathF.Cos(angle),0,MathF.Sin(angle))*(danger?1.5f:.7f);
            ObserveNpc(a,estimate,danger);
        }
    }
    void ObserveNpc(NpcActor a,Vector3 point,bool danger)
    {
        a.Memory.Add(new(point,physics.Tick,danger,physics.Tick+(danger?600:300)));
        if(a.Memory.Count>16)a.Memory.RemoveAt(0);
        if(danger)a.LastDanger=physics.Tick;
    }
    void DecideNpc(NpcLocation l,NpcActor a,NpcMotion motion)
    {
        long tick=physics.Tick;var pos=motion.Position;
        a.Memory.RemoveAll(m=>m.Expires<tick);
        var forward=new Vector3(MathF.Sin(a.Yaw),0,-MathF.Cos(a.Yaw));
        foreach(var hostile in physics.Players.Where(p=>l.HostilePlayers.Contains(p.Id)&&!p.TransitFrozen))
        {
            var delta=hostile.Position-pos;var flat=new Vector3(delta.X,0,delta.Z);
            if(delta.LengthSquared()<1600&&flat.LengthSquared()>.001f&&Vector3.Dot(Vector3.Normalize(flat),forward)>.5735764f&&
                physics.NpcSight(a.Record.Id,pos+Vector3.UnitY*1.6f,Movement.EyePosition(hostile),hostile.Id))ObserveNpc(a,hostile.Position,true);
        }
        bool danger=tick-a.LastDanger<300;
        var threat=a.Memory.LastOrDefault(m=>m.Danger);
        var order=a.Record.Order;
        NpcSchedule desired=(NpcSchedule)(int)order.Kind;
        Vector3 destination=pos;
        if(danger&&threat is not null)
        {
            desired=NpcSchedule.Flee;
            var away=pos-threat.Point;away.Y=0;if(away.LengthSquared()<.01f)away=Vector3.UnitX;
            destination=pos+Vector3.Normalize(away)*12;
            destination.Y=physics.World.SampleHeight(destination.X,destination.Z);
        }
        else if(a.Schedule==NpcSchedule.Flee){desired=NpcSchedule.Search;a.WaitUntil=tick+90;}
        else if(a.Schedule==NpcSchedule.Search&&tick<a.WaitUntil)desired=NpcSchedule.Search;
        else if(order.Kind is NpcOrderKind.Hold or NpcOrderKind.Wander && a.Memory.LastOrDefault(m=>!m.Danger) is {} noise)
        {desired=NpcSchedule.Investigate;destination=noise.Point;}
        else switch(order.Kind)
        {
            case NpcOrderKind.Hold: destination=order.Points[0];break;
            case NpcOrderKind.MoveTo: destination=order.Points[0];break;
            case NpcOrderKind.Follow:
                var followed=physics.Players.FirstOrDefault(p=>p.Id==order.FollowPlayer);
                var offset=pos-followed.Position;offset.Y=0;
                if(offset.Length()<2.7f&&Math.Abs(pos.Y-followed.Position.Y)<.6f&&
                    physics.NpcSight(a.Record.Id,pos+Vector3.UnitY*1.6f,Movement.EyePosition(followed),followed.Id))
                {if(a.Schedule!=NpcSchedule.Follow)CancelNpcTask(a);a.Schedule=NpcSchedule.Follow;StopNpc(a,"Following / holding separation");return;}
                // An offset toward the NPC can land inside the intervening wall.
                // Route to the player's floor, then stop at separation once visible.
                destination=followed.Position;break;
            case NpcOrderKind.Patrol:destination=order.Points[a.PatrolIndex%order.Points.Length];break;
            case NpcOrderKind.Wander:
                if(a.Path.Length>0)destination=a.Destination;
                else {float angle=(tick/120+motion.Slot*17)*2.39996f;destination=a.Record.Home+new Vector3(MathF.Cos(angle),0,MathF.Sin(angle))*12;destination.Y=physics.World.SampleHeight(destination.X,destination.Z);}break;
        }
        if(a.Schedule!=desired){CancelNpcTask(a);a.Schedule=desired;}
        if(desired==NpcSchedule.Search){StopNpc(a,"Look around");a.Yaw+=.08f;return;}
        if(threat is not null&&!danger&&Vector3.DistanceSquared(destination,threat.Point)<16)
        {StopNpc(a,"Waiting away from remembered danger");return;}
        var distance=Vector3.Distance(pos,destination);
        if(distance<.4f)
        {
            a.TaskState=NpcTaskState.Complete;StopNpc(a,"Arrived");a.Path=[];
            if(desired==NpcSchedule.Patrol)a.PatrolIndex=(a.PatrolIndex+1)%order.Points.Length;
            if(desired==NpcSchedule.Investigate){a.Memory.RemoveAll(m=>!m.Danger);a.Schedule=NpcSchedule.Search;a.WaitUntil=tick+90;}
            if(desired==NpcSchedule.Wander)a.NextPlan=tick+120;
            if(desired==NpcSchedule.Hold&&tick%120<6)a.Yaw+=.3f;
            return;
        }
        if(a.RouteRevision!=l.Navigation.Revision)
        {
            if(a.Path.Length==0||a.CorridorRevision!=l.Navigation.CorridorRevision(a.Path))
            {a.Path=[];a.Retries=0;a.NextPlan=0;}
            a.RouteRevision=l.Navigation.Revision;
        }
        if(a.Retries>=3)
        {
            if(tick<a.NextPlan){StopNpc(a,"Blocked / waiting for obstacle change",NavigationOutcome.Blocked);return;}
            bool stillBlocked;
            if(a.WaitingDoor!=Guid.Empty&&CurrentChanges.Pieces.FirstOrDefault(p=>p.Id==a.WaitingDoor) is {} waiting)
                stillBlocked=!waiting.Open&&ValidateDoorSweep(waiting).Length>0;
            else
            {
                // Check the route's available detours, not just the direction
                // the NPC happened to face when it became blocked.
                var retryRoute=l.Navigation.Route(pos,destination);
                if(retryRoute.Outcome!=NavigationOutcome.Ready)
                {StopNpc(a,retryRoute.Reason,retryRoute.Outcome);a.NextPlan=tick+60;return;}
                var next=retryRoute.Points[Math.Min(1,retryRoute.Points.Length-1)];
                var toward=next-pos;toward.Y=0;
                if(toward.LengthSquared()>.0001f)toward=Vector3.Normalize(toward);
                stillBlocked=NpcSteering(l,a,pos,next,toward,toward).LengthSquared()<.0001f;
            }
            if(stillBlocked){StopNpc(a,"Blocked / waiting for obstacle change",NavigationOutcome.Blocked);a.NextPlan=tick+60;return;}
            a.Retries=0;
        }
        bool goalMoved=Vector3.DistanceSquared(a.Destination,destination)>(desired==NpcSchedule.Follow?.5625f:9);
        bool oldEndReached=a.Path.Length>0&&Vector3.DistanceSquared(pos,a.Path[^1])<.16f&&Vector3.DistanceSquared(a.Destination,destination)>.04f;
        if(a.Path.Length==0||goalMoved||oldEndReached)
        {
            if(tick<a.NextPlan){StopNpc(a,"Waiting to replan",a.Outcome);return;}
            var route=l.Navigation.Route(pos,destination);a.Outcome=route.Outcome;a.Reason=route.Reason;a.RouteRevision=route.Revision;
            if(route.Outcome!=NavigationOutcome.Ready){a.Intent=Vector3.Zero;a.Task=route.Reason;a.NextPlan=tick+30;return;}
            a.Path=route.Points;a.CorridorRevision=l.Navigation.CorridorRevision(a.Path);a.Destination=destination;a.Corner=Math.Min(1,a.Path.Length-1);a.ProgressTick=tick;a.LastPosition=pos;a.BestRemaining=float.PositiveInfinity;
        }
        while(a.Corner<a.Path.Length-1&&Vector3.DistanceSquared(pos,a.Path[a.Corner])<.25f&&l.Navigation.CanTraverse(pos,a.Path[a.Corner+1]))a.Corner++;
        var waypoint=a.Path[a.Corner];
        if(!l.Navigation.Current(waypoint)||!l.Navigation.Current(pos)){a.Path=[];StopNpc(a,"Waiting for committed navigation",NavigationOutcome.PendingNavigation);return;}
        if(!NpcDoorTraversal(a,pos,ref waypoint)){a.Intent=Vector3.Zero;return;}
        var direction=waypoint-pos;direction.Y=0;
        if(direction.LengthSquared()<.0001f)
        {
            StopNpc(a,"Waiting for corner clearance",NavigationOutcome.Blocked);
            if(tick-a.ProgressTick>90){a.Retries++;a.Path=[];a.NextPlan=tick+30*a.Retries;a.ProgressTick=tick;}
            return;
        }
        float waypointDistance=direction.Length();direction/=waypointDistance;
        var avoidance=Vector3.Zero;
        foreach(var other in physics.NpcMotions.Where(n=>n.Id!=motion.Id))avoidance+=Avoid(pos,other.Position,.85f);
        foreach(var other in physics.Players)avoidance+=Avoid(pos,other.Position,.85f);
        foreach(var body in physics.Bodies())avoidance+=Avoid(pos,body.Position,1.1f);
        var preferred=direction+avoidance*.65f;preferred=preferred.LengthSquared()>.001f?Vector3.Normalize(preferred):direction;
        direction=NpcSteering(l,a,pos,waypoint,preferred,direction);
        if(direction.LengthSquared()<.0001f)
        {
            StopNpc(a,"Waiting for obstacle clearance",NavigationOutcome.Blocked);a.Reason="No clear local detour";
            if(tick-a.ProgressTick>90){a.Retries++;a.Path=[];a.NextPlan=tick+30*a.Retries;a.ProgressTick=tick;}
            return;
        }
        float yaw=MathF.Atan2(direction.X,-direction.Z),turn=MathF.IEEERemainder(yaw-a.Yaw,MathF.Tau);
        a.Yaw=MathF.IEEERemainder(a.Yaw+Math.Clamp(turn,-.35f,.35f),MathF.Tau);
        var definition=CharacterDefinitions.Get(a.Record.Definition);
        float speed=Math.Min(danger?definition.RunSpeed:definition.WalkSpeed,waypointDistance/.1f);
        a.Intent=Math.Abs(turn)>.9f?Vector3.Zero:l.Navigation.ConstrainSteering(pos,direction*speed*.1f)/.1f;
        a.Task=Math.Abs(turn)>.9f?"Face destination":"Move";a.TaskState=NpcTaskState.Running;a.Outcome=NavigationOutcome.Ready;a.Reason="";
        float remaining=Vector3.Distance(pos,a.Path[a.Corner]);
        for(int i=a.Corner+1;i<a.Path.Length;i++)remaining+=Vector3.Distance(a.Path[i-1],a.Path[i]);
        if(remaining<a.BestRemaining-.1f){a.BestRemaining=remaining;a.LastPosition=pos;a.ProgressTick=tick;}
        if(tick-a.ProgressTick>90)
        {a.Retries++;a.Path=[];a.NextPlan=tick+30*a.Retries;StopNpc(a,"Movement blocked / replan "+a.Retries,NavigationOutcome.Blocked);}
    }
    static Vector3 Avoid(Vector3 pos,Vector3 other,float radius)
    {
        var d=pos-other;if(Math.Abs(d.Y)>1.5f)return Vector3.Zero;d.Y=0;float length=d.Length();
        return length>.001f&&length<radius?d/length*(1-length/radius):Vector3.Zero;
    }
    static void StopNpc(NpcActor a,string task,NavigationOutcome outcome=NavigationOutcome.Ready)
    {a.Intent=Vector3.Zero;a.Task=task;a.Outcome=outcome;if(outcome==NavigationOutcome.Blocked)a.TaskState=NpcTaskState.Failed;}
    bool NpcDoorTraversal(NpcActor a,Vector3 position,ref Vector3 destination)
    {
        if(!CharacterDefinitions.Get(a.Record.Definition).Capabilities.HasFlag(CharacterCapabilities.UseDoors))return true;
        NavigationDoor? link=null;float nearest=float.PositiveInfinity;
        foreach(var d in physics.NavigationDoors().Where(d=>!d.Open))
        {
            float start=Vector3.Dot(position-d.Centre,d.Normal),end=Vector3.Dot(destination-d.Centre,d.Normal);
            if(start*end>0||Math.Abs(start-end)<.001f)continue;
            var crossing=Vector3.Lerp(position,destination,start/(start-end));
            var sideways=crossing-d.Centre;sideways.Y=0;
            float distance=Vector3.Distance(position,crossing);
            if(Math.Abs(crossing.Y-d.Centre.Y)<1&&sideways.Length()<d.HalfWidth+Movement.Radius&&distance<nearest)
            {nearest=distance;link=d;}
        }
        if(link is null||nearest>4){a.WaitingDoor=Guid.Empty;return true;}
        var door=CurrentChanges.Pieces.Single(d=>d.Id==link.Id);
        var approach=Vector3.Dot(position-link.Centre,link.Normal)<0?link.A:link.B;
        var delta=position-approach;delta.Y=0;
        if(delta.LengthSquared()>.16f){destination=approach;a.WaitingDoor=door.Id;a.Task="Approach door outside swing";return true;}
        a.WaitingDoor=door.Id;a.Task="Use door / wait for clearance";a.Outcome=NavigationOutcome.Blocked;
        if (door.Open) return false; // Wait for the moving leaf, do not toggle it closed again.
        if(DoorSavePending||edit is not null||preparing is not null||savingEdit is not null)return false;
        string blocked=ValidateDoorSweep(door with { Open = true });if(blocked.Length>0)
        {
            a.Reason=blocked;
            if(physics.Tick>=a.NextPlan){a.Retries++;a.NextPlan=physics.Tick+30*Math.Min(3,a.Retries);a.TaskState=NpcTaskState.Failed;}
            return false;
        }
        var cmd=new BuildCommand(Guid.NewGuid(),CurrentChanges.Revision,"door",Target:door.Id);
        var result=BuildingRules.Edit(CurrentChanges,cmd,physics.World.SampleHeight,Dry);
        if(!result.Valid){a.Reason=result.Error;return false;}
        BeginDoorMotion(null,cmd,result);
        return false;
    }
    async Task FlushNpcs()
    {
        // A commit may finish after the simulation loop stops. Reconcile its
        // ownership before final snapshots can overwrite the atomic transfer.
        foreach(var (id, flight) in npcFlights.ToArray())
        {
            if(flight.Commit is null)continue;
            try { await flight.Commit; ReleaseNpcFlight(id,flight); }
            catch(Exception e) { Console.Error.WriteLine("NPC transit shutdown: "+e.Message); }
        }
        foreach(var l in npcLocations.Values)
        {
            if(l.Saving is {} saving)try{await saving;}catch(Exception e){Console.Error.WriteLine("NPC save: "+e.Message);}
            if(l.Ready)try{await NpcStorage.Save(l.File,l.Id,l.Revision,l.Actors.Values.Select(a=>a.Record).ToArray());}
                catch(Exception e){Console.Error.WriteLine("NPC shutdown save failed: "+e);}
            l.Navigation.Dispose();
        }
    }
}
