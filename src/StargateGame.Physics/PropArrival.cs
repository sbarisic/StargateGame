using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    static readonly Vector2[] exitOffsets = (from x in Enumerable.Range(-6, 13)
                                             from z in Enumerable.Range(0, 7)
                                             let p = new Vector2(x * .5f, z * .5f)
                                             where p.LengthSquared() <= 9
                                             orderby p.LengthSquared(), z, x
                                             select p).ToArray();
    readonly Dictionary<Guid, (BodyID Body, Shape Shape)> exitReservations = [];
    // Search cursors retain only managed geometry. Native query shapes are shared
    // by every test in a slice and released before returning to simulation.
    public sealed class PropExitSearch
    {
        internal readonly PhysicsWorld Owner;
        internal readonly GateFrame Gate;
        internal readonly PropDefinition Definition;
        internal readonly BodyState Desired;
        internal readonly Vector3[] Vertices;
        internal int Next;
        public bool Complete
        {
            get;
            internal set;
        }
        public int CandidatesChecked => Next;
        internal PropExitSearch(PhysicsWorld owner, GateFrame gate, PropDefinition definition, BodyState desired)
        {
            Owner = owner;
            Gate = gate;
            Definition = definition;
            Desired = desired;
            Vertices = PropGeometry.For(definition).Vertices;
        }
    }
    public PropExitSearch BeginPropExitSearch(GateFrame gate, PropDefinition definition, BodyState desired) => new(this, gate, definition, desired);
    public bool TryFindPropExit(GateFrame gate, PropDefinition definition, BodyState desired, out BodyState result)
        => ContinuePropExitSearch(BeginPropExitSearch(gate, definition, desired), out result, int.MaxValue, double.PositiveInfinity);
    public bool ContinuePropExitSearch(PropExitSearch search, out BodyState result, int maxCandidates = 4, double budgetMilliseconds = 1)
    {
        if (search.Owner != this)
            throw new ArgumentException("Exit search belongs to another physics world.", nameof(search));
        result = search.Desired;
        if (search.Complete || maxCandidates <= 0 || budgetMilliseconds <= 0)
            return false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        lock (NativeGate)
        {
            var gate = search.Gate;
            var definition = search.Definition;
            var desired = search.Desired;
            using var settings = Native.Own(new ConvexHullShapeSettings(search.Vertices, .005f));
            using var shape = Native.Own(settings.Create());
            var hits = new List<ShapeCastResult>();
            int count = 0;
            while (search.Next < exitOffsets.Length * 2 && count < maxCandidates)
            {
                // A native query cannot be preempted; check the budget between
                // candidates, and never retry a whole blocked region each tick.
                if (count > 0 && clock.Elapsed.TotalMilliseconds >= budgetMilliseconds)
                    break;
                bool airborne = search.Next >= exitOffsets.Length;
                var offset = exitOffsets[search.Next++ % exitOffsets.Length];
                count++;
                var candidate = desired with
                {
                    Position = desired.Position + Vector3.Transform(new Vector3(offset.X, 0, offset.Y), gate.Rotation)
                };
                if (!HasTerrain(candidate.Position, definition.HalfExtent.Length() + .1f))
                    continue;
                if (!airborne)
                {
                    if (!TryPlacePropExit(shape, search.Vertices, definition, candidate, out var start))
                    {
                        bool clear = false;
                        for (float lift = .5f; lift <= 2; lift += .5f)
                        {
                            start = candidate with
                            {
                                Position = candidate.Position + Vector3.UnitY * lift
                            };
                            if (CanPlaceProp(shape, definition, start, BodyID.Invalid))
                            {
                                clear = true;
                                break;
                            }
                        }
                        if (!clear)
                            continue;
                    }
                    float drop = 2 + start.Position.Y - candidate.Position.Y;
                    hits.Clear();
                    var matrix = Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(start.Rotation) * Matrix4x4.CreateTranslation(start.Position + Vector3.Transform(shape.CenterOfMass, start.Rotation)));
                    system.NarrowPhaseQuery.CastShape(shape, matrix, -Vector3.UnitY * drop, Vector3.Zero, CollisionCollectorType.AllHit, hits);
                    foreach (var hit in hits.OrderBy(h => h.Fraction))
                    {
                        if (exitReservations.Values.Any(r => r.Body == hit.BodyID2) || hit.PenetrationAxis.Y >= -.01f || system.BodyInterface.GetMotionType(hit.BodyID2) != MotionType.Static)
                            continue;
                        var grounded = start with
                        {
                            Position = start.Position - Vector3.UnitY * Math.Max(0, drop * hit.Fraction - .025f)
                        };
                        if (Math.Abs(grounded.Position.Y - candidate.Position.Y) > 2 || Vector3.DistanceSquared(grounded.Position, desired.Position) > 9 || GateTravel.Extents(gate, grounded, definition).Min < .05f || !CanPlaceProp(shape, definition, grounded, BodyID.Invalid))
                            continue;
                        result = grounded;
                        search.Complete = true;
                        return true;
                    }
                }
                else
                {
                    // Only use an airborne exit after all grounded candidates.
                    if (!CanPlaceProp(shape, definition, candidate, BodyID.Invalid) || GateTravel.Extents(gate, candidate, definition).Min < .05f)
                        continue;
                    hits.Clear();
                    var matrix = Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(candidate.Rotation) * Matrix4x4.CreateTranslation(candidate.Position + Vector3.Transform(shape.CenterOfMass, candidate.Rotation)));
                    system.NarrowPhaseQuery.CastShape(shape, matrix, gate.Normal * .75f + Vector3.UnitY * .25f, Vector3.Zero, CollisionCollectorType.AllHit, hits);
                    if (hits.Any(h => h.Fraction < 1))
                        continue;
                    result = candidate with
                    {
                        LinearVelocity = ClampLength(candidate.LinearVelocity + gate.Normal * 4 + Vector3.UnitY * 1.5f, 12)
                    };
                    search.Complete = true;
                    return true;
                }
            }
            search.Complete = search.Next >= exitOffsets.Length * 2;
        }
        return false;
    }
    public void ReservePropExit(Guid id, PropDefinition definition, BodyState pose)
    {
        lock (NativeGate)
        {
            using var settings = Native.Own(new ConvexHullShapeSettings(PropGeometry.For(definition).Vertices, .005f));
            var shape = Own(settings.Create());
            exitReservations.Add(id, (AddBody(shape, pose.Position, pose.Rotation, false), shape));
        }
    }
    public void ReleasePropExit(Guid id)
    {
        lock (NativeGate)
        {
            if (!exitReservations.Remove(id, out var r))
                return;
            system.BodyInterface.RemoveBody(r.Body);
            system.BodyInterface.DestroyBody(r.Body);
            ownedBodies.Remove(r.Body);
            shapes.Remove(r.Shape);
            r.Shape.Dispose();
            topology++;
        }
    }
}
