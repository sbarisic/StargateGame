using System.Numerics;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.Persistence;

static class NpcChecks
{
    public static async Task Run()
    {
        Verify.Check(!NpcRules.ValidOrder(new(NpcOrderKind.Patrol,[Vector3.Zero])),"Patrol requires two points");
        Verify.Check(!NpcRules.ValidOrder(new(NpcOrderKind.MoveTo,[new(float.NaN,0,0)])),"Reject nonfinite destination");
        Verify.Check(!NpcRules.ValidOrder(new(NpcOrderKind.Patrol,new Vector3[17])),"Patrol bounded at sixteen points");
        Verify.Check(NpcRules.ValidOrder(new(NpcOrderKind.Follow,[],7)),"Follow identifies its player separately");
        var definition=PlanetDefinition.Default with{Entities=[],Props=[]};
        using var world=new PhysicsWorld(definition,false);
        world.AddStaticBox(new(0,-.5f,0),new(30,.5f,30));
        world.AddPlayer(new(1,"Player",new(6,.03f,6),default,0,0,false,0));
        using var empty=world.Save();long epoch=world.Epoch;
        var id=Guid.NewGuid();world.AddNpc(id,new(0,.03f,0),0);
        Verify.Check(world.InteractionBlocked(new(0,1,-3),Vector3.UnitZ,3),"NPC capsule participates in visibility queries");
        Verify.Check(!world.InteractionBlocked(new(0,1,-3),Vector3.UnitZ,3,ignoreNpc:id),"Selection visibility can exclude only its target NPC");
        for(int i=0;i<60;i++){world.SetNpcIntent(id,new(2.7f,0,0),MathF.PI/2);world.Step();}
        Verify.Check(world.NpcMotions.Single().Position.X>1,"NPC intent moves a grounded capsule");
        Verify.Check(world.Players.Count()==1&&world.Epoch==epoch,"NPC does not become a connected player or reset physics epoch");
        using var occupied=world.Save();var saved=world.Snapshot();
        world.RemoveNpc(id);world.AddNpc(Guid.NewGuid(),new(8,.03f,0),0);
        world.Restore(occupied);
        Verify.Check(world.NpcMotions.SequenceEqual(saved.Npcs!),"Rollback restores historical NPC identity, slot and motion");
        world.Restore(empty);
        Verify.Check(world.NpcMotions.Count==0,"Rollback before spawn removes the historical collision proxy");
        world.Restore(occupied);
        for(int i=0;i<12;i++)world.Step();
        Verify.Check(world.NpcMotions[0].CommandTick<world.Tick-6,"Unrefreshed movement has a bounded extrapolation window");
        var invalid=world.Snapshot() with{Npcs=[world.NpcMotions[0],world.NpcMotions[0]]};
        bool rejected=false;try{PhysicsWorld.Validate(invalid,definition);}catch(InvalidDataException){rejected=true;}
        Verify.Check(rejected,"Duplicate collision slots rejected");
        world.Restore(empty);
        for(int i=0;i<NpcRules.Limit;i++)world.AddNpc(Guid.NewGuid(),new(i*.9f,.03f,4),0);
        rejected=false;try{world.AddNpc(Guid.NewGuid(),new(20,.03f,4),0);}catch(InvalidOperationException){rejected=true;}
        Verify.Check(rejected,"Native pool enforces the location limit");
        foreach(float size in new[]{.1f,.15f,.2f})
        {
            using var navigation=new NpcNavigation(size);
            Verify.Check(navigation.Route(Vector3.Zero,new(10,0,0)).Outcome==NavigationOutcome.PendingNavigation,"Unbuilt tiles are pending, not unreachable");
            Verify.Near(navigation.TileSize,32,.1f,"Navigation tile stays near target size");
        }
        using(var navigation=new NpcNavigation())
            Verify.Near(navigation.CellSize,.1f,.0001f,"Default navigation preserves authored house doorways");
        using(var obstacles=new PhysicsWorld(definition,false))
        {
            obstacles.AddStaticBox(new(0,-.5f,0),new(10,.5f,10));
            obstacles.AddStaticBox(new(1,.9f,0),new(.25f,.9f,.7f));
            obstacles.AddNpc(id,new(0,.03f,0),0);
            Verify.Check(obstacles.NpcObstacleFraction(id,new(0,.03f,0),new(2,.03f,0))<1,"Capsule lookahead detects a wall across the route");
            Verify.Near(obstacles.NpcObstacleFraction(id,new(0,.03f,0),new(0,.03f,2)),1,.001f,"A sideways detour remains clear without self or floor obstruction");
            obstacles.AddStaticBox(new(0,2.45f,1),new(.7f,.2f,.15f));
            Verify.Near(obstacles.NpcObstacleFraction(id,new(0,.03f,0),new(0,.03f,2)),1,.001f,"Doorway lintel above head height does not block the steering capsule");
            obstacles.AddStaticBox(new(0,1.7f,-1),new(.7f,.15f,.15f));
            Verify.Check(obstacles.NpcObstacleFraction(id,new(0,.03f,0),new(0,.03f,-2))<1,"Low overhead obstruction still blocks the steering capsule");
            obstacles.AddPlayer(new(1,"Follow target",new(0,.03f,3),default,0,0,true,0));
            Verify.Check(obstacles.NpcSight(id,new(0,1.63f,0),Movement.EyePosition(obstacles.Player(1)),1),"Follow visibility excludes the target player's own capsule");
        }
        using(var authoritative=new PhysicsWorld(definition,false))
        {
            authoritative.AddStaticBox(new(0,-.5f,0),new(30,.5f,30));
            authoritative.AddPlayer(new(1,"Prediction",new(6,.03f,6),default,0,0,false,0));
            var prepared=new PhysicsWorld(definition,false);
            prepared.AddStaticBox(new(0,-.5f,0),new(30,.5f,30));
            using var predicted=new PredictedWorld(definition,1,authoritative.Snapshot(),prepared:prepared);
            for(int i=1;i<=5;i++)predicted.Advance(new(i,0,0,0,0,false,false,TargetTick:i));
            var identity=Guid.NewGuid();authoritative.AddNpc(identity,new(0,.03f,0),0);
            authoritative.SetNpcIntent(identity,new(2.7f,0,0),MathF.PI/2);
            authoritative.Step(new Dictionary<int,PlayerInput>{{1,new(1,0,0,0,0,false,false,TargetTick:1)}});
            predicted.Accept(authoritative.Snapshot());
            Verify.Check(predicted.Resets==0&&predicted.Simulation.Tick==5&&predicted.Simulation.NpcMotions.Single().Id==identity,"Late spawn baseline replays pending player inputs without a topology reset");
            authoritative.RemoveNpc(identity);
            authoritative.Step(new Dictionary<int,PlayerInput>{{1,new(2,0,0,0,0,false,false,TargetTick:2)}});
            predicted.Accept(authoritative.Snapshot());
            Verify.Check(predicted.Resets==0&&predicted.Simulation.NpcMotions.Count==0,"Late removal baseline removes collision during historical replay");
        }
        string folder=Path.Combine(Path.GetTempPath(),"stargate-npc-check-"+Guid.NewGuid().ToString("N"));
        string file=Path.Combine(folder,"npcs.sqlite");var location=Guid.NewGuid();
        try
        {
            Verify.Check((await NpcStorage.Load(file,location)).Records.Length==0,"Existing locations start empty");
            var record=new NpcRecord(id,"military",location,"owner","neutral",Vector3.Zero,0,Vector3.Zero,new(NpcOrderKind.Patrol,[Vector3.Zero,new(4,0,0)]),10);
            await NpcStorage.Save(file,location,4,[record]);var loaded=await NpcStorage.Load(file,location);
            Verify.Check(loaded.Revision==4&&loaded.Records.Single().Id==id&&loaded.Records[0].Order.Points.SequenceEqual(record.Order.Points),"Persistence retains identity, ownership and patrol");
            Verify.Check((await NpcStorage.Load(file,Guid.NewGuid())).Records.Length==0,"NPC storage is location-owned");
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
}
