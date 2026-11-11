using System.Diagnostics;
using System.Numerics;
using StargateGame.Shared;
using StargateGame.Persistence;
using StargateGame.Building;

namespace StargateGame.Server;
public sealed partial class GameServer
{
    sealed class HomeRegeneration(Session session, LocationContext source, PlanetDebugRequest request, long revision)
    {
        public readonly Session Session = session;
        public readonly LocationContext Source = source;
        public readonly PlanetDebugRequest Request = request;
        public readonly long Revision = revision;
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        public Task<SavedLocation>? Save;
        public bool Started;
        public string Phase = "Waiting for nearby collision";
        public double Reported;
    }
    HomeRegeneration? homeRegeneration;
    Task<PlanetDefinition>? environmentWrite;
    Session? environmentOwner;
    Guid environmentRequest;
    readonly Dictionary<Guid, PlanetDebugStatus> debugResults = [];
    bool HomeDebugBusy => homeRegeneration is not null || environmentWrite is not null;
    bool HandlePlanetDebug(Session s, Message m)
    {
        if (!m.Kind.StartsWith("planet-debug-", StringComparison.Ordinal)) return false;
        Guid id = m.PlanetDebug?.Id ?? Guid.Empty;
        void Reply(string phase, string detail) => s.Peer.Send(new() { Kind = "planet-debug-status", PlanetDebugState = new(id, phase, detail, 0, false, false, homeContext.Runtime?.Definition) });
        if (!devPlanets || !s.Loopback || s.PlayerId == 0) { Reply("Failed", "Developer planet control requires an authorized local client."); return true; }
        if (m.Kind == "planet-debug-read") { Reply("Ready", repository is null ? "Fixed fixtures cannot be regenerated. Select the generated-planet launch profile." : "Saved starting planet settings."); return true; }
        if (id == Guid.Empty) { Reply("Failed", "Missing request identity."); return true; }
        if (m.Kind == "planet-debug-cancel")
        {
            if (homeRegeneration is { } active && active.Session == s && active.Request.Id == id && active.Save is null)
                CancelHomeRegeneration("Cancelled", "Generation cancelled; the saved planet is unchanged.");
            return true;
        }
        if (debugResults.TryGetValue(id, out var prior)) { s.Peer.Send(new() { Kind = "planet-debug-status", PlanetDebugState = prior }); return true; }
        if (homeRegeneration?.Request.Id == id || environmentRequest == id && environmentWrite is not null) return true;
        try
        {
            if (m.Kind is not ("planet-debug-regenerate" or "planet-debug-environment")) throw new InvalidOperationException("Unknown planet debug action.");
            var request = m.PlanetDebug ?? throw new InvalidDataException("Missing planet settings.");
            if (request.Settings is null) throw new InvalidDataException("Missing planet settings.");
            request.Settings.Validate();
            if (repository is null || homeContext.Runtime is null || homeContext.Runtime.Definition.Fixture is not null)
                throw new InvalidOperationException("Fixed fixtures cannot be regenerated. Select the generated-planet launch profile.");
            if (AllSessions.Count(p => p.PlayerId != 0 && !p.Peer.Closed) != 1 || AllSessions.Any(p => p != s && p.JoinName is not null))
                throw new InvalidOperationException("Planet controls require you to be the only connected player.");
            if (HomeDebugBusy || links.Count > 0 || fixtureConnection is not null || travels.Count > 0 || relocations.Count > 0
                || contexts.Any(c => c.Edit is not null || c.Preparing is not null && !c.CollisionPreparation || c.Transition is { Data.Switch: true } || c.SavingEdit is not null || c.UnloadSave is not null)
                || AllSessions.Any(p => p.MoveLookup is not null || p.LookupReceipt is not null))
                throw new InvalidOperationException("Wait for gate travel, world edits and other preparation to finish.");
            if (m.Kind == "planet-debug-environment")
            {
                environmentOwner = s; environmentRequest = id;
                environmentWrite = repository.ApplyHomeEnvironmentAsync(homeContext.Runtime.Definition.LocationId, request.Settings.Environment);
                s.Peer.Send(new() { Kind = "planet-debug-status", PlanetDebugState = new(id, "Saving", "Applying environment without regenerating terrain.", 0, true, false, homeContext.Runtime.Definition) });
            }
            else
            {
                StopClientPreload(s, "home regeneration");
                homeRegeneration = new(s, context, request, homeContext.Runtime.Changes.Revision);
                SendHomeProgress();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
        { Reply("Failed", ex.Message); }
        return true;
    }
    async Task<Prepared> PrepareHome(PlanetDebugSettings settings, PlanetDefinition old, CancellationToken cancellation)
    {
        var generationWatch = Stopwatch.StartNew();
        var target = await PlanetRuntime.CreateAsync(settings.Seed, settings.Preset, cancellation, settlements: settings.Settlements, generationSettings: settings.Generation, progress: phase => { if(homeRegeneration is { } job) job.Phase = phase; });
        try
        {
            var environment = PlanetEnvironmentDefinition.Create(old.LocationId, settings.Seed, settings.Preset) with { DaySeconds = old.Environment?.DaySeconds ?? 3600 };
            target.Definition = target.Definition with { UniverseId = old.UniverseId, LocationId = old.LocationId, Name = old.Name,
                Environment = settings.Environment.Apply(environment, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                Props = target.Definition.Props.Select(p => p with { PersistentId = Guid.NewGuid() }).ToArray() };
            target.Changes = GeneratedBuildings.Create(target.Definition);
            target.InitialCollision = StargateGame.Physics.PhysicsWorld.IncludeBuildingOwners(target.Changes, target.InitialCollision);
            await Task.WhenAll(target.InitialCollision.Select(c => target.Generation.RequestAsync(c, cancellation)));
            Console.WriteLine($"PLANET GENERATION seed={settings.Seed} biome={settings.Preset} planMs={target.Generation.GenerationMilliseconds:F1} chunkCpuMs={target.Generation.ChunkGenerationMilliseconds:F1} elapsedMs={generationWatch.Elapsed.TotalMilliseconds:F1}");
            return new(target, target.InitialCollision, true, true);
        }
        catch { target.Dispose(); throw; }
    }
    void SendHomeProgress(string? phase = null)
    {
        if (homeRegeneration is not { } job) return;
        if (phase is not null) job.Phase = phase;
        job.Reported = job.Watch.Elapsed.TotalSeconds;
        job.Session.Peer.Send(new() { Kind = "planet-debug-status", PlanetDebugState = new(job.Request.Id, job.Phase,
            "Replacing saved starting planet", job.Reported, true, job.Save is null, homeContext.Runtime?.Definition) });
    }
    void CompleteHomeDebug(Session owner, Guid request, string phase, string detail, double elapsed)
    {
        var state = new PlanetDebugStatus(request, phase, detail, elapsed, false, false, homeContext.Runtime?.Definition);
        debugResults[request] = state;
        if (debugResults.Count > 128) debugResults.Remove(debugResults.Keys.First());
        owner.Peer.Send(new() { Kind = "planet-debug-status", PlanetDebugState = state });
        Console.WriteLine($"PLANET DEBUG {phase} {elapsed:F2}s: {detail}");
    }
    void TickPlanetDebug()
    {
        if (environmentWrite is { IsCompleted: true } task)
        {
            if (task.IsCompletedSuccessfully)
            {
                homeContext.Runtime!.Definition = task.Result;
                homeContext.Physics.World.SetEnvironment(task.Result.Environment!);
                foreach (var peer in homeContext.Sessions) peer.Peer.Send(new() { Kind = "planet-environment", LocationId = task.Result.LocationId, PlanetEnvironment = task.Result.Environment });
            }
            CompleteHomeDebug(environmentOwner!, environmentRequest, task.IsCompletedSuccessfully ? "Ready" : "Failed",
                task.IsCompletedSuccessfully ? "Environment saved." : task.Exception?.GetBaseException().Message ?? "Environment update failed.", 0);
            environmentWrite = null; environmentOwner = null;
        }
        if (homeRegeneration is not { } job) return;
        if (job.Session.Peer.Closed && job.Save is null) { CancelHomeRegeneration("Cancelled", "Explorer disconnected before commit."); return; }
        if (!job.Started && job.Source.Preparing is null && job.Source.Transition is null && homeContext.Preparing is null && homeContext.Transition is null)
        {
            job.Started = true;
            job.Source.DestinationStop?.Dispose(); job.Source.DestinationStop = new();
            job.Source.CollisionPreparation = false;
            job.Source.Preparing = PrepareHome(job.Request.Settings, homeContext.Runtime!.Definition, job.Source.DestinationStop.Token);
            SendHomeProgress("Generating");
        }
        if (job.Started && job.Source.Preparing is null && job.Source.Transition is null && job.Save is null)
        { CancelHomeRegeneration("Failed", "Planet preparation did not complete. The saved planet is unchanged."); return; }
        if (job.Watch.Elapsed.TotalSeconds - job.Reported >= 1) SendHomeProgress();
    }
    void CancelHomeRegeneration(string phase, string reason)
    {
        if (homeRegeneration is not { Save: null } job) return;
        if (job.Started) job.Source.DestinationStop?.Cancel();
        if (job.Started && job.Source.Transition is { Data.Regenerate: true } change)
        {
            job.Source.LastCancelledTransfer = change.Id;
            job.Session.Peer.Send(new() { Kind = "terrain-cancel", TransferId = change.Id, Reason = reason });
            job.Session.TerrainQueue.Clear(); job.Session.WorldQueue.Clear(); job.Session.Payload = null;
            job.Session.PreparedRuntime = null; job.Session.PreparedCoordinates.Clear();
            change.World?.Dispose(); change.Data.Runtime.Dispose(); job.Source.Transition = null;
        }
        CompleteHomeDebug(job.Session, job.Request.Id, phase, reason, job.Watch.Elapsed.TotalSeconds);
        homeRegeneration = null;
    }
    bool SaveHomeReady(Transition change)
    {
        if (!change.Data.Regenerate) return true;
        var job = homeRegeneration!;
        if (job.Save is null)
        {
            SendHomeProgress("Saving");
            var saved = job.Session.SavedPlayer;
            if (saved is not null) saved = saved with { State = physics.Player(job.Session.PlayerId), Appearance = characterStates.GetValueOrDefault(job.Session.PlayerId) };
            job.Save = repository!.ReplaceHomeAsync(change.Data.Runtime.Definition, job.Revision, saved);
        }
        if (!job.Save.IsCompleted) return false;
        if (!job.Save.IsCompletedSuccessfully)
        {
            string error = job.Save.Exception?.GetBaseException().Message ?? "Save cancelled.";
            job.Save = null; CancelHomeRegeneration("Failed", error); return false;
        }
        return true;
    }
    void FinishHomeRegeneration()
    {
        if (homeRegeneration is not { } job) return;
        if (context != homeContext)
        {
            // The transition was prepared on the explorer's source context. Move
            // its newly installed runtime to the stable home context.
            throw new InvalidOperationException("Home runtime must be installed before publishing.");
        }
        foreach (var key in farTiles.Keys.Where(k => k.Item1 == homeContext.Runtime!.Definition.LocationId).ToArray()) farTiles.Remove(key);
        ClearTravelHistory(homeContext.Runtime!.Definition.LocationId);
        if (homeContext.Association is { } association) SetAssociation(homeContext, association);
        homeRegeneration = null;
        foreach (var peer in sessions) { SendGateBaseline(peer); SavePlayer(peer); }
        CompleteHomeDebug(job.Session, job.Request.Id, "Ready", "Starting planet regenerated; visual detail continues streaming.", job.Watch.Elapsed.TotalSeconds);
    }
}
