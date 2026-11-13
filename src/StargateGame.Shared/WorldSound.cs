using System.Numerics;
namespace StargateGame.Shared;

public enum WorldSoundKind
{
    Impact, Scrape, Place, Remove, Scenery, Door, GateArrival, RifleFire, RifleReload, RifleDry, RifleImpact
}
public readonly record struct WorldSoundEvent(long Id, long Tick, WorldSoundKind Kind, int BodyId, Vector3 Position, float Strength, int SourcePlayer = 0);

// Audio is cosmetic. Neither event history nor playback belongs in saved physics.
public sealed class SoundEventHistory
{
    Guid location;
    long epoch = -1, lastId;
    public bool Accept(Guid world, long generation, WorldSoundEvent sound)
    {
        if (world != location || generation != epoch)
        {
            location = world;
            epoch = generation;
            lastId = 0;
        }
        if (sound.Id <= lastId || !Enum.IsDefined(sound.Kind) || !float.IsFinite(sound.Strength) || sound.Strength < 0 || sound.Strength > 1 ||
            !float.IsFinite(sound.Position.X) || !float.IsFinite(sound.Position.Y) || !float.IsFinite(sound.Position.Z))
            return false;
        lastId = sound.Id;
        return true;
    }
}
