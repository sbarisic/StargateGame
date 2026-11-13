using System.Numerics;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.Persistence;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    sealed class NpcFlight
    {
        public required LocationContext Source;
        public LocationContext? Destination;
        public Link? Link;
        public required NpcActor Actor;
        public NpcMotion Motion;
        public GateFrame Entry, Exit;
        public long Started;
        public Task? Data, Commit;
        public ChunkCoordinate[]? Chunks;
        public PlayerState Arrival;
        public NpcRecord? Record;
    }
    readonly Dictionary<Guid, NpcFlight> npcFlights = [];
    readonly Dictionary<(Guid, Guid), NpcMotion> previousTravelNpcs = [];
    static PlayerState NpcTraveler(NpcMotion n) => new(1, "NPC", n.Position, n.Velocity, n.Yaw, 0, n.Grounded, 0);

    void ObserveNpcTravel(GateFrame? entry, GateFrame? exit, Link? link = null)
    {
        var location = physics.World.Definition.LocationId;
        var npcs = GetNpcs();
        foreach (var n in physics.NpcMotions)
        {
            var key = (location, n.Id);
            bool known = previousTravelNpcs.TryGetValue(key, out var before);
            previousTravelNpcs[key] = n;
            if (!known || entry is null || npcFlights.ContainsKey(n.Id) || !npcs.Actors.TryGetValue(n.Id, out var actor)
                || !GateTravel.PlayerCrossing(entry.Value, before.Position, n.Position)) continue;
            npcFlights.Add(n.Id, new() { Source = context, Destination = link is null ? context : link.Destination, Link = link,
                Actor = actor, Motion = n, Entry = entry.Value, Exit = exit ?? default, Started = serverTick });
            physics.RemoveNpc(n.Id); physics.Epoch++;
            npcs.Revision++;
            Console.WriteLine($"NPC TRANSIT captured id={n.Id}");
        }
        var active = physics.NpcMotions.Select(n => n.Id).ToHashSet();
        foreach (var key in previousTravelNpcs.Keys.Where(k => k.Item1 == location && !active.Contains(k.Item2)).ToArray()) previousTravelNpcs.Remove(key);
    }
    void TickNpcTravel()
    {
        foreach (var (id, flight) in npcFlights.ToArray())
        {
            var source = GetNpcs(flight.Source);
            if (flight.Commit is { IsCompleted: false }) continue;
            if (flight.Commit is { IsCompletedSuccessfully: true }) { ReleaseNpcFlight(id, flight); continue; }
            if (flight.Commit?.IsFaulted == true || serverTick - flight.Started > 1800 || flight.Link is { } expired && ConnectionExpired(expired))
            {
                Console.Error.WriteLine($"NPC TRANSIT returned id={id}: {flight.Commit?.Exception?.GetBaseException().Message ?? "destination not ready"}");
                flight.Destination = flight.Source; flight.Exit = flight.Entry;
                flight.Arrival = flight.Source.Physics.ResolveCharacterExit(flight.Entry, GateTravel.Exit(flight.Entry, flight.Entry, NpcTraveler(flight.Motion)), out _);
                flight.Record = null; ReleaseNpcFlight(id, flight); continue;
            }
            if (serverTick - flight.Started < GateTravel.MinimumTransitSeconds * 60) continue;
            flight.Destination ??= flight.Link?.Destination;
            if (flight.Destination is not { CollisionReady: true } target) continue;
            if (flight.Link is not null) flight.Exit = Frame(target);
            var destination = GetNpcs(target);
            if (!destination.Ready) continue;
            if (target != flight.Source && destination.Actors.Count >= NpcRules.Limit)
            { flight.Started = serverTick - 1801; continue; }
            var desired = GateTravel.Exit(flight.Entry, flight.Exit, NpcTraveler(flight.Motion));
            if (target != flight.Source)
            {
                var runtime = target.Runtime!;
                flight.Chunks ??= PhysicsWorld.IncludeBuildingOwners(runtime.Changes, ChunkCoordinate.At(desired.Position.X, desired.Position.Z).Around(1));
                flight.Data ??= Task.WhenAll(flight.Chunks.Select(c => runtime.Generation.RequestAsync(c, stop.Token)));
                if (flight.Data.IsFaulted || flight.Data.IsCanceled) { flight.Started = serverTick - 1801; continue; }
                if (!flight.Data.IsCompleted || target.Transition is not null || target.Preparing is not null || target.Edit is not null) continue;
                if (!target.Physics.PrepareTerrainBatch(flight.Chunks, 1)) continue;
                if (!flight.Chunks.All(target.Physics.TerrainCoordinates.Contains)) target.Physics.SetTerrain(target.Physics.TerrainCoordinates.Concat(flight.Chunks).Distinct());
            }
            if (source.Saving is { IsCompleted: false } || destination.Saving is { IsCompleted: false }
                || travels.Values.Any(t => t.Destination == target && t.Commit is not null)) continue;
            flight.Arrival = target.Physics.ResolveCharacterExit(flight.Exit, desired, out bool launched);
            var order = flight.Actor.Record.Order;
            if (target != flight.Source || order.Kind != NpcOrderKind.Follow)
                order = new(NpcOrderKind.Hold, [flight.Arrival.Position]);
            flight.Record = flight.Actor.Record with { Location = destination.Id, Position = flight.Arrival.Position,
                Yaw = flight.Arrival.Yaw, Home = flight.Arrival.Position, Order = order, EffectiveTick = target.Physics.Tick };
            Console.WriteLine($"NPC TRANSIT ready id={id} exit={(launched ? "centre-launch" : "clear")}");
            if (target == flight.Source) { ReleaseNpcFlight(id, flight); continue; }
            // Persist both sides atomically; block ordinary saves until release
            // has updated both in-memory ownership tables.
            var from = source.Actors.Values.Where(a => a.Record.Id != id).Select(a => a.Record).ToArray();
            var to = destination.Actors.Values.Select(a => a.Record).Append(flight.Record).ToArray();
            flight.Commit = NpcStorage.Transfer(source.File, source.Id, source.Revision + 1, from, destination.Id, destination.Revision + 1, to);
            source.Saving = destination.Saving = flight.Commit;
        }
    }
    void ReleaseNpcFlight(Guid id, NpcFlight flight)
    {
        var target = flight.Destination!;
        var source = GetNpcs(flight.Source); var destination = GetNpcs(target);
        npcFlights.Remove(id);
        source.Actors.Remove(id); source.Revision++; source.Dirty = true;
        var record = flight.Record ?? flight.Actor.Record with { Position = flight.Arrival.Position, Yaw = flight.Arrival.Yaw,
            Order = new(NpcOrderKind.Hold, [flight.Arrival.Position]), EffectiveTick = target.Physics.Tick };
        flight.Actor.Record = record; CancelNpcTask(flight.Actor); flight.Actor.Yaw = record.Yaw;
        destination.Actors[id] = flight.Actor; destination.Revision++; destination.Dirty = true;
        target.Physics.AddNpc(id, flight.Arrival.Position, flight.Arrival.Yaw);
        var snapshot = target.Physics.Snapshot();
        target.Physics.Apply(snapshot with { Npcs = snapshot.Npcs!.Select(n => n.Id == id ? n with { Velocity = flight.Arrival.Velocity } : n).ToArray() });
        target.Physics.Epoch++;
        previousTravelNpcs[(destination.Id, id)] = target.Physics.NpcMotions.Single(n => n.Id == id);
        Console.WriteLine($"NPC TRANSIT released id={id} location={destination.Id}");
    }
}
