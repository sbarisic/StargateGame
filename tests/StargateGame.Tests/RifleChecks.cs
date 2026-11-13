using System.Diagnostics;
using System.Net;
using System.Numerics;
using StargateGame.Shared;
using StargateGame.Client;
using StargateGame.Server;
using StargateGame.Persistence;

static class RifleChecks
{
    public static async Task Run()
    {
        RifleAudioChecks.Run();
        var fresh = new WeaponItem(Guid.NewGuid());
        var fired = Rifle.Fire(fresh);
        Verify.Check(fired.Magazine==28 && fired.Chambered && fired.Reserve==120,"shot consumes one round and feeds chamber");
        var tactical = Rifle.Reload(fired);
        Verify.Check(tactical.Magazine==30 && tactical.Chambered && tactical.Reserve==118,"tactical reload keeps chamber and fills 30-round magazine");
        Verify.Check(!Rifle.CanReload(tactical),"full 30+1 rifle cannot reload again");
        var empty = fresh;
        for(int i=0;i<30;i++)empty=Rifle.Fire(empty);
        Verify.Check(empty.Magazine==0 && !empty.Chambered && Rifle.Fire(empty)==empty,"exactly 30 shots then dry fire without negative ammo");
        var loaded=Rifle.Reload(empty);
        Verify.Check(loaded.Magazine==29 && loaded.Chambered && loaded.Reserve==90,"empty reload chambers one from the 30-round magazine");
        var last=Rifle.Reload(empty with {Reserve=1});
        Verify.Check(last.Magazine==0 && last.Chambered && last.Reserve==0 && !Rifle.CanReload(last),"last reserve round chambers without creating ammo");
        var inventory=new WeaponInventory(0,[tactical,null,null,null],tactical.Id);
        var chest=new WeaponContainer(Guid.NewGuid(),Guid.NewGuid(),0,new WeaponItem?[16]);
        var stored=WeaponCatalog.Apply(inventory,chest,new(Guid.NewGuid(),"store",Item:tactical.Id,Container:chest.Piece));
        Verify.Check(stored.Container!.Slots[0]==tactical,"storage preserves magazine chamber and reserve");
        string directory=Path.Combine(Path.GetTempPath(),"StargateRifle-"+Guid.NewGuid().ToString("N"));
        try
        {
            Guid player=Guid.NewGuid(); WeaponItem saved;
            await using(var db=new WorldRepository(directory))
            {
                var inv=await db.EnsureWeaponsAsync(player,true); saved=Rifle.Fire(inv.Slots[0]!);
                inv=inv with {Revision=inv.Revision+1,Slots=[saved,null,null,null]};
                await db.SaveAmmoAsync(player,inv,saved);
            }
            await using(var db=new WorldRepository(directory))
                Verify.Check((await db.EnsureWeaponsAsync(player,true)).Slots[0]==saved,"ammunition survives repository restart");
            await Network(Path.Combine(directory,"sandbox"));
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
    static async Task Network(string saves)
    {
        await using var server=new GameServer(IPAddress.Loopback,0,2,fixture:"gate-pair",devPlanets:true,saveDirectory:saves,allowTestIdentities:true);
        server.Start();
        var connection=await NetworkChecks.Connect(server.Port,"Rifle shooter");
        using var a=new ClientSession(connection);
        using var b=new ClientSession(await NetworkChecks.Connect(server.Port,"Rifle observer"));
        float pitch=-.25f;
        async Task Until(Func<bool> condition,string label,int timeout=6000)
        {
            var clock=Stopwatch.StartNew();
            while(!condition() && clock.ElapsedMilliseconds<timeout)
            {
                a.Poll();b.Poll();
                if(a.AwaitingArrivalFrame)a.DestinationFrameRendered(true);
                if(b.AwaitingArrivalFrame)b.DestinationFrameRendered(true);
                if(a.Failure is not null || b.Failure is not null)throw new Exception(a.Failure??b.Failure);
                a.Step(0,0,0,pitch,false,false);b.Step(0,0,0,0,false,false);
                await Task.Delay(5);
            }
            Verify.Check(condition(),label);
        }
        await Until(()=>a.Inventory is not null && b.Inventory is not null,"two clients ready with rifle inventories");
        await server.InspectPhysicsAsync(a.World.Definition.LocationId,w=>w.Apply(w.Snapshot() with {Players=w.Players.Select(p=>p.Id==a.Local.Id?p with {Position=new(-9,0,11),Velocity=Vector3.Zero,Yaw=0,Pitch=pitch}:p).ToArray()}));
        await Until(()=>Vector3.Distance(a.Local.Position,new(-9,0,11))<.1f && Math.Abs(a.Local.Pitch-pitch)<.01f && !a.TransitLocked,"shooter positioned facing prop");
        a.Character(true,false,"fire");
        await Until(()=>a.EquippedRifle?.Magazine==28 && b.RifleShots.Count>0,"server shot consumes ammo and replicates event");
        Verify.Check(b.RifleShots.Peek().Hit && b.RifleShots.Peek().Body==1,"server ray hits crate and stops at collision: " + b.RifleShots.Peek());
        await Until(()=>a.AudioSounds.Any(s=>s.Kind==WorldSoundKind.RifleFire) && b.AudioSounds.Any(s=>s.Kind==WorldSoundKind.RifleFire),"shot sound reaches shooter and observer");
        Verify.Check(a.AudioSounds.First(s=>s.Kind==WorldSoundKind.RifleFire).SourcePlayer==a.Local.Id &&
            b.AudioSounds.First(s=>s.Kind==WorldSoundKind.RifleFire).SourcePlayer==a.Local.Id,
            "shot sound identifies its shooter on both clients");
        Verify.Check(b.AudioSounds.Where(s=>s.Kind==WorldSoundKind.RifleImpact).All(s=>s.SourcePlayer==0),"bullet impacts remain world sounds");
        long sequence=a.LocalCharacter.Sequence;
        for(int i=1;i<=4;i++)connection.Peer.Send(new(){Kind="character-action",LocationId=a.World.Definition.LocationId,CharacterAction=new(sequence+i,true,false,i%2==0?"fire":"idle")});
        await Until(()=>a.LocalCharacter.Sequence>=sequence+4,"spam requests acknowledged");
        Verify.Check(a.EquippedRifle!.Magazine==28,"idle cannot bypass independent shot cooldown");
        await Until(()=>a.ServerTick>=a.LocalCharacter.StartTick+Rifle.FireTicks,"shot cooldown elapsed");
        a.Character(true,false,"reload");
        await Until(()=>a.LocalCharacter.Action=="reload" && b.Characters[a.Local.Id].Action=="reload","reload replicates to observer");
        await Until(()=>a.AudioSounds.Any(s=>s.Kind==WorldSoundKind.RifleReload && s.SourcePlayer==a.Local.Id) &&
            b.AudioSounds.Any(s=>s.Kind==WorldSoundKind.RifleReload && s.SourcePlayer==a.Local.Id),"reload sound retains shooter identity on both clients");
        var reload=a.LocalCharacter;
        sequence=reload.Sequence;
        foreach(var action in new[]{"fire","idle","reload","holster"})connection.Peer.Send(new(){Kind="character-action",LocationId=a.World.Definition.LocationId,CharacterAction=new(++sequence,true,false,action)});
        connection.Peer.Send(new(){Kind="weapon-command",LocationId=a.World.Definition.LocationId,WeaponCommand=new(Guid.NewGuid(),"holster",a.Inventory!.Revision)});
        await Until(()=>a.LocalCharacter.Sequence>=sequence && a.LastWeaponResult?.Success==false,"conflicting reload actions rejected");
        Verify.Check(a.LocalCharacter.Action=="reload" && a.LocalCharacter.StartTick==reload.StartTick && a.EquippedRifle?.Magazine==28,"reload cannot restart cancel fire or grant ammo early");
        await Until(()=>!a.Reloading && a.EquippedRifle?.Magazine==30,"reload completes on authoritative deadline");
        Verify.Check(a.EquippedRifle!.Chambered && a.EquippedRifle.Reserve==118 && a.Inventory!.Equipped is not null,"reload retains chamber and equipped rifle");
        a.Character(true,false,"fire");
        await Until(()=>a.EquippedRifle?.Magazine==29,"rifle fires again after reload");
        a.Character(true,false,"reload");
        await Until(()=>a.LocalCharacter.Action=="reload","reload can begin immediately after a shot");
        await Until(()=>!a.Reloading && a.EquippedRifle?.Magazine==30,"immediate reload finishes normally");
        int reloadSounds = a.AudioSounds.Count(s => s.Kind == WorldSoundKind.RifleReload);
        long fullSequence = a.LocalCharacter.Sequence;
        a.Character(true, false, "reload");
        await Until(() => a.LocalCharacter.Sequence > fullSequence, "full-magazine reload rejected and acknowledged");
        Verify.Check(!a.Reloading && a.LocalCharacter.Action != "reload" &&
            a.AudioSounds.Count(s => s.Kind == WorldSoundKind.RifleReload) == reloadSounds,
            "rejected reload has neither accepted animation nor sound notification");
        int priorShots = a.AudioSounds.Count(s => s.Kind == WorldSoundKind.RifleFire);
        for (int i = 0; i < 5; i++)
        {
            int beforeRounds = a.EquippedRifle!.Magazine;
            await Until(() => a.ServerTick >= a.LocalCharacter.StartTick + Rifle.FireTicks, "automatic-fire cadence elapsed");
            a.Character(true, false, "fire");
            await Until(() => a.EquippedRifle?.Magazine == beforeRounds - 1, "automatic shot accepted");
        }
        await Until(() => a.AudioSounds.Count(s => s.Kind == WorldSoundKind.RifleFire) == priorShots + 5 &&
            b.AudioSounds.Count(s => s.Kind == WorldSoundKind.RifleFire) == priorShots + 5,
            "one sound notification per accepted automatic shot on both clients");
    }
}
