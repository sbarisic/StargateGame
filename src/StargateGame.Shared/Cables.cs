using System.Numerics;
namespace StargateGame.Shared;

public sealed record CableAnchor(string Kind, Guid Owner, string Entity, Vector3 Point, Vector3 Normal);
public sealed record CableRecord(Guid Id, CableAnchor A, CableAnchor B, float Length, int Color, float Diameter, int StyleVersion = 0);
public sealed record CableCommand(Guid Request, string Action, CableAnchor? Anchor = null, Guid Target = default, float Slack = .15f, int Color = 0, float Diameter = .05f);
public sealed record CableSnapshot(long Revision, int Part, int Count, CableRecord[] Cables);
public sealed record CableReply(Guid Request, bool Success, string Reason);
public static class CableRules
{
    public const int Limit = 256;
    public static readonly float[] Diameters = [.02f, .05f, .08f, .11f, .14f];
    public static readonly Vector3[] Colors = [new(.025f), new(.035f,.18f,.65f), new(.65f,.045f,.025f), new(.8f,.5f,.015f), new(.04f,.45f,.08f)];
    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public static bool Valid(CableAnchor a) => a is not null && (a.Kind is "world" or "building" or "prop" or "entity") && a.Entity is not null && a.Entity.Length <= 128 && Finite(a.Point) && Finite(a.Normal) && Math.Abs(a.Normal.LengthSquared()-1)<.05f;
}
