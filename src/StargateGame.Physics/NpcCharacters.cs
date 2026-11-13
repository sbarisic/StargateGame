using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
namespace StargateGame.Physics;

public interface ICharacterMovement
{
    Vector3 Accelerate(Vector3 current,Vector3 wish,bool grounded,float airSpeedLimit);
}
// Both controllers share acceleration and platform handling. Jump/swim policies
// stay with the player until a character definition needs those capabilities.
readonly struct JoltCharacterMovement(CharacterVirtual character) : ICharacterMovement
{
    public Vector3 Accelerate(Vector3 current,Vector3 wish,bool grounded,float airSpeedLimit)
    {
        var support=grounded?character.GroundVelocity:Vector3.Zero;
        return Movement.Accelerate(current-support,wish,grounded,airSpeedLimit,character.GroundNormal)+support;
    }
}
public sealed partial class PhysicsWorld
{
    const uint NpcLayer=5;
    readonly List<CharacterVirtual> npcCharacters=[];
    readonly SortedDictionary<int,NpcMotion> npcMotions=[];
    readonly HashSet<int> activeNpcBodies=[];
    public IReadOnlyList<NpcMotion> NpcMotions => npcMotions.Values.ToArray();
    public Guid NpcForCollisionBody(int body)=>npcMotions.Values.FirstOrDefault(n=>npcCharacters[n.Slot].InnerBodyID.GetHashCode()==body).Id;
    void InitializeNpcCharacters()
    {
        var definition=CharacterDefinitions.Soldier;
        var capsule=Own(new RotatedTranslatedShape(new Vector3(0,definition.Height/2,0),Quaternion.Identity,
            Own(new CapsuleShape(definition.Height/2-definition.Radius,definition.Radius))));
        // Fixed slots keep checkpoint topology stable when an NPC appears or disappears.
        for(int slot=0;slot<NpcRules.Limit;slot++)
        {
            var c=Native.Own(new CharacterVirtual(new CharacterVirtualSettings {
                Shape=capsule,InnerBodyShape=capsule,InnerBodyLayer=NpcLayer,
                Mass=80,MaxStrength=Movement.PushStrength,MaxSlopeAngle=definition.SlopeDegrees*MathF.PI/180,
                SupportingVolume=new Plane(Vector3.UnitY,-definition.Radius),EnhancedInternalEdgeRemoval=true
            },new Vector3(0,-4096,0),Quaternion.Identity,(ulong)(1000+slot),system));
            npcCharacters.Add(c);system.BodyInterface.RemoveBody(c.InnerBodyID);
        }
    }
    public void AddNpc(Guid id,Vector3 position,float yaw)
    {
        lock(NativeGate)
        {
            if(id==Guid.Empty||!NpcRules.Finite(position)||!float.IsFinite(yaw)||npcMotions.Values.Any(n=>n.Id==id)||npcMotions.Count>=NpcRules.Limit)
                throw new InvalidOperationException("Invalid NPC identity or location capacity.");
            int slot=Enumerable.Range(0,NpcRules.Limit).First(s=>!npcMotions.ContainsKey(s));
            ApplyNpcs([..npcMotions.Values,new(id,slot,position,Vector3.Zero,yaw,false,Vector3.Zero,Tick,Tick)]);
        }
    }
    public void RemoveNpc(Guid id) {lock(NativeGate)ApplyNpcs(npcMotions.Values.Where(n=>n.Id!=id).ToArray());}
    public void SetNpcIntent(Guid id,Vector3 wish,float yaw)
    {
        if(!NpcRules.Finite(wish)||wish.LengthSquared()>100||!float.IsFinite(yaw))throw new ArgumentException("Invalid NPC movement intent.");
        lock(NativeGate)
        {
            foreach(var n in npcMotions.Values.ToArray())if(n.Id==id)
                npcMotions[n.Slot]=n with{WishVelocity=wish,CommandTick=Tick,Yaw=yaw};
        }
    }
    void ApplyNpcs(NpcMotion[] states)
    {
        SetNpcMembership(states);
        npcMotions.Clear();
        foreach(var n in states)
        {
            var c=npcCharacters[n.Slot];c.Position=n.Position;c.LinearVelocity=n.Velocity;
            system.BodyInterface.SetPositionRotationAndVelocity(c.InnerBodyID,n.Position,Quaternion.Identity,n.Velocity,Vector3.Zero);
            system.BodyInterface.InvalidateContactCache(c.InnerBodyID);
            Native.SG_Refresh(c.Handle,system.Handle,allocator,NpcLayer);
            npcMotions.Add(n.Slot,n);
        }
    }
    void SetNpcMembership(NpcMotion[] states)
    {
        var wanted=states.Select(n=>n.Slot).ToHashSet();
        foreach(int slot in activeNpcBodies.Except(wanted).ToArray())
        {system.BodyInterface.RemoveBody(npcCharacters[slot].InnerBodyID);activeNpcBodies.Remove(slot);}
        foreach(int slot in wanted.Except(activeNpcBodies))
        {
            var n=states.First(n=>n.Slot==slot);var c=npcCharacters[slot];c.Position=n.Position;
            system.BodyInterface.SetPositionRotationAndVelocity(c.InnerBodyID,n.Position,Quaternion.Identity,n.Velocity,Vector3.Zero);
            system.BodyInterface.AddBody(c.InnerBodyID,Activation.Activate);activeNpcBodies.Add(slot);
        }
    }
    void StepNpcs()
    {
        foreach(var n in npcMotions.Values.ToArray())
        {
            var c=npcCharacters[n.Slot];c.UpdateGroundVelocity();
            if(!HasTerrain(c.Position,2)){c.LinearVelocity=Vector3.Zero;npcMotions[n.Slot]=n with{Velocity=Vector3.Zero};continue;}
            bool grounded=c.GroundState==GroundState.OnGround && Vector3.Dot(c.LinearVelocity-c.GroundVelocity,c.GroundNormal)<.1f;
            // The same bounded extrapolation is recorded in prediction checkpoints.
            Vector3 wish=Tick-n.CommandTick<=6?n.WishVelocity:Vector3.Zero;
            var velocity=new JoltCharacterMovement(c).Accelerate(c.LinearVelocity,wish,grounded,CharacterDefinitions.Soldier.RunSpeed);
            velocity.Y-=Movement.Gravity*Movement.Dt;
            if(!HasTerrain(c.Position+velocity*Movement.Dt,2))velocity.X=velocity.Z=0;
            c.LinearVelocity=velocity;
            Native.SG_CharacterStep(c.Handle,system.Handle,allocator,Movement.Dt,NpcLayer,!grounded&&velocity.Y>0);
            npcMotions[n.Slot]=n with {Position=c.Position,Velocity=c.LinearVelocity,Grounded=c.GroundState==GroundState.OnGround};
        }
    }
    static void ValidateNpcs(NpcMotion[]? states)
    {
        if(states is null)return;
        if(states.Length>NpcRules.Limit||states.Any(n=>!NpcRules.ValidMotion(n))||states.Select(n=>n.Slot).Distinct().Count()!=states.Length||states.Select(n=>n.Id).Distinct().Count()!=states.Length)
            throw new InvalidDataException("Invalid NPC collision baseline.");
    }
}
