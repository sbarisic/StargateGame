using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class ClientSession
{
    public WeaponInventory? Inventory { get; private set; }
    public WeaponContainer? OpenWeaponContainer { get; private set; }
    public WeaponResult? LastWeaponResult { get; private set; }
    public string WeaponStatus { get; private set; } = "Loading inventory...";
    public bool WeaponPending { get; private set; }
    Guid pendingWeapon;
    bool closeWeaponsWhenReady;
    public void Weapon(string action, Guid? item = null, Guid container = default)
    {
        if (action == "close") { OpenWeaponContainer = null; if (WeaponPending) { closeWeaponsWhenReady = true; return; } }
        if (WeaponPending || Inventory is null || TransitLocked) return;
        pendingWeapon = Guid.NewGuid(); WeaponPending = true;
        if (action == "close") OpenWeaponContainer = null;
        connection.Peer.Send(new() { Kind = "weapon-command", LocationId = World.Definition.LocationId,
            WeaponCommand = new(pendingWeapon, action, Inventory.Revision, item,
                container != Guid.Empty ? container : OpenWeaponContainer?.Piece ?? Guid.Empty, OpenWeaponContainer?.Revision ?? 0) });
    }
    bool HandleWeapons(Message m)
    {
        if (m.Weapons is not { } result) return false;
        if (result.Inventory.Slots.Length != WeaponCatalog.PersonalSlots || result.Container?.Slots.Length is not (null or WeaponCatalog.ContainerSlots))
            throw new InvalidDataException("Invalid weapon snapshot.");
        if (Inventory is null || result.Inventory.Revision >= Inventory.Revision) Inventory = result.Inventory;
        if (result.Container is null || result.Container.Location == World.Definition.LocationId &&
            (OpenWeaponContainer?.Piece != result.Container.Piece || result.Container.Revision >= OpenWeaponContainer.Revision)) OpenWeaponContainer = result.Container;
        LastWeaponResult = result;
        WeaponStatus = result.Reason;
        if (result.Request == pendingWeapon) WeaponPending = false;
        // Equipment snapshots are authoritative even after a predicted presentation action.
        bool equipped = Inventory.Equipped is not null;
        var old = LocalCharacter;
        if (old.Equipped != equipped) Characters[localId] = old with { Equipped = equipped, Aiming = false, Action = equipped ? "draw" : "holster", StartTick = (long)GateTime };
        characterSequence = Math.Max(characterSequence, LocalCharacter.Sequence);
        if (closeWeaponsWhenReady && !WeaponPending) { closeWeaponsWhenReady = false; Weapon("close"); }
        return true;
    }
}
