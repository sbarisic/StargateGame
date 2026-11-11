using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class GameApp
{
    double nextRifleFire;
    void CharacterInput(InputManager input)
    {
        if (session is null || paused || buildMode || buildPaletteOpen || dhdHover >= 0)
            return;
        var state = session.LocalCharacter;
        Key[] slots = [Key.Alpha1, Key.Alpha2, Key.Alpha3, Key.Alpha4];
        if (input.WasKeyPressed(Key.Alpha0)) { session.Weapon("holster"); return; }
        for (int i = 0; i < slots.Length; i++)
            if (input.WasKeyPressed(slots[i]) && session.Inventory?.Slots[i] is { } item)
            {
                session.Weapon(session.Inventory.Equipped == item.Id ? "holster" : "equip", item.Id);
                return;
            }
        if (!state.Equipped)
            return;
        bool aim = input.IsMouseButtonDown(MouseButton.Right);
        if (input.WasKeyPressed(Key.R))
        {
            session.Character(true, aim, "reload");
            nextRifleFire = timer.Elapsed.TotalSeconds + 2.8;
        }
        else if (input.IsMouseButtonDown(MouseButton.Left) && timer.Elapsed.TotalSeconds >= nextRifleFire)
        {
            session.Character(true, aim, "fire");
            nextRifleFire = timer.Elapsed.TotalSeconds + .12;
        }
        else if (aim != state.Aiming && state.Action != "reload")
            session.Character(true, aim, "idle");
    }
}
