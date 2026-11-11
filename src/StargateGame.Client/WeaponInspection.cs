namespace StargateGame.Client;

sealed partial class GameApp
{
    int weaponSmokeStage;
    // Explicit local-test automation uses the same commands and committed snapshots as UI clicks.
    void WeaponSmoke(double now)
    {
        if (session?.Inventory is null || session.WeaponPending || now - connectedAt < 6) return;
        double age = now - connectedAt;
        bool observer = args.Contains("--weapon-observer");
        if (weaponSmokeStage == 0)
        {
            var container = session.Changes.Pieces.FirstOrDefault(p => StargateGame.Shared.WeaponCatalog.IsContainer(p.Prefab));
            if (container is null) return;
            session.Weapon("open", container: container.Id); SetInventory(true); weaponSmokeStage++;
        }
        else if (weaponSmokeStage == 1 && age > 9 && session.OpenWeaponContainer is not null)
        {
            if (!observer && session.Inventory.Slots.FirstOrDefault(i => i is not null) is { } rifle)
            { selectedWeapon = rifle.Id; selectedShared = false; session.Weapon("store", rifle.Id); }
            weaponSmokeStage++;
        }
        else if (weaponSmokeStage == 2 && age > 13 && session.OpenWeaponContainer is not null)
        {
            if (observer && session.OpenWeaponContainer.Slots.FirstOrDefault(i => i is not null) is { } rifle)
            { selectedWeapon = rifle.Id; selectedShared = true; session.Weapon("take", rifle.Id); }
            weaponSmokeStage++;
        }
        else if (weaponSmokeStage == 3 && age > 18)
        {
            Console.WriteLine($"WEAPON VISUAL personal={session.Inventory.Slots.Count(i => i is not null)} shared={session.OpenWeaponContainer?.Slots.Count(i => i is not null)} equipped={session.Inventory.Equipped} status={session.WeaponStatus}");
            SetInventory(false); weaponSmokeStage++;
        }
    }
}
