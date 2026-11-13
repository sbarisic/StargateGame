using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using StargateGame.Shared;
using StargateGame.Building;
using StargateGame.Testing;

namespace StargateGame.Harness;

static partial class Recipes
{
    public static string[] Names => RecipeCatalog.All.Select(r => r.Id).ToArray();
    public static async Task Run(SessionManifest manifest, string name, CancellationToken token, string controller = "", Func<TestRequest, CancellationToken, Task<TestResult>>? dispatch = null)
    {
        int actionIndex = 0;
        async Task<TestResult> Send(TestAction action, object? data = null, int client = 1, int timeout = 180)
        {
            if (action != TestAction.Status) TestJson.Write(Path.Combine(manifest.Root, "recipe-progress.json"), new { recipe = name, step = actionIndex++, action, expected = data });
            var result = await (dispatch ?? ((request, cancellation) => LocalPipe.Send(manifest.Pipe, request, cancellation)))(new() { Session = manifest.Id, Generation = manifest.Generation, Controller = controller, Action = action, Data = TestJson.Element(data ?? new { }), Client = client, TimeoutSeconds = timeout }, token);
            result.EnsureSuccess(); return result;
        }
        async Task<JsonElement> State(int client = 1)
        {
            var r = await Send(TestAction.Status); return r.Data.GetProperty("clients")[client - 1];
        }
        async Task<JsonElement> Until(Func<JsonElement, bool> condition, int client = 1)
        {
            var watch = Stopwatch.StartNew(); JsonElement last = default;
            while (watch.Elapsed.TotalSeconds < 180)
            {
                last = await State(client); if (condition(last)) return last;
                await Task.Delay(100, token);
            }
            throw new TimeoutException("Recipe condition not reached: " + last);
        }
        if (File.Exists(name))
        {
            var recipe = TestJson.Read<RecipeDefinition>(name);
            if (recipe.InitialScenario is null && recipe.ResetBefore) throw new ArgumentException("Replay reset requires an initial scenario.");
            if (recipe.ResetBefore)
            {
                await Send(TestAction.SwitchScenario, recipe.InitialScenario);
                manifest = TestJson.Read<SessionManifest>(Path.Combine(manifest.Root, "session.json"));
            }
            int stepIndex = 0;
            foreach (var step in recipe.Steps)
            {
                TestJson.Write(Path.Combine(manifest.Root, "recipe-progress.json"), new { recipe = recipe.Name, step = stepIndex++, action = step.Action, expected = step.Data });
                var current = await State(step.Client);
                var resolved = ReplayExport.Transform(step.Data, TestJson.Element(new { }), true, current);
                if (step.Action == TestAction.Input && resolved.TryGetProperty("controlId", out var controlId))
                {
                    var clock = Stopwatch.StartNew();
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var view = await Send(TestAction.Observe, new { sections = new[] { "ui" } }, step.Client);
                        var ui = view.Data.GetProperty("observation").GetProperty("ui");
                        if (ui.GetProperty("controls").EnumerateArray().Any(c => c.GetProperty("id").GetString() == controlId.GetString()))
                        {
                            var fields = resolved.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                            fields["uiRevision"] = ui.GetProperty("revision"); resolved = TestJson.Element(fields); break;
                        }
                        if (clock.Elapsed.TotalSeconds >= step.TimeoutSeconds) throw new TimeoutException("Replay control not visible: " + controlId);
                        await Task.Delay(50, token);
                    }
                }
                await Send(step.Action, resolved, step.Client, step.TimeoutSeconds);
                if (step.Action is TestAction.ResetScenario or TestAction.SwitchScenario) manifest = TestJson.Read<SessionManifest>(Path.Combine(manifest.Root, "session.json"));
            }
            return;
        }
        if (!Names.Contains(name)) throw new ArgumentException("Unknown recipe: " + name);
        if(name=="npc-follow-buildings"){await FollowBuildings(manifest,token,controller,dispatch);return;}
        if(name=="rifle-audio"){await RifleAudio(manifest,token,controller,dispatch);return;}
        switch (name)
        {
            case "grass-performance":
            {
                if(manifest.Options.Scenario!="sandbox")throw new ArgumentException("Grass comparison requires Sandbox.");
                await Send(TestAction.Camera,new{bookmark="grass"});
                await Send(TestAction.Settings,new{quality="performance",hour=12,weather="off",visualTime=10,exposure="manual",exposureEv=0,renderWidth=1920,renderHeight=1080,
                    grassVolumeDensity=1,canopyDepthCulling=true,canopyLightLod=false,canopyHierarchicalSkip=false,grassDepthCulling=false,grassShaderReuse=false,grassScreenLod=false});
                await Send(TestAction.Wait,new{condition="captureReady"});
                await Until(s=>s.GetProperty("rendering").GetProperty("grass").GetProperty("ready").GetBoolean());
                var results=new List<object>();
                foreach(var mode in new[]{"baseline","depth","reuse","lod","combined"}) {
                    await Send(TestAction.Settings,new{grassDepthCulling=mode is "depth" or "combined",grassShaderReuse=mode is "reuse" or "combined",grassScreenLod=mode is "lod" or "combined"});
                    await Send(TestAction.Wait,new{condition="captureReady"});
                    await Send(TestAction.Sample,new{seconds=5});
                    var sample=await Send(TestAction.Sample,new{seconds=8});
                    if(sample.Data.GetProperty("frames").GetInt32()==0)throw new InvalidOperationException("No completed GPU timing samples.");
                    var grass=sample.Data.GetProperty("grassSettings");
                    if(grass.GetProperty("lods").EnumerateArray().Sum(v=>v.GetInt32())!=grass.GetProperty("patches").GetInt32())
                        throw new InvalidOperationException("Grass LOD counters do not match this frame's submitted patches.");
                    results.Add(new{mode,metrics=sample.Data});
                    TestJson.Write(Path.Combine(manifest.Root,"grass-performance.json"),new{results});
                    await Send(TestAction.Capture,new{name="grass-"+mode+".png",includeUi=false});
                }
                break;
            }
            case "canopy-performance":
            {
                if(manifest.Options.Scenario!="sandbox")throw new ArgumentException("Canopy comparison requires Sandbox.");
                await Send(TestAction.Camera,new{bookmark="forest"});
                await Send(TestAction.Settings,new{quality="performance",hour=12,weather="off",visualTime=10,renderWidth=1920,renderHeight=1080,canopyDepthCulling=false,canopyLightLod=false,canopyHierarchicalSkip=false});
                await Send(TestAction.Wait,new{condition="captureReady"});
                var results=new List<object>();
                foreach(var mode in new[]{"baseline","depth","skip","light","combined"}) {
                    await Send(TestAction.Settings,new{canopyDepthCulling=mode is "depth" or "combined",canopyLightLod=mode is "light" or "combined",canopyHierarchicalSkip=mode is "skip" or "combined"});
                    await Send(TestAction.Wait,new{condition="captureReady"});
                    await Send(TestAction.Sample,new{seconds=5}); // Bounded warmup; discard its timings.
                    var sample=await Send(TestAction.Sample,new{seconds=8});
                    results.Add(new{mode,metrics=sample.Data});
                    TestJson.Write(Path.Combine(manifest.Root,"canopy-performance.json"),new{results});
                    if(mode is "baseline" or "combined")await Send(TestAction.Capture,new{name="canopy-"+mode+".png"});
                }
                break;
            }
            case "npc-lifecycle":
            {
                if(manifest.Options.Scenario!="sandbox")throw new ArgumentException("NPC checks require Sandbox.");
                await Send(TestAction.Teleport,new{station="props"});
                var state=await State();var player=state.GetProperty("player").Deserialize<PlayerState>(TestJson.Options);
                var spawned=await Send(TestAction.Npc,new{command=new NpcCommand(Guid.NewGuid(),"spawn",Point:player.Position+new Vector3(-3,-.1f,3))});
                var npc=spawned.Data.Deserialize<NpcReply>(TestJson.Options)!.Target;
                await Until(s=>s.GetProperty("npcs").GetProperty("records").EnumerateArray().Any(n=>n.GetProperty("id").GetGuid()==npc));
                if(manifest.ClientPids.Length<2)await Send(TestAction.AddClient);
                await Until(s=>s.GetProperty("npcs").GetProperty("records").EnumerateArray().Any(n=>n.GetProperty("id").GetGuid()==npc),2);
                await Send(TestAction.Npc,new{command=new NpcCommand(Guid.NewGuid(),"order",npc,new(NpcOrderKind.Follow,[],player.Id))});
                await Until(s=>s.GetProperty("npcs").GetProperty("records").EnumerateArray().Any(n=>n.GetProperty("id").GetGuid()==npc&&n.GetProperty("order").GetProperty("kind").GetString()=="follow"));
                await Task.Delay(150,token); // Respect the authoritative command rate limit.
                await Send(TestAction.Npc,new{command=new NpcCommand(Guid.NewGuid(),"remove",npc)});
                await Until(s=>!s.GetProperty("npcs").GetProperty("records").EnumerateArray().Any(n=>n.GetProperty("id").GetGuid()==npc),2);
                break;
            }
            case "npc-navigation":
            {
                if(manifest.Options.Scenario!="sandbox")throw new ArgumentException("NPC navigation comparisons require Sandbox.");
                await Send(TestAction.Teleport,new{station="movement"});
                var state=await State();var navigationChanges=state.GetProperty("changes").Deserialize<WorldChanges>(TestJson.Options)!;
                var navigationDoor=navigationChanges.Pieces.First(BuildingPrefabs.IsDoor);
                var root=DoorGeometry.Root(navigationChanges.Structures.Single(s=>s.Id==navigationDoor.StructureId),navigationDoor);
                var navigationBounds=BuildingGeometry.Bounds(BuildingGeometry.Mesh(navigationChanges,navigationDoor with{Open=false}));
                var navigationCentre=(navigationBounds.Min+navigationBounds.Max)*.5f;navigationCentre.Y=navigationBounds.Min.Y+.05f;
                var normal=Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ,root));
                var houseBounds=BuildingGeometry.Bounds(navigationChanges.Pieces.Where(p=>p.StructureId==navigationDoor.StructureId)
                    .SelectMany(p=>BuildingGeometry.Mesh(navigationChanges,p)).ToArray());
                float houseMidZ=(houseBounds.Min.Z+houseBounds.Max.Z)*.5f;
                var probes=new[]{
                    (Name:"flat",From:new Vector3(-24,.05f,17),To:new Vector3(-17,.05f,17)),
                    (Name:"both-stair-flights",From:new Vector3(-23.3f,.05f,6.9f),To:new Vector3(-21.7f,3.35f,8.1f)),
                    (Name:"door-clearance",From:navigationCentre-normal*2,To:navigationCentre+normal*2),
                    (Name:"house-obstacle",From:new Vector3(houseBounds.Min.X-2,.05f,houseMidZ),To:new Vector3(houseBounds.Max.X+2,.05f,houseMidZ))};
                var report=new List<object>();
                foreach(float cell in new[]{.1f,.15f,.2f})
                {
                    await Send(TestAction.Npc,new{command=new NpcCommand(Guid.NewGuid(),"configure",CellSize:cell),debug=true});
                    foreach(var probe in probes)
                    {
                        NpcReply? answer=null;var watch=Stopwatch.StartNew();
                        while(watch.Elapsed.TotalSeconds<180)
                        {
                            await Task.Delay(200,token);
                            answer=(await Send(TestAction.Npc,new{command=new NpcCommand(Guid.NewGuid(),"probe",Origin:probe.From,Point:probe.To)})).Data.Deserialize<NpcReply>(TestJson.Options)!;
                            if(answer.Navigation!=NavigationOutcome.PendingNavigation)break;
                        }
                        await Task.Delay(150,token);
                        await Send(TestAction.Npc,new{command=new NpcCommand(Guid.NewGuid(),"debug"),debug=true});
                        var diagnostics=(await State()).GetProperty("npcNavigation");
                        report.Add(new{cell,probe.Name,probe.From,probe.To,outcome=answer?.Navigation,path=answer?.Path,reason=answer?.Reason,elapsedSeconds=watch.Elapsed.TotalSeconds,diagnostics});
                    }
                    await Send(TestAction.Camera,new{bookmark="movement"});
                    await Send(TestAction.Capture,new{name=$"npc-navigation-{cell*100:0}cm.png"});
                }
                TestJson.Write(Path.Combine(manifest.Root,"npc-navigation-comparison.json"),report);
                await Send(TestAction.Npc,new{command=new NpcCommand(Guid.NewGuid(),"configure",CellSize:NpcRules.NavigationCellSize),debug=false});
                break;
            }
            case "lantern-lighting":
            {
                if (manifest.Options.Scenario != "sandbox") throw new ArgumentException("lantern-lighting requires Sandbox.");
                await Send(TestAction.Teleport, new { station = "props" });
                var lanternState = await State();
                var player = lanternState.GetProperty("player").Deserialize<PlayerState>(TestJson.Options);
                var eye = player.Position + Vector3.UnitY * Movement.ViewHeight(player);
                foreach (var offset in new[] { new Vector3(-3, -1, -2), new Vector3(-3, -1, 1), new Vector3(1, -1, -3) })
                    await Send(TestAction.Build, new { command = new BuildCommand(Guid.NewGuid(), 0, "spawn-prop", Anchor: eye, Prefab: "wooden-lantern", HitPoint: player.Position + offset) });
                await Send(TestAction.Camera, new { eye = player.Position + new Vector3(2, 1.6f, -5), target = player.Position + new Vector3(-1, .45f, 0) });
                await Send(TestAction.Settings, new { quality = "high", weather = "off", gateLighting = false, visualTime = 20, orbit = false, traverse = false, exposure = "manual", exposureEv = 0 });
                foreach (int hour in new[] { 0, 12 })
                    foreach (string ao in new[] { "high", "off" })
                    {
                        await Send(TestAction.Settings, new { hour, ao, aoView = "lit" });
                        await Send(TestAction.Wait, new { condition = "captureReady" });
                        await Send(TestAction.Capture, new { name = $"lanterns-{hour}h-ao-{ao}.png", includeUi = false });
                    }
                foreach (string quality in new[] { "low", "performance", "high" })
                {
                    await Send(TestAction.Settings, new { quality, hour = 0, ao = "high" });
                    await Send(TestAction.Wait, new { condition = "captureReady" });
                    await Send(TestAction.Capture, new { name = $"lanterns-night-{quality}.png", includeUi = false });
                }
                break;
            }
            case "deferred-ao-compare":
                if (manifest.Options.Scenario != "sandbox") throw new ArgumentException("deferred-ao-compare requires Sandbox.");
                await Send(TestAction.Settings, new { quality = "high", weather = "off", hour = 12, visualTime = 20, orbit = false, traverse = false, exposure = "manual", exposureEv = 0 });
                foreach (string bookmark in new[] { "gate-pair", "interior", "dhd" })
                {
                    await Send(TestAction.Camera, new { bookmark });
                    foreach (string quality in new[] { "high", "off" })
                    {
                        await Send(TestAction.Settings, new { ao = quality, aoView = "lit" });
                        await Send(TestAction.Wait, new { condition = "captureReady" });
                        await Send(TestAction.Capture, new { name = $"deferred-{bookmark}-{quality}.png" });
                    }
                    await Send(TestAction.Settings, new { ao = "high", aoView = "ao" });
                    await Send(TestAction.Capture, new { name = $"deferred-{bookmark}-ao.png" });
                }
                await Send(TestAction.Settings, new { ao = "auto", aoView = "lit" });
                break;
            case "ui-live": await Send(TestAction.UiReview, timeout: 240); break;
            case "movement-visual": await Send(TestAction.MovementReview, timeout: 60); await Send(TestAction.Capture, new { name = "movement-end.png" }); break;
            case "sandbox-check":
                if (manifest.Options.Scenario != "sandbox") throw new ArgumentException("sandbox-check requires Sandbox.");
                if (manifest.ClientPids.Length < 2) await Send(TestAction.AddClient);
                var before = await State(); var originalPlayer = before.GetProperty("player").GetProperty("position");
                await Send(TestAction.Camera, new { bookmark = "interior" });
                var afterCamera = await State();
                if (Vector3.Distance(originalPlayer.Deserialize<Vector3>(TestJson.Options), afterCamera.GetProperty("player").GetProperty("position").Deserialize<Vector3>(TestJson.Options)) > .05) throw new Exception("Camera moved the authoritative player.");
                var changes = before.GetProperty("changes").Deserialize<WorldChanges>(TestJson.Options)!;
                var door = changes.Pieces.First(p => p.Kind == PieceKind.Door || BuildingPrefabs.IsDoor(p));
                var bounds = BuildingGeometry.Bounds(BuildingGeometry.Mesh(changes, door));
                Vector3 centre = (bounds.Min + bounds.Max) / 2; bool placed = false;
                foreach (var offset in new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX })
                {
                    try { await Send(TestAction.Teleport, new { position = new Vector3(centre.X, .03f, centre.Z) + offset * 1.5f }); placed = true; break; } catch (InvalidOperationException) { }
                }
                if (!placed) throw new Exception("No safe door interaction station.");
                await Send(TestAction.Camera, new { bookmark = "player" });
                await Send(TestAction.Aim, new { target = centre });
                await Send(TestAction.Build, new { command = new BuildCommand(Guid.NewGuid(), 0, "door", Target: door.Id) });
                bool DoorAt(JsonElement s, float angle) => s.GetProperty("doors").EnumerateArray().Any(d => d.GetProperty("id").GetGuid() == door.Id && Math.Abs(d.GetProperty("angle").GetSingle() - angle) < .001f);
                await Until(s => DoorAt(s, MathF.PI / 2));
                await Until(s => DoorAt(s, MathF.PI / 2), 2);
                await Send(TestAction.ResetStation, new { station = "interior" });
                await Until(s => DoorAt(s, 0));
                var crate = changes.Pieces.First(p => p.Prefab == "weapon-crate");
                await Send(TestAction.Teleport, new { station = "equipment" });
                await Send(TestAction.Weapon, new { action = "open", container = crate.Id });
                var equipped = (await State()).GetProperty("inventory").GetProperty("slots")[0].GetProperty("id").GetGuid();
                await Send(TestAction.Weapon, new { action = "store", item = equipped, container = crate.Id });
                await Send(TestAction.Teleport, new { position = new Vector3(23.6f, .03f, 10) }, 2);
                await Send(TestAction.Weapon, new { action = "open", container = crate.Id }, 2);
                await Send(TestAction.Weapon, new { action = "take", item = equipped, container = crate.Id }, 2);
                await Send(TestAction.ResetStation, new { station = "equipment" });
                await Send(TestAction.ResetStation, new { station = "props" });
                await Send(TestAction.ResetPlayer);
                await Send(TestAction.Camera, new { bookmark = "gate-pair" });
                await Send(TestAction.Capture, new { name = "sandbox-stations.png" });
                break;
            case "visual-check":
                if (manifest.Options.Preset != "default")
                {
                    var bookmark = await Send(TestAction.Camera, new { bookmark = ScenarioCatalog.Presets.Concat(ScenarioCatalog.AdvancedPresets).Single(p => p.Id == manifest.Options.Preset).Bookmark });
                }
                else await Send(TestAction.Camera, new { bookmark = manifest.Options.Scenario == "sandbox" ? "gate-pair" : "gate" });
                await Send(TestAction.Wait, new { condition = "captureReady" });
                await Send(TestAction.Capture, new { name = "overview.png" });
                await Send(TestAction.Sample, new { seconds = 3 }); break;
            case "environment":
                foreach (var hour in new[] { 12, 0 })
                {
                    await Send(TestAction.Settings, new { hour });
                    await Send(TestAction.Capture, new { name = "environment-" + hour + ".png" });
                }
                break;
            case "gate-effects":
                foreach (string phase in new[] { "idle", "opening", "open", "closing" })
                {
                    await Send(TestAction.GatePreview, new { phase, seconds = .3 });
                    await Send(TestAction.Capture, new { name = "preview-" + phase + ".png" });
                }
                await Send(TestAction.GatePreview, new { phase = "off" }); break;
            case "horizon-lifecycle":
            case "lifecycle":
                if (name == "horizon-lifecycle")
                {
                    await Send(TestAction.Camera, new { bookmark = "horizon-45" });
                    await Send(TestAction.Settings, new { horizonProps = true, horizonPlayers = true, waveCheck = true });
                    await Send(TestAction.GatePreview, new { phase = "open", seconds = 5 });
                }
                await Send(TestAction.Settings, new { quality = "low" });
                await Send(TestAction.Capture, new { name = "low.png" });
                await Send(TestAction.Settings, new { quality = "performance" });
                await Send(TestAction.Settings, new { quality = "high" });
                await Send(TestAction.Capture, new { name = "high.png" });
                await Send(TestAction.Window, new { state = "minimize" });
                await Send(TestAction.Window, new { state = "restore" });
                await Send(TestAction.Disconnect);
                foreach (string path in new[] { "resources.environmentBytes", "resources.volumeBytes", "resources.horizonBytes" }) await Send(TestAction.Assert, new { path, equals = 0 });
                await Send(TestAction.Connect);
                await Send(TestAction.ResetScenario);
                manifest = TestJson.Read<SessionManifest>(Path.Combine(manifest.Root, "session.json"));
                await Send(TestAction.Capture, new { name = "reset.png" }); break;
            case "audio-failure":
            case "dialing-failure":
                if (manifest.Options.Scenario != "travel") throw new ArgumentException("Failure dialing requires production Travel addressing.");
                if (name == "audio-failure") await Send(TestAction.AudioRecord, new { name = "failed-dial.wav" });
                var invalid = (await State()).GetProperty("returnAddress").Deserialize<int[]>(TestJson.Options)!;
                var (_, failureDhd) = await Endpoints(null); await StandAtDhd(failureDhd);
                foreach (int key in invalid.Concat([1])) await Send(TestAction.Dial, new { dhd = failureDhd.Id, key });
                try { await Send(TestAction.Dial, new { dhd = failureDhd.Id, key = 0 }); } catch (InvalidOperationException) { }
                await Until(s => s.GetProperty("gate").GetProperty("phase").GetString() == "idle");
                await Send(TestAction.Capture, new { name = "failed-dial-reset.png" });
                if (name == "audio-failure") {
                    await Send(TestAction.AudioRecord, new { stop = true });
                    if ((await State()).GetProperty("audioCues").GetProperty("gate.abort").GetInt32() != 1) throw new Exception("Expected one failed-dial abort cue.");
                }
                break;
            case "audio-endpoints":
                if (manifest.Options.Scenario != "travel") throw new ArgumentException("audio-endpoints requires Travel.");
                if (manifest.ClientPids.Length < 2) await Send(TestAction.AddClient);
                var route = (await Send(TestAction.Status)).Data.GetProperty("server").GetProperty("routes").EnumerateArray().First(r => r.GetProperty("biome").GetString() == "icy").GetProperty("address").Deserialize<int[]>(TestJson.Options)!;
                await Send(TestAction.PrepareDestination, new { address = route }, timeout: 240);
                await Send(TestAction.Relocate, new { address = route }, 2);
                await Send(TestAction.AudioRecord, new { name = "receiver.wav" }, 2);
                await Send(TestAction.AudioRecord, new { name = "source.wav" });
                await Cross(route, null, walkThrough: false);
                await Until(s => s.GetProperty("gate").GetProperty("phase").GetString() == "open", 2);
                await Close(null);
                await Send(TestAction.AudioRecord, new { stop = true }, 2);
                await Send(TestAction.AudioRecord, new { stop = true });
                var receiverCues = (await State(2)).GetProperty("audioCues");
                TestJson.Write(Path.Combine(manifest.Root, "audio-endpoints.json"), new { source = (await State()).GetProperty("audioCues"), receiver = receiverCues });
                if (receiverCues.GetProperty("gate.incoming").GetInt32() != 7 || receiverCues.TryGetProperty("dhd.glyph", out _)) throw new Exception("Receiver audio isolation/count failed: " + receiverCues);
                foreach (string cue in new[] { "gate.open", "gate.close" }) if (!receiverCues.TryGetProperty(cue, out _)) throw new Exception("Receiver cue missing: " + cue);
                break;
            case "audio":
                await Send(TestAction.AudioRecord, new { name = "gate.wav" });
                await GateRoundtrip();
                await Send(TestAction.AudioRecord, new { stop = true });
                var cues = (await State()).GetProperty("audioCues");
                foreach (string cue in new[] { "dhd.glyph", "gate.open", "gate.close" }) if (!cues.TryGetProperty(cue, out var count) || count.GetInt32() == 0) throw new Exception("Missing audio cue: " + cue);
                break;
            case "gate-roundtrip":
            case "travel-roundtrip": await GateRoundtrip(); break;
        }
        async Task GateRoundtrip()
        {
            JsonElement initial = await State();
            Guid home = initial.GetProperty("location").GetGuid();
            int[] homeAddress = initial.TryGetProperty("returnAddress", out var a) ? a.Deserialize<int[]>(TestJson.Options)! : [2, 3, 4, 5, 6, 7];
            bool sandbox = manifest.Options.Scenario == "sandbox";
            int[] outgoing = sandbox ? [2, 3, 4, 5, 6, 7] : (await Send(TestAction.Status)).Data.GetProperty("server").GetProperty("routes").EnumerateArray().First(r => r.GetProperty("biome").GetString() == "icy").GetProperty("address").Deserialize<int[]>(TestJson.Options)!;
            Guid? cargo = name == "travel-roundtrip" ? initial.GetProperty("props")[2].GetProperty("persistentId").GetGuid() : null;
            await Cross(outgoing, sandbox ? "a" : null, cargo);
            var arrived = await Until(s => s.GetProperty("playable").GetBoolean() && (sandbox ? s.GetProperty("player").GetProperty("position").GetProperty("x").GetSingle() > 0 : s.GetProperty("location").GetGuid() != home));
            if (cargo is { } id) await Until(s => s.GetProperty("props").EnumerateArray().Any(p => p.GetProperty("persistentId").GetGuid() == id));
            await Send(TestAction.Capture, new { name = "arrived.png" });
            await Close(sandbox ? "b" : null);
            await Cross(sandbox ? outgoing : homeAddress, sandbox ? "b" : null, cargo);
            await Until(s => s.GetProperty("playable").GetBoolean() && (sandbox ? s.GetProperty("player").GetProperty("position").GetProperty("x").GetSingle() < 0 : s.GetProperty("location").GetGuid() == home));
            if (cargo is { } returned) await Until(s => s.GetProperty("props").EnumerateArray().Any(p => p.GetProperty("persistentId").GetGuid() == returned));
            await Send(TestAction.Capture, new { name = "returned.png" });
        }
        async Task<(StaticEntity Gate, StaticEntity Dhd)> Endpoints(string? suffix)
        {
            var s = await State(); var entities = s.GetProperty("entities").Deserialize<StaticEntity[]>(TestJson.Options)!;
            return (entities.First(e => e.Model == "stargate" && (suffix is null || e.Id == "gate-" + suffix)), entities.First(e => e.Model == "dhd" && (suffix is null || e.Id == "dhd-" + suffix)));
        }
        async Task StandAtDhd(StaticEntity dhd)
        {
            var position = dhd.Position + Vector3.Transform(new Vector3(0, .03f, 1.1f), dhd.Rotation);
            await Send(TestAction.Teleport, new { position });
            await Until(s => Vector3.Distance(s.GetProperty("player").GetProperty("position").Deserialize<Vector3>(TestJson.Options), position) < .2f);
        }
        async Task Cross(int[] address, string? suffix, Guid? cargo = null, bool walkThrough = true)
        {
            var (gate, dhd) = await Endpoints(suffix);
            if (cargo is { } safeCargo)
            {
                await Send(TestAction.Carry, new { action = "drop" });
                await Send(TestAction.SetupProp, new { item = safeCargo, position = dhd.Position + Vector3.Transform(new Vector3(2, .65f, 1), dhd.Rotation) });
            }
            await StandAtDhd(dhd);
            foreach (int key in address.Concat([1, 0])) await Send(TestAction.Dial, new { dhd = dhd.Id, key });
            await Until(s => s.GetProperty("gate").GetProperty("phase").GetString() == "open");
            if (!walkThrough) return;
            var frame = GateFrame.From(gate);
            Vector3 position = frame.Centre + frame.Normal * (cargo.HasValue ? 4.5f : 2.5f); position.Y = dhd.Position.Y + .03f;
            await Send(TestAction.Teleport, new { position, yaw = Math.Atan2(-frame.Normal.X, frame.Normal.Z), pitch = 0 });
            await Until(s => Vector3.Distance(s.GetProperty("player").GetProperty("position").Deserialize<Vector3>(TestJson.Options), position) < .2f);
            await Send(TestAction.Camera, new { bookmark = "player" });
            if (cargo is { } item)
            {
                var propPosition = position - frame.Normal * 1.8f + Vector3.UnitY * .65f;
                await Send(TestAction.SetupProp, new { item, position = propPosition });
                await Send(TestAction.Move, new { seconds = .3, yaw = Math.Atan2(-frame.Normal.X, frame.Normal.Z), pitch = -.45 });
                await Send(TestAction.Carry, new { action = "grab", item });
            }
            await Send(TestAction.Move, new { forward = 1, seconds = 1.1, yaw = Math.Atan2(-frame.Normal.X, frame.Normal.Z), pitch = 0 });
        }
        async Task Close(string? suffix)
        {
            var (_, dhd) = await Endpoints(suffix); await StandAtDhd(dhd);
            await Send(TestAction.Dial, new { dhd = dhd.Id, key = 0 });
            await Until(s => s.GetProperty("gate").GetProperty("phase").GetString() == "idle");
        }
    }
}
