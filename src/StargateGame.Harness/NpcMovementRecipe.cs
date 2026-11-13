using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using StargateGame.Building;
using StargateGame.Shared;
using StargateGame.Testing;

namespace StargateGame.Harness;

static partial class Recipes
{
    static async Task FollowBuildings(SessionManifest manifest,CancellationToken token,string controller = "",Func<TestRequest,CancellationToken,Task<TestResult>>? dispatch=null)
    {
        if(manifest.Options.Scenario!="sandbox")throw new ArgumentException("NPC movement checks require Sandbox.");
        var samples=new List<object>();
        string phase="setup";
        async Task<TestResult> Send(TestAction action,object? data=null)
        {
            var result=await (dispatch ?? ((request,cancellation)=>LocalPipe.Send(manifest.Pipe,request,cancellation)))(new(){Session=manifest.Id,Generation=manifest.Generation,Controller=controller,
                Action=action,Data=TestJson.Element(data??new{}),TimeoutSeconds=60},token);
            result.EnsureSuccess();return result;
        }
        async Task<JsonElement> State()=> (await Send(TestAction.Status)).Data.GetProperty("clients")[0];
        async Task WaitFor(Guid id,Vector3 goal,Vector3 centre,Vector3 normal,bool? inside)
        {
            var watch=Stopwatch.StartNew();
            while(watch.Elapsed.TotalSeconds<45)
            {
                var response=await Send(TestAction.Status);var client=response.Data.GetProperty("clients")[0];
                var states=client.GetProperty("npcCollision").Deserialize<NpcMotion[]>(TestJson.Options)!;
                var motion=states.FirstOrDefault(n=>n.Id==id);
                var status=client.GetProperty("npcs").Deserialize<NpcSnapshot>(TestJson.Options)!.Status.FirstOrDefault(n=>n.Id==id);
                samples.Add(new{phase,seconds=watch.Elapsed.TotalSeconds,motion,status,diagnostics=response.Data.GetProperty("server").GetProperty("npcMovement")});
                float side=Vector3.Dot(motion.Position-centre,normal);
                if(motion.Id==id&&Vector3.Distance(motion.Position,goal)<2.9f&&(!inside.HasValue||(inside.Value?side>.5f:side<-.5f)))return;
                await Task.Delay(250,token);
            }
            throw new TimeoutException("NPC failed movement phase: "+phase);
        }
        try
        {
            var initial=await State();
            if(initial.GetProperty("npcs").GetProperty("records").GetArrayLength()!=0)
                throw new InvalidOperationException("Movement recipe requires an isolated empty NPC store.");
            var changes=initial.GetProperty("changes").Deserialize<WorldChanges>(TestJson.Options)!;
            var doors=changes.Pieces.Where(BuildingPrefabs.IsDoor).Take(2).ToArray();
            if(doors.Length!=2)throw new InvalidOperationException("Sandbox needs two house entrances.");
            for(int i=0;i<doors.Length;i++)
            {
                var door=doors[i];var structure=changes.Structures.Single(s=>s.Id==door.StructureId);
                var root=DoorGeometry.Root(structure,door);
                var bounds=BuildingGeometry.Bounds(BuildingGeometry.Mesh(changes,door with{Open=false}));
                var centre=(bounds.Min+bounds.Max)*.5f;centre.Y=bounds.Min.Y+.06f;
                var normal=Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ,root));
                var right=Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX,root));
                var outside=centre-normal*4;var inside=centre+normal*3.5f;
                phase=$"house-{i+1}-spawn";
                await Send(TestAction.Teleport,new{position=outside-normal*1.5f});
                var spawn=(await Send(TestAction.Npc,new{command=new NpcCommand(Guid.NewGuid(),"spawn",Point:outside+right*.7f-Vector3.UnitY*.06f)})).Data.Deserialize<NpcReply>(TestJson.Options)!;
                await Task.Delay(150,token);
                await Send(TestAction.Npc,new{command=new NpcCommand(Guid.NewGuid(),"order",spawn.Target,new(NpcOrderKind.Follow,[],initial.GetProperty("player").GetProperty("id").GetInt32()))});
                // Explicit setup behind a closed door; the NPC must open and cross
                // it through normal movement. No NPC teleport or forced door state.
                await Send(TestAction.Teleport,new{position=inside});
                phase=$"house-{i+1}-enter-closed-door";
                await WaitFor(spawn.Target,inside,centre,normal,true);
                var current=await State();
                if(!current.GetProperty("changes").Deserialize<WorldChanges>(TestJson.Options)!.Pieces.Single(p=>p.Id==door.Id).Open)
                    throw new InvalidOperationException("NPC did not open its doorway.");
                phase=$"house-{i+1}-exit";
                // Set up the follower behind the player rather than walking the
                // player directly into the NPC's solid capsule in the narrow hall.
                await Send(TestAction.Teleport,new{position=outside});
                await WaitFor(spawn.Target,outside,centre,normal,false);
                phase=$"house-{i+1}-follow-walking-player";
                float yaw=MathF.Atan2(-normal.X,normal.Z);
                await Send(TestAction.Move,new{seconds=1,forward=1,yaw});
                var player=(await State()).GetProperty("player").Deserialize<PlayerState>(TestJson.Options);
                if(Vector3.Distance(player.Position,outside)<3)throw new InvalidOperationException("Player did not walk away from the follower.");
                await WaitFor(spawn.Target,player.Position,centre,normal,false);
                phase=$"house-{i+1}-outside-wall-detour";
                var behind=structure.Origin+normal*7+Vector3.UnitY*.06f;
                await Send(TestAction.Teleport,new{position=behind});
                await WaitFor(spawn.Target,behind,centre,normal,null);
                await Send(TestAction.Npc,new{command=new NpcCommand(Guid.NewGuid(),"remove",spawn.Target)});
            }
        }
        finally
        {
            TestJson.Write(Path.Combine(manifest.Root,"npc-movement.json"),new{phase,samples});
        }
    }
}
