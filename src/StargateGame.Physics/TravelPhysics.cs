using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    readonly Dictionary<int, BodyState> dormantProps = [];
    readonly HashSet<int> heldProps = [];
    readonly Dictionary<int, BodyState> completedProps = [];
    public bool NeedsPropSimulation
    {
        get
        {
            lock (NativeGate)
            {
                foreach (var (id, body) in props)
                {
                    if (heldProps.Contains(id) || completedProps.ContainsKey(id))
                        continue;
                    // A moving body paused for missing collision must resume once
                    // its region loads, even without a player to keep it awake.
                    if (dormantProps.TryGetValue(id, out var paused))
                    {
                        if (!paused.Sleeping)
                            return true;
                    }
                    else if (system.BodyInterface.IsActive(body))
                        return true;
                }
                return false;
            }
        }
    }
    void ReleaseCompletedProp(int id)
    {
        if (!completedProps.Remove(id, out var state) || !props.TryGetValue(id, out var body))
            return;
        var b = system.BodyInterface;
        if (!heldProps.Contains(id) && !dormantProps.ContainsKey(id))
            b.SetMotionType(body, MotionType.Dynamic, Activation.Activate);
        b.SetPositionRotationAndVelocity(body, state.Position, state.Rotation, state.LinearVelocity, state.AngularVelocity);
    }
    void ReleaseCompletedProps()
    {
        foreach (int id in completedProps.Keys.ToArray())
            ReleaseCompletedProp(id);
    }
    void PreparePropRegions()
    {
        RecoverBuriedProps();
        var b = system.BodyInterface;
        foreach (var (id, native) in props)
        {
            if (heldProps.Contains(id) || completedProps.ContainsKey(id))
                continue;
            bool ready = HasTerrain(b.GetPosition(native), 4);
            if (!ready && !dormantProps.ContainsKey(id))
            {
                dormantProps[id] = new(id, b.GetPosition(native), b.GetRotation(native), b.GetLinearVelocity(native), b.GetAngularVelocity(native), !b.IsActive(native));
                b.SetMotionType(native, MotionType.Kinematic, Activation.DontActivate);
                b.SetLinearVelocity(native, Vector3.Zero);
                b.SetAngularVelocity(native, Vector3.Zero);
            }
            else if (ready && dormantProps.Remove(id, out var saved))
            {
                b.SetMotionType(native, MotionType.Dynamic, Activation.Activate);
                b.SetLinearVelocity(native, saved.LinearVelocity);
                b.SetAngularVelocity(native, saved.AngularVelocity);
                if (saved.Sleeping)
                    b.DeactivateBody(native);
            }
        }
    }
    void RecoverBuriedProps()
    {
        // The world is an immutable heightfield, with no underground playable
        // space. Recover legacy fallen props before they reach snapshot limits.
        // Never move a partial/held traveler, or guess at unloaded terrain.
        if (!hasWorldGeometry)
            return;
        var b = system.BodyInterface;
        foreach (var (id, native) in props)
        {
            if (heldProps.Contains(id) || completedProps.ContainsKey(id) || propClips.ContainsKey(id) || dormantProps.ContainsKey(id))
                continue;
            var position = b.GetPosition(native);
            var definition = propDefinitions[id];
            float radius = definition.HalfExtent.Length();
            if (!HasTerrain(position, radius + 1))
                continue;
            float ground = World.SampleHeight(position.X, position.Z);
            if (!float.IsFinite(ground) || position.Y >= ground - Math.Max(4, 2 * radius))
                continue;
            foreach (float lift in new[] { 0f, 2f, 4f, 8f, 16f, 32f })
            {
                var recovered = position with
                {
                    Y = ground + radius + .2f + lift
                };
                if (!ClearShape(originalPropShapes[id], recovered, Quaternion.Identity, native))
                    continue;
                b.SetPositionRotationAndVelocity(native, recovered, Quaternion.Identity, Vector3.Zero, Vector3.Zero);
                b.InvalidateContactCache(native);
                b.ActivateBody(native);
                Epoch++;
                Console.WriteLine($"Recovered buried prop {id} ({definition.PersistentId}) from {position} to {recovered}.");
                break;
            }
        }
    }
    readonly Dictionary<int, Shape> originalPropShapes = [];
    readonly Dictionary<int, Shape> clippedPropShapes = [];
    readonly Dictionary<int, GateFrame> propClips = [];
    readonly Dictionary<int, Guid> propEmergences = [];
    public GateFrame? TravelGate
    {
        get;
        set;
    }
    public PropClip[] CurrentClips() => propClips.Select(p => new PropClip(p.Key, p.Value, propEmergences.ContainsKey(p.Key) ? PropClipMode.Emergence : PropClipMode.Entry, propEmergences.GetValueOrDefault(p.Key))).OrderBy(p => p.Body).ToArray();
    readonly HashSet<int> frozenCharacters = [];
    public void FreezePlayer(int id, bool frozen)
    {
        lock (NativeGate)
        {
            var state = players[id];
            if (frozen)
                DropCarried(id);
            players[id] = state with
            {
                TransitFrozen = frozen
            };
            if (frozen && frozenCharacters.Add(id))
            {
                characters[id].LinearVelocity = Vector3.Zero;
                system.BodyInterface.RemoveBody(characters[id].InnerBodyID);
            }
            else if (!frozen && frozenCharacters.Remove(id))
                system.BodyInterface.AddBody(characters[id].InnerBodyID, Activation.Activate);
        }
    }
    public void HoldProp(int id)
    {
        lock (NativeGate)
        {
            DropCarriedProp(id);
            entrySupports.Remove(id);
            heldProps.Add(id);
            system.BodyInterface.SetMotionType(props[id], MotionType.Kinematic, Activation.DontActivate);
            system.BodyInterface.SetLinearVelocity(props[id], Vector3.Zero);
            system.BodyInterface.SetAngularVelocity(props[id], Vector3.Zero);
        }
    }
    public void RemoveProp(int id)
    {
        lock (NativeGate)
        {
            DropCarriedProp(id);
            entrySupports.Remove(id);
            propEmergences.Remove(id);
            completedProps.Remove(id);
            foreach (var c in checkpoints.ToArray())
                c.Dispose();
            if (!props.Remove(id, out var body))
                return;
            heldProps.Remove(id);
            dormantProps.Remove(id);
            system.BodyInterface.RemoveBody(body);
            system.BodyInterface.DestroyBody(body);
            ownedBodies.Remove(body);
            propDefinitions.Remove(id);
            if (originalPropShapes.Remove(id, out var original))
            {
                shapes.Remove(original);
                original.Dispose();
            }
            propClips.Remove(id);
            if (clippedPropShapes.Remove(id, out var shape))
            {
                shapes.Remove(shape);
                shape.Dispose();
            }
            topology++;
            World.SetProps(propDefinitions.Values.OrderBy(p => p.Id).ToArray());
        }
    }
    public void InsertProp(PropDefinition definition, BodyState state)
    {
        lock (NativeGate)
        {
            if (props.Count >= 64)
                throw new InvalidOperationException("Destination prop capacity reached.");
            AddProp(definition with
            {
                Position = state.Position
            });
            system.BodyInterface.SetPositionRotationAndVelocity(props[definition.Id], state.Position, state.Rotation, state.LinearVelocity, state.AngularVelocity);
            World.SetProps(propDefinitions.Values.OrderBy(p => p.Id).ToArray());
        }
    }
    void SynchronizeProps(PropDefinition[] registry)
    {
        bool changed = !propDefinitions.Values.OrderBy(p => p.Id).SequenceEqual(registry.OrderBy(p => p.Id));
        if (!changed)
            return;
        foreach (var c in checkpoints.ToArray())
            c.Dispose();
        foreach (var id in props.Keys.Where(id => !registry.Any(p => p.Id == id && propDefinitions[id] == p)).ToArray())
            RemoveProp(id);
        foreach (var p in registry)
            if (!props.ContainsKey(p.Id))
                AddProp(p);
        World.SetProps(registry);
    }
    public bool CanPlacePlayer(Vector3 position, int ignorePlayer = 0, bool? crouched = null)
    {
        lock (NativeGate)
        {
            bool low = crouched ?? (players.TryGetValue(ignorePlayer, out var p) && p.Crouched);
            return HasTerrain(position) && ClearShape(low ? crouchShape : characterShape, position, Quaternion.Identity, characters.TryGetValue(ignorePlayer, out var c) ? c.InnerBodyID : BodyID.Invalid);
        }
    }
    public bool CanPlaceProp(PropDefinition definition, BodyState state, int ignoreBody = 0)
    {
        lock (NativeGate)
        {
            if (!HasTerrain(state.Position, definition.HalfExtent.Length() + .1f))
                return false;
            using var settings = Native.Own(new ConvexHullShapeSettings(PropGeometry.For(definition).Vertices, .005f));
            using var shape = Native.Own(settings.Create());
            return ClearShape(shape, state.Position, state.Rotation, props.GetValueOrDefault(ignoreBody, BodyID.Invalid));
        }
    }
    bool CanPlaceProp(Shape shape, PropDefinition definition, BodyState state, BodyID ignore)
        => HasTerrain(state.Position, definition.HalfExtent.Length() + .1f) && ClearShape(shape, state.Position, state.Rotation, ignore);
    public bool TryPlacePropExit(PropDefinition definition, BodyState desired, out BodyState result)
    {
        lock (NativeGate)
        {
            var vertices = PropGeometry.For(definition).Vertices;
            using var settings = Native.Own(new ConvexHullShapeSettings(vertices, .005f));
            using var shape = Native.Own(settings.Create());
            return TryPlacePropExit(shape, vertices, definition, desired, out result);
        }
    }
    bool TryPlacePropExit(Shape shape, Vector3[] vertices, PropDefinition definition, BodyState desired, out BodyState result)
    {
        result = desired;
        if (CanPlaceProp(shape, definition, desired, BodyID.Invalid))
            return true;
        if (!HasTerrain(desired.Position, definition.HalfExtent.Length() + .1f))
            return false;
        // Entry proxies can settle below their full hull, and the receiving bank
        // need not have the source bank's elevation relative to the gate. Correct
        // terrain penetration only; never search through walls or other props.
        float lift = 0;
        foreach (var vertex in vertices)
        {
            var p = desired.Position + Vector3.Transform(vertex, desired.Rotation);
            lift = Math.Max(lift, World.SampleHeight(p.X, p.Z) + .035f - p.Y);
        }
        if (lift <= 0 || lift > 2)
            return false;
        var candidate = desired with
        {
            Position = desired.Position + Vector3.UnitY * lift
        };
        if (!CanPlaceProp(shape, definition, candidate, BodyID.Invalid))
            return false;
        result = candidate;
        return true;
    }
    bool ClearShape(Shape shape, Vector3 position, Quaternion rotation, BodyID ignore, float tolerance = .015f)
    {
        List<CollideShapeResult> hits = [];
        var matrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position + Vector3.Transform(shape.CenterOfMass, rotation));
        // The pinned wrapper transposes Matrix4x4 in ToJolt; supply column-vector layout.
        system.NarrowPhaseQuery.CollideShape(shape, Vector3.One, Matrix4x4.Transpose(matrix), Vector3.Zero, CollisionCollectorType.AllHit, hits);
        return !hits.Any(h => h.BodyID2 != ignore && h.PenetrationDepth > tolerance);
    }
    public void SetPropClips(PropClip[] clips)
    {
        var wanted = clips.ToDictionary(c => c.Body, c => c.Gate);
        foreach (var id in propClips.Keys.Where(id => !wanted.ContainsKey(id)).ToArray())
            RestorePropShape(id);
        foreach (var clip in clips)
            if (props.ContainsKey(clip.Body))
            {
                if (clip.Mode == PropClipMode.Emergence)
                    propEmergences[clip.Body] = clip.Connection;
                else
                    propEmergences.Remove(clip.Body);
                UpdatePropShape(clip.Body, clip.Gate);
            }
    }
    void RestorePropShape(int id, bool retainEmergence = false)
    {
        if (!retainEmergence)
            propEmergences.Remove(id);
        ReleaseCompletedProp(id);
        entrySupports.Remove(id);
        propClips.Remove(id);
        if (!clippedPropShapes.Remove(id, out var old))
            return;
        var original = originalPropShapes[id];
        system.BodyInterface.SetShape(props[id], original, false, Activation.Activate);
        shapes.Remove(old);
        old.Dispose();
    }
    void UpdatePropShape(int id, GateFrame gate)
    {
        if (completedProps.ContainsKey(id))
            return;
        var body = props[id];
        var state = new BodyState(id, system.BodyInterface.GetPosition(body), system.BodyInterface.GetRotation(body), system.BodyInterface.GetLinearVelocity(body), system.BodyInterface.GetAngularVelocity(body), false);
        var e = GateTravel.Extents(gate, state, propDefinitions[id]);
        if (e.Min >= 0)
        {
            bool emerging = propEmergences.ContainsKey(id);
            RestorePropShape(id, emerging);
            if (emerging)
                propClips[id] = gate;
            return;
        }
        var vertices = GateTravel.ClippedVertices(gate, state, propDefinitions[id]);
        if (vertices.Length < 4 || e.Max <= (propEmergences.ContainsKey(id) ? 0 : GateTravel.PropCompletionDepth))
        {
            using var emptySettings = Native.Own(new EmptyShapeSettings(originalPropShapes[id].CenterOfMass));
            Shape empty = Own(emptySettings.Create());
            system.BodyInterface.SetShape(body, empty, false, Activation.Activate);
            if (clippedPropShapes.Remove(id, out var prior))
            {
                shapes.Remove(prior);
                prior.Dispose();
            }
            clippedPropShapes[id] = empty;
            propClips[id] = gate;
            // Wait for the authoritative transfer instead of integrating gravity
            // on an empty collider. Retain entry momentum for the receiver and
            // prediction snapshots, including while the transit journal is pending.
            completedProps[id] = state;
            DropCarriedProp(id);
            entrySupports.Remove(id);
            system.BodyInterface.SetMotionType(body, MotionType.Kinematic, Activation.DontActivate);
            system.BodyInterface.SetLinearVelocity(body, Vector3.Zero);
            system.BodyInterface.SetAngularVelocity(body, Vector3.Zero);
            return;
        }
        using var settings = Native.Own(new ConvexHullShapeSettings(vertices, 0));
        using var hull = Native.Own(settings.Create());
        // The proxy's geometric centroid must not move the original physical COM.
        Shape shape = Own(new OffsetCenterOfMassShape(originalPropShapes[id].CenterOfMass - hull.CenterOfMass, hull));
        system.BodyInterface.SetShape(body, shape, false, Activation.Activate);
        if (clippedPropShapes.Remove(id, out var old))
        {
            shapes.Remove(old);
            old.Dispose();
        }
        clippedPropShapes[id] = shape;
        propClips[id] = gate;
    }
    void UpdateTravelProxies()
    {
        foreach (var state in Bodies())
        {
            if (!propClips.TryGetValue(state.Id, out var gate))
                continue;
            var e = GateTravel.Extents(gate, state, propDefinitions[state.Id]);
            if (e.Min >= .02f && !propEmergences.ContainsKey(state.Id))
                RestorePropShape(state.Id);
            else
                UpdatePropShape(state.Id, gate);
        }
    }
    public void BeginPropEmergence(int id, GateFrame gate, Guid connection)
    {
        lock (NativeGate)
        {
            entrySupports.Remove(id);
            propEmergences[id] = connection;
            propClips[id] = gate;
            UpdatePropShape(id, gate);
        }
    }
    public void FinishPropEmergence(int id)
    {
        lock (NativeGate)
        {
            RestorePropShape(id);
        }
    }
    public void BeginPropEntry(int id, GateFrame gate)
    {
        lock (NativeGate)
        {
            if (!propClips.ContainsKey(id))
                BeginEntrySupport(id);
            propClips[id] = gate;
            UpdatePropShape(id, gate);
        }
    }
}
