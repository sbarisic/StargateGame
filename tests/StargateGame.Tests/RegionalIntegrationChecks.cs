using System.Diagnostics;
using System.Net;
using System.Numerics;
using StargateGame.Client;
using StargateGame.Physics;
using StargateGame.Server;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class RegionalIntegrationChecks
{
    public static async Task Run()
    {
        string root=Path.Combine(RegionalChecks.Output,"integration-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var watch=Stopwatch.StartNew();
        var village=new PlanetPlan(7319,"temperate",settlements:new("village","mega"),generation:new(LandscapeMode:LandscapeMode.Inland),cacheDirectory:RegionalChecks.Cache);
        Verify.Check(village.Settlements.Layout.Plots.Length>=8,"regional village fits a terrace");
        Verify.Check(village.Settlements.Layout.Plots.All(p=>village.UnsettledSample((int)p.Position.X,(int)p.Position.Z).Water==int.MinValue),"settlement dry footprints");
        Console.WriteLine($"SETTLEMENT plots={village.Settlements.Layout.Plots.Length} roads={village.Settlements.Layout.Roads.Length} elapsed={watch.Elapsed.TotalSeconds:F2}s");
        using(var generation=new GenerationService(748193,"temperate",cacheRoot:root,settlements:new("wilderness")))
        {
            await Task.Delay(40);generation.Dispose();
            try{await generation.Plan;}catch(OperationCanceledException){}
            Verify.Check(!generation.Plan.IsCompletedSuccessfully,"active generation cancellation");
        }
        await using var server=new GameServer(IPAddress.Loopback,0,1,seed:7319,devPlanets:true,saveDirectory:root,universe:"arrival",settlements:new("wilderness"),generationSettings:new(LandscapeMode:LandscapeMode.Inland));
        server.Start();using var identity=new PlayerIdentity();
        using var client=new ClientSession(await GameConnection.ConnectAsync("127.0.0.1",server.Port,"Regional checks",PhysicsWorld.PhysicsHash,PhysicsWorld.ContentHash,identity:identity));
        Verify.Check(client.Terrain is not null&&client.World.Definition.HalfSize==2048,"4 km network arrival and collision preparation");
        var start=client.Local.Position;
        foreach(int distance in new[]{0,128,256,384})
        {
            var focus=start+new Vector3(distance,0,0);client.StreamingFocus=focus;
            var c=ChunkCoordinate.At(focus.X,focus.Z);watch.Restart();
            while(watch.Elapsed.TotalSeconds<15 && !client.Terrain!.TryGetChunk(c,out _))
            {client.Poll();client.Step(0,0,0,0,false,false);if(client.Failure is not null)throw new Exception(client.Failure);await Task.Delay(10);}
            Verify.Check(client.Terrain!.TryGetChunk(c,out _),"streamed traversal "+distance+"m");
        }
        watch.Restart();
        while(watch.Elapsed.TotalSeconds<10&&client.FarScene.Count==0){client.Poll();client.Step(0,0,0,0,false,false);await Task.Delay(10);}
        Verify.Check(client.FarScene.Count>0&&client.FarDecodeFailures==0,"regional far streaming over network");
        Console.WriteLine("REGIONAL INTEGRATION PASSED (camera interest traversal; not a manual movement walkthrough)");
    }
}
