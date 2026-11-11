using System.Numerics;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Building;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class GameApp
{
    string pickupHint = "";
    bool carryingConsumed, carryMouseGuard;
    void PickupInput(InputManager input)
    {
        carryingConsumed = false;
        pickupHint = "";
        if (session is null)
            return;
        if (!input.IsMouseButtonDown(MouseButton.Left))
            carryMouseGuard = false;
        if (CableInput(input)) return;
        if (PhysicsGunInput(input)) return;
        if (paused || bookOpen || buildMode || buildPaletteOpen || session.TransitLocked)
        {
            if (session.Carrying)
                session.Carry(CarryAction.Drop);
            return;
        }

        if (session.Carrying)
        {
            pickupHint = "E Drop   Left click Throw";
            carryingConsumed = true;
            if (input.WasKeyPressed(Key.E))
                session.Carry(CarryAction.Drop);
            else if (input.WasMouseButtonPressed(MouseButton.Left))
            {
                session.Carry(CarryAction.Throw);
                carryMouseGuard = true;
            }
            return;
        }
        var eye = camera.Position;
        var aim = camera.WorldForwardNormal;
        var prop = session.Physics.PickProp(session.Local.Id, eye, aim);
        var door = PickPiece();
        float doorDistance = door.Piece is { } piece && BuildingPrefabs.IsDoor(piece) ? Vector3.Distance(eye, door.Point) : float.PositiveInfinity;
        var storage = PickWeaponContainer(eye, aim);
        if (storage.Piece != Guid.Empty && storage.Distance < doorDistance && (prop.Prop is null || storage.Distance < prop.Distance))
        {
            pickupHint = "E Weapon storage";
            if (input.WasKeyPressed(Key.E)) { session.Weapon("open", container: storage.Piece); SetInventory(true); carryingConsumed = true; }
            return;
        }
        bool pickProp = prop.Prop is not null && prop.Distance < doorDistance;
        if (pickProp)
            pickupHint = "E Pick up";
        else if (float.IsFinite(doorDistance))
            pickupHint = "E Door";
        if (session.InteractionFeedback.Length > 0) pickupHint = session.InteractionFeedback;
        if (!input.WasKeyPressed(Key.E))
            return;
        if (pickProp)
        {
            session.Carry(CarryAction.Grab, prop.Prop!.PersistentId);
            carryingConsumed = true;
        }
        else if (float.IsFinite(doorDistance))
            session.Build(new(Guid.NewGuid(), session.Changes.Revision, "door", Target: door.Piece!.Id, HitPoint: door.Point));
    }
}
