using System.Numerics;
using StargateGame.Shared;
using StargateGame.Building;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    long nextSoundId;
    void EmitSound(WorldSoundKind kind, Vector3 position, float strength = 1, int body = 0)
    {
        var sound = new WorldSoundEvent(++nextSoundId, physics.Tick, kind, body, position, Math.Clamp(strength, 0, 1));
        foreach (var s in sessions.Where(s => s.PlayerId != 0 && s.RejectAt == 0))
            if (Vector3.DistanceSquared(physics.Player(s.PlayerId).Position, position) < 100 * 100)
                s.Peer.TrySendCosmetic(new()
                {
                    Kind = "world-sound",
                    LocationId = physics.World.Definition.LocationId,
                    Epoch = physics.Epoch,
                    Sounds = [sound]
                });
    }
    void PublishSoundContacts()
    {
        if (context.SoundEpoch != physics.Epoch)
        {
            context.SoundEpoch = physics.Epoch;
            context.SoundCooldown.Clear();
            context.SoundWarmup = physics.Tick + 15;
        }
        var contacts = physics.DrainSoundContacts();
        if (physics.Tick < context.SoundWarmup)
            return;
        foreach (var group in contacts.GroupBy(c => (c.BodyId, c.OtherId)))
        {
            var impact = group.MaxBy(c => c.Approach);
            var key = (impact.BodyId, impact.OtherId, false);
            if (impact.Approach > .65f && (!context.SoundCooldown.TryGetValue(key, out var last) || physics.Tick - last >= 12))
            {
                EmitSound(WorldSoundKind.Impact, impact.Position, Math.Clamp(impact.Approach / 6, .08f, 1), impact.BodyId);
                context.SoundCooldown[key] = physics.Tick;
            }
            var scrape = group.MaxBy(c => c.Sliding);
            key = (scrape.BodyId, scrape.OtherId, true);
            if (scrape.Sliding > .3f && (!context.SoundCooldown.TryGetValue(key, out var lastScrape) || physics.Tick - lastScrape >= 6))
            {
                EmitSound(WorldSoundKind.Scrape, scrape.Position, Math.Clamp(scrape.Sliding / 4, .05f, .5f), scrape.BodyId);
                context.SoundCooldown[key] = physics.Tick;
            }
        }
        if (physics.Tick % 60 == 0)
            foreach (var key in context.SoundCooldown.Where(p => physics.Tick - p.Value > 120).Select(p => p.Key).ToArray())
                context.SoundCooldown.Remove(key);
    }
    Vector3 BuildSoundPosition(BuildCommand command, BuildEdit result)
    {
        BuildTriangle[] mesh;
        if (command.Action == "clear")
            mesh = physics.SceneryMesh(command.Category, command.SceneryId, command.Chunk).ToArray();
        else
        {
            var state = command.Action is "remove" or "door" ? CurrentChanges : result.State;
            var piece = command.Action is "remove" or "door" ? state.Pieces.Single(p => p.Id == command.Target) : result.Piece!;
            mesh = BuildingGeometry.Mesh(state, piece);
        }
        var bounds = BuildingGeometry.Bounds(mesh);
        return (bounds.Min + bounds.Max) * .5f;
    }
    void PublishBuildSound(PendingEdit completed)
    {
        EmitSound(completed.Command.Action switch
        {
            "door" => WorldSoundKind.Door,
            "remove" => WorldSoundKind.Remove,
            "clear" => WorldSoundKind.Scenery,
            _ => WorldSoundKind.Place
        }, completed.SoundPosition);
    }
}
