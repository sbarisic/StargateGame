using System.Numerics;
using System.Diagnostics;
using StargateGame.Shared;
using StargateGame.Building;
using StargateGame.Physics;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    sealed class HazardWork
    {
        public KawooshVolume? Volume;
        public Guid Connection;
        public int TerrainCount = -1;
        public long Revision = -1;
        public PhysicsWorld.HazardCandidate[] Candidates = [];
        public readonly HashSet<Guid> Props = [], Pieces = [];
        public readonly HashSet<SceneryRemoval> Scenery = [];
        public readonly Dictionary<Guid, BodyState> Previous = [];
        public readonly HashSet<string> Hit = [];
        public bool Pending => Props.Count + Pieces.Count + Scenery.Count > 0;
    }
    readonly Dictionary<LocationContext, HazardWork> hazards = [];
    readonly List<double> hazardTimes = [];
    void ObserveKawoosh()
    {
        if (runtime is null || repository is null)
            return;
        if (!hazards.TryGetValue(context, out var work))
            hazards[context] = work = new();
        var state = context.Gate;
        if (state is { Phase: GatePhase.Opening })
        {
            var watch = Stopwatch.StartNew();
            if (work.Connection != state.ConnectionId)
            {
                work.Connection = state.ConnectionId;
                work.Hit.Clear();
                work.Previous.Clear();
                work.Revision = -1;
            }
            var gate = Frame(context);
            double age = (serverTick - state.PhaseTick) / 60.0;
            var effect = GateEffectTimeline.Evaluate(GatePhase.Opening, age);
            if (effect.Extension > .001f)
            {
                if (work.Revision != CurrentChanges.Revision || work.TerrainCount != physics.TerrainCoordinates.Count())
                {
                    work.Candidates = physics.KawooshCandidates(gate);
                    work.Revision = CurrentChanges.Revision;
                    work.TerrainCount = physics.TerrainCoordinates.Count();
                }
                // Adaptive temporal samples accompany swept prop poses. Neither
                // the flat sheet nor cosmetic particles participate in this query.
                var previousEffect = GateEffectTimeline.Evaluate(GatePhase.Opening, age - 1.0 / 60);
                int samples = Math.Clamp((int)Math.Ceiling(Math.Abs(effect.Extension - previousEffect.Extension) * KawooshShape.Length / .15), 1, 4);
                for (int sampleIndex = 1; sampleIndex <= samples; sampleIndex++)
                {
                    double sample = age - (1 - sampleIndex / (double)samples) / 60;
                    var f = GateEffectTimeline.Evaluate(GatePhase.Opening, sample);
                    if (f.Extension <= .001f)
                        continue;
                    if (work.Volume is null)
                        work.Volume = new KawooshVolume(gate, f);
                    else
                        work.Volume.Update(gate, f);
                    var volume = work.Volume;
                    foreach (var body in physics.Bodies())
                    {
                        var prop = physics.World.Definition.Props.Single(p => p.Id == body.Id);
                        if (work.Props.Contains(prop.PersistentId) || propDestructions.Any(d => d.Context == context && d.Prop.PersistentId == prop.PersistentId) || travels.Values.Any(t => t.Source == context && t.Prop?.PersistentId == prop.PersistentId))
                            continue;
                        if (volume.IntersectsProp(prop, work.Previous.GetValueOrDefault(prop.PersistentId, body), body))
                        {
                            physics.HoldProp(body.Id);
                            work.Props.Add(prop.PersistentId);
                            Console.WriteLine($"KAWOOSH hit item={prop.PersistentId} connection={state.ConnectionId} gate={state.GateId} location={state.LocationId} tick={serverTick} epoch={physics.Epoch}");
                        }
                    }
                    foreach (var candidate in work.Candidates)
                    {
                        string key = candidate.Scenery is { } scenery ? $"{scenery.Category}:{scenery.Id}" : $"piece:{candidate.Piece}";
                        if (work.Hit.Contains(key) || !volume.Intersects(candidate.Mesh))
                            continue;
                        work.Hit.Add(key);
                        if (candidate.Scenery is { } removed)
                            work.Scenery.Add(removed);
                        else
                            work.Pieces.Add(candidate.Piece);
                        Console.WriteLine($"KAWOOSH hit {key} connection={state.ConnectionId} gate={state.GateId} location={state.LocationId} tick={serverTick}");
                    }
                }
            }
            work.Previous.Clear();
            foreach (var body in physics.Bodies())
            {
                var prop = physics.World.Definition.Props.Single(p => p.Id == body.Id);
                work.Previous[prop.PersistentId] = body;
            }
            // Keep diagnostics bounded even when an opening hits no editable objects.
            if (hazardTimes.Count == 2048)
                hazardTimes.RemoveAt(0);
            hazardTimes.Add(watch.Elapsed.TotalMilliseconds);
        }
        if (!work.Pending || edit is not null || transition is not null || preparing is not null || sessions.Any(s => s.JoinName is not null))
            return;
        var next = BuildingRules.Destroy(CurrentChanges, work.Pieces, work.Scenery);
        edit = new(null, new(Guid.NewGuid(), CurrentChanges.Revision, "hazard"), new(next, null, "", []), default, work.Props.ToArray());
        preparing = PrepareCollision(PhysicsWorld.IncludeBuildingOwners(next, physics.TerrainCoordinates));
    }
    void FinishKawooshEdit(PendingEdit completed)
    {
        var work = hazards[context];
        foreach (var id in completed.DestroyedProps ?? [])
            work.Props.Remove(id);
        work.Pieces.RemoveWhere(id => !CurrentChanges.Pieces.Any(p => p.Id == id));
        work.Scenery.RemoveWhere(r => CurrentChanges.IsRemoved(r.Category, r.Id));
        Console.WriteLine($"KAWOOSH committed location={CurrentChanges.LocationId} revision={CurrentChanges.Revision} epoch={physics.Epoch} props={completed.DestroyedProps?.Length ?? 0}");
        if (hazardTimes.Count > 0)
        {
            var samples = hazardTimes.Order().ToArray();
            Console.WriteLine($"KAWOOSH CPU samples={samples.Length} p95_ms={samples[(int)((samples.Length - 1) * .95)]:F4} max_ms={samples[^1]:F4}");
            hazardTimes.Clear();
        }
    }
}
