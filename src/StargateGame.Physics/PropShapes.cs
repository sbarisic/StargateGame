using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    static Shape CreatePropShape(PropDefinition prop)
    {
        if (prop.Kind == PropKind.Lantern)
        {
            using var compound = Native.Own(new StaticCompoundShapeSettings());
            foreach (var (centre, half) in LanternAsset.Sections)
            {
                using var box = Native.Own(new BoxShape(half, .003f));
                compound.AddShape(centre, Quaternion.Identity, box);
            }
            using var shape = Native.Own(compound.Create());
            // Jolt's compound centre of mass must not move the authored body origin.
            return new OffsetCenterOfMassShape(-shape.CenterOfMass, shape);
        }
        if (!prop.IsBarrel) return new BoxShape(prop.HalfExtent);
        using var hull = Native.Own(new ConvexHullShapeSettings(PropGeometry.For(prop).Vertices, .005f));
        return hull.Create();
    }
}
