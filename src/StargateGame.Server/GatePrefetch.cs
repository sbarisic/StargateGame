using StargateGame.Galaxy;
using StargateGame.Persistence;
using StargateGame.Shared;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    sealed class DialPrefetch(ulong key, Task<GalaxyTarget> lookup, long started)
    {
        public readonly ulong Key = key;
        public readonly Task<GalaxyTarget> Lookup = lookup;
        public readonly long Started = started;
        public GateAssociation? Target;
        public bool Failed;
    }
    readonly Dictionary<Guid, CancellationTokenSource> loadCancellation = [];
    readonly Dictionary<Guid, long> loadAbandonedAt = [];
    public int PrefetchedLocationCount => contexts.Count(c => c.Prefetch?.Target is not null);
    public int PrefetchLoadsStarted
    {
        get;
        private set;
    }
    public int PrefetchLoadsReused
    {
        get;
        private set;
    }

    void UpdatePrefetch(LocationContext c)
    {
        if (galaxy is null || catalogue is null || repository is null)
            return;
        if (c.Gate is not { Phase: GatePhase.Dialing, Symbols.Length: >= 6 } gate)
        {
            c.Prefetch = null;
            return;
        }
        try
        {
            ulong key = Address.Resolve(gate.Symbols.Take(6).ToArray()).Key;
            if (c.Prefetch?.Key == key)
                return;
            c.Prefetch = new(key, repository.ResolveGalaxyAsync(galaxy, catalogue, key), serverTick);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException)
        {
            c.Prefetch = null;
        } // Invalid coordinates remain an orb-time error.
    }
    bool LoadReferenced(Guid id) => testPreparedLocations.Contains(id) || links.Values.Any(l => l.Target.LocationId == id) || relocations.Values.Any(r => r.Target == id) ||
        contexts.Any(c => c.Prefetch?.Target?.LocationId == id) || AllSessions.Any(s => s.SavedPlayer?.Location == id);
    void PollPrefetch()
    {
        foreach (var c in contexts)
        {
            if (c.Prefetch is not { } p || p.Failed)
                continue;
            if (serverTick - p.Started > 1800)
            {
                c.Prefetch = null;
                continue;
            }
            if (!p.Lookup.IsCompleted)
                continue;
            if (!p.Lookup.IsCompletedSuccessfully)
            {
                _ = p.Lookup.Exception;
                p.Failed = true;
                continue;
            }
            var target = p.Lookup.Result.Association;
            if (target.LocationId == c.Association?.LocationId || !target.Available || !target.Powered || !target.SupportsOrdinary)
            {
                p.Failed = true;
                continue;
            }
            p.Target = target;
            if (contexts.Any(x => x.Association?.LocationId == target.LocationId) || locationLoads.ContainsKey(target.LocationId))
                continue;
            // Keep one of the two generation slots available for explicit activation/relocation.
            if (locationLoads.Count >= 1)
                continue;
            if (contexts.Count(x => x != homeContext && x.Sessions.Count == 0 && !links.Values.Any(l => l.Source == x || l.Target.LocationId == x.Association?.LocationId)) >= 2)
                continue;
            BeginLoad(target, gateRegionOnly: true);
            PrefetchLoadsStarted++;
            Console.WriteLine($"Prefetch {target.LocationId}: sixth symbol accepted; generation started without a gate reservation.");
        }
        foreach (var (id, cancellation) in loadCancellation)
            if (LoadReferenced(id))
                loadAbandonedAt.Remove(id);
            else if (!loadAbandonedAt.TryGetValue(id, out var since))
                loadAbandonedAt[id] = serverTick;
            else if (serverTick - since > 120 && !cancellation.IsCancellationRequested)
                cancellation.Cancel();
    }
}

