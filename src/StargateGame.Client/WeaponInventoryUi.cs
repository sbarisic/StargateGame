using System.Numerics;
using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Building;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class GameApp
{
    bool inventoryOpen, inventoryHadContainer;
    Guid? selectedWeapon;
    bool selectedShared;
    UiRect InventoryPanel => Layout.Inventory;
    UiRect WeaponSlot(bool shared, int slot) => Layout.WeaponSlot(shared, slot);
    UiRect WeaponButton(int index) => Layout.WeaponButton(index);
    void SetInventory(bool open)
    {
        if (!open && session is not null) session.Weapon("close");
        inventoryOpen = open; inventoryHadContainer = false; selectedWeapon = null;
        SetPause(open);
        if (open && session?.LocalCharacter.Aiming == true) session.Character(session.LocalCharacter.Equipped, false, "idle");
    }
    bool InventoryInput(InputManager input)
    {
        if (session is null) return false;
        if (inventoryOpen && (session.TransitLocked || inventoryHadContainer && session.OpenWeaponContainer is null)) { SetInventory(false); return true; }
        if (!inventoryOpen)
        {
            if (!paused && !bookOpen && !buildMode && !buildPaletteOpen && !session.TransitLocked && input.WasKeyPressed(Key.I))
            { SetInventory(true); return true; }
            return false;
        }
        uiInputConsumed = true;
        inventoryHadContainer |= session.OpenWeaponContainer is not null;
        if (input.WasKeyPressed(Key.I) || input.WasKeyPressed(Key.Escape)) { SetInventory(false); return true; }
        if (input.WasKeyPressed(Key.Alpha0)) { session.Weapon("holster"); return true; }
        Key[] slotKeys = [Key.Alpha1, Key.Alpha2, Key.Alpha3, Key.Alpha4];
        for (int i = 0; i < slotKeys.Length; i++)
            if (input.WasKeyPressed(slotKeys[i]) && session.Inventory?.Slots[i] is { } item)
            {
                selectedWeapon = item.Id; selectedShared = false;
                session.Weapon(session.Inventory.Equipped == item.Id ? "holster" : "equip", item.Id);
                return true;
            }
        if (!input.WasMouseButtonPressed(MouseButton.Left)) return true;
        for (int i = 0; i < 4; i++) if (WeaponSlot(false, i).Contains(UiMouse)) { selectedWeapon = session.Inventory?.Slots[i]?.Id; selectedShared = false; }
        for (int i = 0; i < 16; i++) if (WeaponSlot(true, i).Contains(UiMouse)) { selectedWeapon = session.OpenWeaponContainer?.Slots[i]?.Id; selectedShared = true; }
        if (WeaponButton(0).Contains(UiMouse) && !selectedShared && selectedWeapon is not null) session.Weapon("equip", selectedWeapon);
        if (WeaponButton(1).Contains(UiMouse)) session.Weapon("holster");
        if (WeaponButton(2).Contains(UiMouse) && !selectedShared && selectedWeapon is not null && session.OpenWeaponContainer is not null) session.Weapon("store", selectedWeapon);
        if (WeaponButton(3).Contains(UiMouse) && selectedShared && selectedWeapon is not null) session.Weapon("take", selectedWeapon);
        return true;
    }
    void DrawWeaponInventory(RenderPass pass)
    {
        var p = InventoryPanel;
        Rect(pass, 0, 0, UiWidth, UiHeight, new Color(4, 10, 16, 120)); Panel(pass, p, true);
        Text(pass, p.X + 24, p.Y + 24, "EQUIPMENT / WEAPON STORAGE", 22, UiTheme.Amber);
        Text(pass, p.X + 24, p.Y + 72, "PERSONAL / 4 SLOTS", 16);
        Text(pass, p.X + p.W / 2 + 8, p.Y + 72, session?.OpenWeaponContainer is null ? "NO CONTAINER OPEN" : "SHARED / 16 SLOTS", 16);
        for (int i = 0; i < 4; i++)
        {
            var item = session?.Inventory?.Slots[i];
            Button(pass, WeaponSlot(false, i), $"{i + 1}  " + (item is null ? "EMPTY" : "RIFLE" + (session!.Inventory!.Equipped == item.Id ? " / EQUIPPED" : "")), selected: !selectedShared && item is not null && item.Id == selectedWeapon);
        }
        for (int i = 0; i < 16; i++)
        {
            var item = session?.OpenWeaponContainer?.Slots[i];
            Button(pass, WeaponSlot(true, i), $"{i + 1:00} " + (item is null ? "--" : "RIFLE"), selected: selectedShared && item is not null && item.Id == selectedWeapon);
        }
        string[] actions = ["Equip", "Holster", "Store", "Take"];
        bool ready = session?.WeaponPending == false && session.Inventory is not null;
        bool ownedSelection = !selectedShared && selectedWeapon is not null && session?.Inventory?.Slots.Any(s => s?.Id == selectedWeapon) == true;
        bool sharedSelection = selectedShared && selectedWeapon is not null && session?.OpenWeaponContainer?.Slots.Any(s => s?.Id == selectedWeapon) == true;
        bool[] enabled = [ready && ownedSelection,
            ready && session!.Inventory!.Equipped is not null,
            ready && ownedSelection && session!.OpenWeaponContainer is not null,
            ready && sharedSelection && session!.Inventory!.Slots.Any(s => s is null)];
        for (int i = 0; i < 4; i++) Button(pass, WeaponButton(i), actions[i], enabled: enabled[i]);
        Text(pass, p.X + 24, p.Bottom - 38, Fit(session?.WeaponPending == true ? "Committing..." : session?.WeaponStatus is { Length: > 0 } reason ? reason : "I / ESC Close    1-4 Select or holster    0 Empty hands", p.W - 48, 14), 14, UiTheme.Muted);
    }
    (Guid Piece, float Distance) PickWeaponContainer(Vector3 eye, Vector3 direction)
    {
        if (session is null) return (Guid.Empty, float.PositiveInfinity);
        Guid best = Guid.Empty; float distance = WeaponCatalog.Reach;
        foreach (var piece in session.Changes.Pieces.Where(p => WeaponCatalog.IsContainer(p.Prefab)))
            if (BuildingGeometry.Ray(eye, direction, BuildingGeometry.Mesh(session.Changes, piece), distance) is { } d &&
                !session.Physics.InteractionBlocked(eye, direction, Math.Max(0, d - .03f), ignorePlayer: session.Local.Id))
            { best = piece.Id; distance = d; }
        return (best, best == Guid.Empty ? float.PositiveInfinity : distance);
    }
}
