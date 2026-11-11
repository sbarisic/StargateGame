using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    public bool CanBeginPropEmergence(GateFrame gate, PropDefinition definition, BodyState pose)
    {
        lock (NativeGate)
        {
            if (!HasTerrain(pose.Position, definition.HalfExtent.Length() + .1f) || !GateTravel.Extents(gate, pose, definition).Fits)
                return false;
            var vertices = GateTravel.ClippedVertices(gate, pose, definition);
            if (vertices.Length < 4)
                return false;
            using var settings = Native.Own(new ConvexHullShapeSettings(vertices, 0));
            using var hull = Native.Own(settings.Create());
            return ClearShape(hull, pose.Position, pose.Rotation, BodyID.Invalid, .001f);
        }
    }
    public void ReservePropEmergence(Guid id, GateFrame gate, PropDefinition definition, BodyState pose)
    {
        lock (NativeGate)
        {
            using var settings = Native.Own(new ConvexHullShapeSettings(GateTravel.ClippedVertices(gate, pose, definition), 0));
            var shape = Own(settings.Create());
            exitReservations.Add(id, (AddBody(shape, pose.Position, pose.Rotation, false), shape));
        }
    }
}
