using System.Numerics;
using JoltPhysicsSharp;
namespace StargateGame.Physics;

public readonly record struct SoundContact(int BodyId, int OtherId, Vector3 Position, float Approach, float Sliding);
public sealed partial class PhysicsWorld
{
    readonly object soundLock = new();
    readonly Queue<SoundContact> soundContacts = new(256);
    bool soundObservations;
    public void EnableSoundObservations()
    {
        if (soundObservations)
            return;
        system.OnContactAdded += ObserveSoundContact;
        system.OnContactPersisted += ObserveSoundContact;
        soundObservations = true;
    }
    void ObserveSoundContact(PhysicsSystem _, in Body a, in Body b, in ContactManifold manifold, ref ContactSettings settings)
    {
        // Observation only. Body callbacks are locked by Jolt; never call its locking
        // BodyInterface here or change ContactSettings.
        int id = 0, other = 0;
        foreach (var pair in props)
        {
            if (pair.Value == a.ID)
                id = pair.Key;
            if (pair.Value == b.ID)
                other = pair.Key;
        }
        if (id == 0 && other == 0 || manifold.PointCount == 0)
            return;
        var point = (manifold.GetWorldSpaceContactPointOn1(0) + manifold.GetWorldSpaceContactPointOn2(0)) * .5f;
        var relative = a.GetPointVelocity(point) - b.GetPointVelocity(point);
        float normal = Vector3.Dot(relative, manifold.WorldSpaceNormal);
        float slide = (relative - manifold.WorldSpaceNormal * normal).Length();
        var contact = new SoundContact(id != 0 ? id : other, id != 0 ? b.ID.GetHashCode() : a.ID.GetHashCode(), point, Math.Max(0, normal), slide);
        lock (soundLock)
        {
            if (soundContacts.Count < 256)
                soundContacts.Enqueue(contact);
        }
    }
    public SoundContact[] DrainSoundContacts()
    {
        lock (soundLock)
        {
            var result = soundContacts.ToArray();
            soundContacts.Clear();
            return result;
        }
    }
}
