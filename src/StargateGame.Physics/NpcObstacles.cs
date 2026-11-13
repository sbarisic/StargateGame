using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;

namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    Shape? npcSteeringProbe;
    readonly List<ShapeCastResult> npcSteeringHits=[];

    // Broad/narrow phase against live bodies, including moving props and other
    // characters. Lift the probe's feet over valid steps; Jolt handles the step.
    public float NpcObstacleFraction(Guid npc,Vector3 from,Vector3 to,Action<int,Vector3,float>? blocked=null)
    {
        lock(NativeGate)
        {
            var delta=to-from;
            if(delta.LengthSquared()<.000001f)return 1;
            var definition=CharacterDefinitions.Soldier;
            float bottom=definition.StepHeight+.04f,top=definition.Height;
            // Use an untranslated capsule: the cast API accepts a world transform,
            // not a second centre-of-mass offset for a translated child shape.
            npcSteeringProbe??=Own(new CapsuleShape((top-bottom)*.5f-definition.Radius,definition.Radius));
            interactionFilter??=Native.Own(new InteractionFilter());
            var motion=npcMotions.Values.FirstOrDefault(n=>n.Id==npc);
            interactionFilter.Ignore=motion.Id==npc&&npc!=Guid.Empty?npcCharacters[motion.Slot].InnerBodyID:BodyID.Invalid;
            interactionFilter.Player=BodyID.Invalid;interactionFilter.Buildings=null;
            npcSteeringHits.Clear();
            var matrix=Matrix4x4.Transpose(Matrix4x4.CreateTranslation(from+Vector3.UnitY*((bottom+top)*.5f)));
            system.NarrowPhaseQuery.CastShape(npcSteeringProbe,matrix,delta,Vector3.Zero,CollisionCollectorType.AllHit,npcSteeringHits,bodyFilter:interactionFilter);
            float fraction=1;
            foreach(var hit in npcSteeringHits)
            {
                float length=hit.PenetrationAxis.Length();
                if(length>0&&hit.PenetrationAxis.Y/length<-.7071f)continue; // Walkable support.
                if(hit.Fraction<=.00001f&&Vector3.Dot(delta,hit.PenetrationAxis)<=.0001f*Math.Max(1,delta.Length()))continue;
                blocked?.Invoke(hit.BodyID2.GetHashCode(),hit.PenetrationAxis,hit.Fraction);
                fraction=Math.Min(fraction,Math.Max(0,hit.Fraction-.025f/delta.Length()));
            }
            return fraction;
        }
    }
}
