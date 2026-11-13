using System.Numerics;
namespace StargateGame.Shared;

[Flags] public enum CharacterCapabilities { None=0, Walk=1, UseDoors=2 }
public sealed record CharacterDefinition(string Id, string Model, float Radius, float Height, float WalkSpeed,
    float RunSpeed, float StepHeight, float SlopeDegrees, CharacterCapabilities Capabilities,
    string Idle, string Walk, string Run);
public static class CharacterDefinitions
{
    public static readonly CharacterDefinition Soldier = new("military", "military", .32f, 1.8f, 2.7f, 5.4f, .3f, 45,
        CharacterCapabilities.Walk|CharacterCapabilities.UseDoors, "idle", "walk", "run");
    public static readonly CharacterDefinition[] All=[Soldier];
    public static CharacterDefinition Get(string id) => All.FirstOrDefault(d=>d.Id==id)??throw new InvalidDataException("Unknown character definition.");
    public static bool Contains(string? id) => All.Any(d=>d.Id==id);
}
public enum NpcOrderKind { Hold, MoveTo, Follow, Wander, Patrol }
public enum NavigationOutcome { Ready, PendingNavigation, Unreachable, Blocked }
public enum NpcSchedule { Hold, MoveTo, Follow, Wander, Patrol, Investigate, Flee, Search }
public enum NpcTaskState { Running, Complete, Failed, Cancelled }
public sealed record NpcOrder(NpcOrderKind Kind, Vector3[] Points, int FollowPlayer=0);
public sealed record NpcRecord(Guid Id, string Definition, Guid Location, string Owner, string Faction,
    Vector3 Position, float Yaw, Vector3 Home, NpcOrder Order, long EffectiveTick);
// Simulation state, never filled from a render interpolation. Slots have stable native bodies.
public readonly record struct NpcMotion(Guid Id, int Slot, Vector3 Position, Vector3 Velocity, float Yaw,
    bool Grounded, Vector3 WishVelocity, long CommandTick, long EffectiveTick);
public sealed record NpcStatus(Guid Id, NpcSchedule Schedule, string Task, NavigationOutcome Navigation,
    string Reason, Vector3[] Path, Vector3? Threat=null);
public sealed record NpcSnapshot(long Revision, long EffectiveTick, NpcRecord[] Records, NpcStatus[] Status);
public sealed record NpcCommand(Guid Request, string Action, Guid Target=default, NpcOrder? Order=null,
    Vector3 Origin=default, Vector3 Point=default, float CellSize=NpcRules.NavigationCellSize, int HostilePlayer=0);
public sealed record NpcReply(Guid Request, bool Accepted, string Reason, Guid Target=default,
    NavigationOutcome? Navigation=null,Vector3[]? Path=null);
public readonly record struct NpcDebugLine(Vector3 A,Vector3 B,Vector3 Color);
public sealed record NpcTileMetric(int X,int Z,long Revision,string State,double Milliseconds,long Bytes);
public sealed record NpcDebugSnapshot(long Tick,int Part,int Count,float CellSize,NpcTileMetric[] Tiles,NpcDebugLine[] Lines);
public static class NpcRules
{
    // Coarser cells close the authored house entrances after agent erosion.
    public const float NavigationCellSize=.1f;
    public const int Limit=16, Schema=1;
    public static bool Finite(Vector3 p) => float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)&&p.LengthSquared()<1e8f;
    public static bool ValidOrder(NpcOrder? o) => o is not null && Enum.IsDefined(o.Kind) && o.Points is not null &&
        o.Points.Length<=16 && o.Points.All(Finite) && (o.Kind switch {
            NpcOrderKind.Patrol => o.Points.Length>=2,
            NpcOrderKind.Follow => o.Points.Length==0 && o.FollowPlayer>0,
            _ => o.Points.Length==1 });
    public static bool Valid(NpcRecord n) => n.Id!=Guid.Empty && CharacterDefinitions.Contains(n.Definition) &&
        n.Location!=Guid.Empty && n.Owner is {Length:>0 and <=128} && n.Faction is {Length:>0 and <=32} &&
        Finite(n.Position)&&Finite(n.Home)&&float.IsFinite(n.Yaw)&&n.EffectiveTick>=0&&ValidOrder(n.Order);
    public static bool ValidMotion(NpcMotion m) => m.Id!=Guid.Empty && m.Slot>=0&&m.Slot<Limit && Finite(m.Position)&&
        Finite(m.Velocity)&&Finite(m.WishVelocity)&&m.WishVelocity.LengthSquared()<=100 && float.IsFinite(m.Yaw)&&m.CommandTick>=0&&m.EffectiveTick>=0;
}
