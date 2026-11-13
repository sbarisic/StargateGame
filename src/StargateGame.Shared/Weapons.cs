namespace StargateGame.Shared;

public sealed record WeaponItem(Guid Id, string CatalogId = "rifle", int Magazine = 29, bool Chambered = true, int Reserve = 120);
public sealed record WeaponInventory(long Revision, WeaponItem?[] Slots, Guid? Equipped);
public sealed record WeaponContainer(Guid Location, Guid Piece, long Revision, WeaponItem?[] Slots);
public sealed record WeaponRequest(Guid Id, string Action, long InventoryRevision = 0, Guid? Item = null,
    Guid Container = default, long ContainerRevision = 0);
public sealed record WeaponResult(Guid Request, bool Success, string Reason, WeaponInventory Inventory, WeaponContainer? Container = null);

public static class WeaponCatalog
{
    public const int PersonalSlots = 4, ContainerSlots = 16;
    public const float Reach = 2.5f;
    // Included in content compatibility independently of save/world generation revisions.
    public const string Identity = "weapons-v2:rifle:30+1:120:reload164:fire8:4:16:2.5";
    public static bool IsContainer(string prefab) => prefab is "weapon-crate" or "furnishing-chest";
    public static WeaponInventory Starter(bool equipped)
    {
        var item = new WeaponItem(Guid.NewGuid());
        return new(0, [item, null, null, null], equipped ? item.Id : null);
    }

    // Pure state transition, also used by the transient fixture server. Persistence
    // commits both results in one transaction before acknowledging any mutation.
    public static WeaponResult Apply(WeaponInventory inventory, WeaponContainer? container, WeaponRequest request)
    {
        WeaponResult Fail(string error) => new(request.Id, false, error, inventory, container);
        if (request.Id == Guid.Empty) return Fail("Invalid request ID.");
        if (request.Action is "snapshot" or "open" or "close") return new(request.Id, true, "", inventory, container);
        if (inventory.Revision != request.InventoryRevision) return Fail("Inventory changed; refreshed.");
        var personal = (WeaponItem?[])inventory.Slots.Clone();
        int own = Array.FindIndex(personal, i => i is not null && i.Id == request.Item);
        if (request.Action == "equip")
        {
            if (own < 0) return Fail("You do not own this weapon.");
            inventory = inventory with { Revision = inventory.Revision + 1, Equipped = request.Item };
        }
        else if (request.Action == "holster")
            inventory = inventory with { Revision = inventory.Revision + 1, Equipped = null };
        else if (request.Action is "store" or "take")
        {
            if (container is null || container.Piece != request.Container) return Fail("Open the container first.");
            if (container.Revision != request.ContainerRevision) return Fail("Container changed; refreshed.");
            var shared = (WeaponItem?[])container.Slots.Clone();
            bool deposit = request.Action == "store";
            var source = deposit ? personal : shared;
            var target = deposit ? shared : personal;
            int from = Array.FindIndex(source, i => i is not null && i.Id == request.Item);
            int to = Array.FindIndex(target, i => i is null);
            if (from < 0) return Fail("Weapon is no longer in the source.");
            if (to < 0) return Fail(deposit ? "Container is full." : "All four weapon slots are occupied.");
            target[to] = source[from]; source[from] = null;
            inventory = new(inventory.Revision + 1, personal, deposit && inventory.Equipped == request.Item ? null : inventory.Equipped);
            container = container with { Revision = container.Revision + 1, Slots = shared };
        }
        else return Fail("Unknown weapon action.");
        return new(request.Id, true, "", inventory, container);
    }
}
