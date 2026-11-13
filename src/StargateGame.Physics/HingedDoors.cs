using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Building;
using StargateGame.Shared;

namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    sealed class HingedDoor(BuildingPiece piece, StructureRecord structure, BodyID body, Shape shape, float angle)
    {
        public BuildingPiece Piece = piece;
        public StructureRecord Structure = structure;
        public readonly BodyID Body = body;
        public readonly Shape Shape = shape;
        public float Angle = angle;
        public float PreviousAngle = angle;
        public bool Blocked;
        public float Target => Piece.Open ? MathF.PI / 2 : 0;
    }
    readonly Dictionary<Guid, HingedDoor> hingedDoors = [];
    public bool HasMovingDoors => hingedDoors.Values.Any(d => d.Angle != d.Target);
    WorldChanges? posedDoors;
    public DoorMotion[] DoorStates => hingedDoors.OrderBy(p => p.Key).Select(p => new DoorMotion(p.Key, p.Value.Angle, p.Value.Blocked, p.Value.Piece.Open)).ToArray();
    public DoorMotion[] PresentedDoorStates(double tick) => hingedDoors.Select(p => new DoorMotion(p.Key,
        float.Lerp(p.Value.PreviousAngle, p.Value.Angle, (float)Math.Clamp(tick - (Tick - 1), 0, 1)), p.Value.Blocked, p.Value.Piece.Open)).ToArray();
    public void SetDoorTarget(Guid id, bool open)
    {
        lock (NativeGate)
        {
            var piece = Changes.Pieces.Single(p => p.Id == id);
            if (!BuildingPrefabs.IsDoor(piece)) throw new ArgumentException("Target is not a hinged door.");
            if (piece.Open == open) return;
            var replacement = piece with { Open = open };
            Changes = Changes with { Pieces = Changes.Pieces.Select(p => p.Id == id ? replacement : p).ToArray() };
            if (hingedDoors.TryGetValue(id, out var door)) door.Piece = replacement;
            posedDoors = null;
        }
    }
    public WorldChanges PosedChanges => posedDoors ??= Changes with
    {
        Pieces = Changes.Pieces.Select(p => hingedDoors.TryGetValue(p.Id, out var d) ? p with { DoorAngle = d.Angle } : p).ToArray()
    };
    void SyncHingedDoors()
    {
        posedDoors = null;
        var wanted = Changes.Pieces.Where(BuildingPrefabs.IsDoor)
            .Where(p => hasWorldGeometry && (terrainSource is null || buildingBodies.ContainsKey(Owner(Changes, p))))
            .ToDictionary(p => p.Id);
        foreach (var id in hingedDoors.Keys.ToArray())
        {
            var d = hingedDoors[id];
            if (wanted.TryGetValue(id, out var p) && (d.Piece with { Open = p.Open }) == p
                && Changes.Structures.Single(s => s.Id == p.StructureId) == d.Structure)
            { d.Piece = p; continue; }
            interactionBuildings.Remove(d.Body);
            system.BodyInterface.RemoveBody(d.Body); system.BodyInterface.DestroyBody(d.Body);
            ownedBodies.Remove(d.Body); shapes.Remove(d.Shape); d.Shape.Dispose(); hingedDoors.Remove(id); topology++;
        }
        foreach (var p in wanted.Values.Where(p => !hingedDoors.ContainsKey(p.Id)))
        {
            var structure = Changes.Structures.Single(s => s.Id == p.StructureId);
            var pivot = DoorGeometry.Pivot(p);
            using var compound = Native.Own(new StaticCompoundShapeSettings());
            foreach (var section in DoorGeometry.Sections(p))
            {
                using var box = Native.Own(new BoxShape((section.Max - section.Min) / 2, .001f));
                compound.AddShape((section.Min + section.Max) / 2 - pivot, Quaternion.Identity, box);
            }
            using var original = Native.Own(compound.Create());
            var shape = Own(new OffsetCenterOfMassShape(-original.CenterOfMass, original));
            float angle = p.Open ? MathF.PI / 2 : 0;
            var transform = Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(pivot) * DoorGeometry.Root(structure, p);
            Matrix4x4.Decompose(transform, out _, out var rotation, out var position);
            using var settings = Native.Own(new BodyCreationSettings(shape, position, rotation, MotionType.Kinematic, PropLayer));
            settings.Friction = .6f;
            var body = system.BodyInterface.CreateAndAddBody(settings, Activation.Activate);
            if (body.IsInvalid) throw new InvalidOperationException("Door body capacity exceeded.");
            ownedBodies.Add(body); interactionBuildings.Add(body); topology++;
            hingedDoors.Add(p.Id, new(p, structure, body, shape, angle));
        }
    }
    void ApplyDoorStates(DoorMotion[] states, bool native = true)
    {
        var supplied = states.ToDictionary(s => s.Id);
        foreach (var (id, door) in hingedDoors)
        {
            if (supplied.TryGetValue(id, out var target)) SetDoorTarget(id, target.Open);
            door.Angle = supplied.TryGetValue(id, out var s) ? s.Angle : door.Target;
            door.PreviousAngle = door.Angle;
            door.Blocked = s?.Blocked ?? false;
            if (native) PlaceDoor(door, false);
        }
        posedDoors = null;
    }
    void PlaceDoor(HingedDoor d, bool moving)
    {
        var transform = Matrix4x4.CreateRotationY(d.Angle) * Matrix4x4.CreateTranslation(DoorGeometry.Pivot(d.Piece)) * DoorGeometry.Root(d.Structure, d.Piece);
        Matrix4x4.Decompose(transform, out _, out var rotation, out var position);
        if (moving) system.BodyInterface.MoveKinematic(d.Body, position, rotation, Movement.Dt);
        else system.BodyInterface.SetPositionRotationAndVelocity(d.Body, position, rotation, Vector3.Zero, Vector3.Zero);
    }
    void StepDoors()
    {
        foreach (var d in hingedDoors.Values)
        {
            bool wasMoving = d.PreviousAngle != d.Angle;
            d.PreviousAngle = d.Angle;
            if (d.Angle == d.Target) { if (wasMoving) PlaceDoor(d, false); d.Blocked = false; continue; }
            float next = d.Angle + Math.Clamp(d.Target - d.Angle, -DoorGeometry.Speed * Movement.Dt, DoorGeometry.Speed * Movement.Dt);
            d.Blocked = DoorStepBlocked(d, next);
            if (!d.Blocked) d.Angle = next;
            PlaceDoor(d, true);
            posedDoors = null;
        }
    }
    bool DoorStepBlocked(HingedDoor d, float next)
    {
        var root = DoorGeometry.Root(d.Structure, d.Piece);
        int first = (int)MathF.Floor(Math.Min(d.Angle, next) * 180 / MathF.PI);
        int last = (int)MathF.Ceiling(Math.Max(d.Angle, next) * 180 / MathF.PI);
        var profile = DoorGeometry.For(d.Piece);
        var envelope = BuildingQuery.TransformBounds(profile.Envelope, root);
        var actors = players.Values.Select(p => (p.Position, Height: Movement.BodyHeight(p), Radius: Movement.Radius))
            .Concat(npcMotions.Values.Select(n => (n.Position, Height: Movement.Height, Radius: Movement.Radius)))
            .Where(p => Vector3.DistanceSquared(p.Position, Vector3.Clamp(p.Position, envelope.Min, envelope.Max)) < 4).ToArray();
        var propsNear = Bodies().Where(b => Vector3.DistanceSquared(b.Position, Vector3.Clamp(b.Position, envelope.Min, envelope.Max)) <= propDefinitions[b.Id].HalfExtent.LengthSquared())
            .SelectMany(b => DoorPropHulls(b, propDefinitions[b.Id])).ToArray();
        foreach (var sweep in profile.Sweeps.Where(s => s.Degree >= first && s.Degree < last))
        {
            Matrix4x4.Invert(sweep.Pose * root, out var inv);
            foreach (var p in actors)
                if (DoorGeometry.CapsuleBox(Vector3.Transform(p.Position, inv), p.Height, p.Radius, sweep.Bounds)) return true;
            foreach (var hull in propsNear)
                if (SolidGeometry.ConvexBox(hull.Vertices.Select(v => Vector3.Transform(v, inv)).ToArray(), hull.Faces, hull.Edges, sweep.Bounds)) return true;
        }
        return false;
    }
}
