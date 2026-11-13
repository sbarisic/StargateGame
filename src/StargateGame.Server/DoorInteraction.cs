using System.Numerics;
using System.Diagnostics;
using StargateGame.Building;
using StargateGame.Shared;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    string ValidateDoorSweep(BuildingPiece door)
    {
        long started=Stopwatch.GetTimestamp();
        var structure=CurrentChanges.Structures.Single(s=>s.Id==door.StructureId);
        var root=DoorGeometry.Root(structure,door);
        Matrix4x4.Invert(root,out var inverse);
        var profile=DoorGeometry.For(door);
        var envelope=BuildingQuery.TransformBounds(profile.Envelope,root);
        // Swing obstruction is dynamic-only. Authored frames, walls, terrain,
        // scenery and map edges never veto opening or closing a door.
        // Reach and line-of-sight checks still run separately in ValidateEdit.
        var dynamic=physics.Bodies().Select(body=>(Body:body,Definition:physics.World.Definition.Props.FirstOrDefault(p=>p.Id==body.Id)))
            .Where(p=>p.Definition is not null && Vector3.DistanceSquared(p.Body.Position,Vector3.Clamp(p.Body.Position,envelope.Min,envelope.Max))<=p.Definition.HalfExtent.LengthSquared())
            .SelectMany(p=>physics.DoorPropHulls(p.Body,p.Definition!).Select(hull=>(p.Body.Id,Name:p.Definition!.Name,Hull:hull)))
            .Where(p=>p.Hull is not null)
            .Select(p=>(p.Id,p.Name,Hull:p.Hull!,Local:p.Hull!.Vertices.Select(v=>Vector3.Transform(v,inverse)).ToArray())).ToArray();
        var players=physics.Players.Where(p=>new BuildBounds(p.Position-new Vector3(Movement.Radius,0,Movement.Radius),p.Position+new Vector3(Movement.Radius,Movement.BodyHeight(p),Movement.Radius)).Intersects(envelope,-.001f)).ToArray();
        string Block(string name,string id,DoorGeometry.Sweep sweep)
        {
            Console.WriteLine($"DOOR rejected door={door.Id} blocker={id} kind={name} section={sweep.Section} interval={sweep.Degree}..{sweep.Degree+1} candidates={dynamic.Length+players.Length} ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}");
            return $"Door blocked by {name}.";
        }
        float angle = physics.DoorStates.FirstOrDefault(d => d.Id == door.Id)?.Angle ?? (door.Open ? 0 : MathF.PI / 2);
        float targetAngle = door.Open ? MathF.PI / 2 : 0;
        // Only the remaining arc matters when a moving leaf is reversed.
        foreach(var sweep in profile.Sweeps.Where(s => s.Degree + 1 > Math.Min(angle, targetAngle) * 180 / MathF.PI && s.Degree < Math.Max(angle, targetAngle) * 180 / MathF.PI))
        {
            Matrix4x4.Invert(sweep.Pose,out var inv);
            foreach(var p in dynamic)
                if(SolidGeometry.ConvexBox(p.Local.Select(v=>Vector3.Transform(v,inv)).ToArray(),p.Hull.Faces,p.Hull.Edges,sweep.Bounds))
                    return Block(p.Name,p.Id.ToString(),sweep);
            foreach(var n in physics.NpcMotions)
                if(DoorGeometry.CapsuleBox(Vector3.Transform(Vector3.Transform(n.Position,inverse),inv),Movement.Height,Movement.Radius,sweep.Bounds))
                    return Block("NPC",n.Id.ToString(),sweep);
            foreach(var p in players)
                if(DoorGeometry.CapsuleBox(Vector3.Transform(Vector3.Transform(p.Position,inverse),inv),Movement.BodyHeight(p),Movement.Radius,sweep.Bounds))
                    return Block("player",p.Id.ToString(),sweep);
        }
        Console.WriteLine($"DOOR clear door={door.Id} candidates={dynamic.Length+players.Length} ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}");
        return "";
    }
}
