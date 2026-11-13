using System.Numerics;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.Persistence;
using StargateGame.WorldGen;

static class CharacterTransitChecks
{
    public static async Task Run()
    {
        var definition = GraphicsFixture.Create("gate-pair");
        var terrain = new TerrainStore(definition.SettingsHash);
        foreach (var c in GraphicsFixture.Chunks) terrain.Put(GraphicsFixture.BuildChunk(definition, c), false);
        using (var world = new PhysicsWorld(definition, true, terrain, GraphicsFixture.Chunks))
        {
            var gate = GateFrame.From(definition.Entities.Single(e => e.Id == "gate-b"));
            var desired = new PlayerState(1, "Traveler", gate.Centre - Vector3.UnitY*.9f + gate.Normal*.5f, gate.Normal*3, 0, 0, false, 0);
            var clear = world.ResolveCharacterExit(gate, desired, out bool launch);
            Verify.Check(!launch && clear.Position == desired.Position && clear.Velocity == desired.Velocity, "clear mapped exit preserves position and momentum");
            world.AddStaticBox(desired.Position + Vector3.UnitY*.9f, new(.4f,1,.3f));
            var alternate = world.ResolveCharacterExit(gate, desired, out launch);
            Verify.Check(!launch && alternate.Position != desired.Position && world.CanPlacePlayer(alternate.Position), "occupied mapped exit finds a nearby clear capsule position");
            world.AddStaticBox(gate.Centre + gate.Normal, new(4,5,4));
            var forced = world.ResolveCharacterExit(gate, desired with { TransitFrozen = true }, out launch);
            Verify.Check(launch && !forced.TransitFrozen && forced.ArrivalMomentumActive && Vector3.Dot(forced.Velocity,gate.Normal)>=12, "fully blocked exit launches from centre and clears frozen state");
            Verify.Near(gate.Local(forced.Position + Vector3.UnitY*.9f).X, 0, .001f, "fallback is centred horizontally");
            Verify.Near(gate.Local(forced.Position + Vector3.UnitY*.9f).Y, 0, .001f, "fallback is centred vertically");
            var crouched = world.ResolveCharacterExit(gate, desired with { Crouched = true }, out launch);
            Verify.Near(gate.Local(crouched.Position + Vector3.UnitY*Movement.CrouchHeight/2).Y,0,.001f,"fallback respects crouched height");
        }
        var frame = new GateFrame(new(0,3.06f,0), Quaternion.Identity, 2.3f);
        Verify.Check(GateTravel.PlayerCrossing(frame,new(0,.1f,.5f),new(0,.1f,.34f)),"low capsule entering aperture captures before rim contact stops movement");
        Verify.Check(GateTravel.PlayerCrossing(frame,new(0,.1f,.32f),new(0,.1f,.31f)),"inward movement already at aperture recovers capture");
        Verify.Check(!GateTravel.PlayerCrossing(frame,new(0,.1f,.31f),new(0,.1f,.32f)),"outward movement never recaptures arrival");
        Verify.Check(!GateTravel.PlayerCrossing(frame,new(3,.1f,.5f),new(3,.1f,.3f)),"movement beside rim does not teleport");
        string directory=Path.Combine(Path.GetTempPath(),"NpcTransfer-"+Guid.NewGuid().ToString("N"));
        try
        {
            string file=Path.Combine(directory,"npc.db"); Guid source=Guid.NewGuid(), destination=Guid.NewGuid(), id=Guid.NewGuid();
            var npc=new NpcRecord(id,"military",source,"test","neutral",Vector3.Zero,0,Vector3.Zero,new(NpcOrderKind.Hold,[Vector3.Zero]),0);
            await NpcStorage.Save(file,source,1,[npc]);
            await NpcStorage.Transfer(file,source,2,[],destination,1,[npc with{Location=destination}]);
            Verify.Check((await NpcStorage.Load(file,source)).Records.Length==0 && (await NpcStorage.Load(file,destination)).Records.Single().Id==id,"NPC ownership transfers atomically between saves");
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
        await FixtureGateChecks.Run(charactersOnly:true);
    }
}
