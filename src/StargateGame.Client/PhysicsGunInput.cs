using System.Numerics;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class GameApp
{
    bool physicsGunEquipped;
    void TogglePhysicsGun()
    {
        if(session?.World.Definition.Fixture!="gate-pair" || session.Reloading)return;
        if(session.Carrying)session.Carry(CarryAction.Drop);
        if(cableTool)ExitCable();
        physicsGunEquipped=!physicsGunEquipped;
        buildMode=false;
        if(scene is not null){scene.PhysicsGunEquipped=physicsGunEquipped;scene.PhysicsGunBeam=null;}
        carryMouseGuard=true;
    }
    bool PhysicsGunInput(InputManager input)
    {
        if(!physicsGunEquipped||session is null)return false;
        carryingConsumed=true;
        if(scene is not null)scene.PhysicsGunBeam=null;
        if(paused||bookOpen||buildMode||buildPaletteOpen||spawnMenuOpen||session.TransitLocked)
        {if(session.Carrying)session.Carry(CarryAction.Drop);return true;}
        pickupHint="Physics gun: hold Left mouse / Wheel distance / E + Mouse rotate / G rifle";
        if(!input.IsMouseButtonDown(MouseButton.Left)||input.WasMouseButtonPressed(MouseButton.Right))
        {if(session.Carrying)session.Carry(CarryAction.Drop);return true;}
        var hold=session.Physics.Carries.FirstOrDefault(c=>c.Player==session.Local.Id&&c.PhysicsGun);
        if(hold is not null)
        {
            var body=session.Physics.Bodies().FirstOrDefault(b=>b.Id==hold.Body);
            if(scene is not null)scene.PhysicsGunBeam=body.Position+Vector3.Transform(hold.GrabOffset,body.Rotation);
        }
        else
        {
            var hit=session.Physics.PickProp(session.Local.Id,camera.Position,camera.WorldForwardNormal,Movement.PhysicsGunRange);
            if(scene is not null)scene.PhysicsGunBeam=hit.Prop is not null?hit.Point:camera.Position+camera.WorldForwardNormal*Movement.PhysicsGunRange;
            if(input.WasMouseButtonPressed(MouseButton.Left)&&hit.Prop is not null)
                session.Carry(CarryAction.PhysicsGrab,hit.Prop.PersistentId);
        }
        return true;
    }
}
