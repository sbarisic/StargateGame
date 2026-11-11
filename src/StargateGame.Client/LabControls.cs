using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class GameApp
{
    static readonly string[] labClips = ["idle", "walk", "jog", "run", "jump", "fall", "land", "swim", "swim-idle", "bind"];
    int labClip = -1;
    double? labAnimationTime;
    void LabInput(InputManager input)
    {
        if (session is null || scene is null || !GraphicsFixture.IsFixture(session.World.Definition) || paused || bookOpen || buildPaletteOpen)
            return;
        if (input.WasKeyPressed(Key.F6))
        {
            labClip = (labClip + 1) % labClips.Length;
            labAnimationTime = 0;
        }
        if (input.WasKeyPressed(Key.F7))
            labAnimationTime = labAnimationTime is null ? session.ServerTick / 60d : null;
        if (input.WasKeyPressed(Key.F8))
            labAnimationTime = (labAnimationTime ?? session.ServerTick / 60d) + 1 / 30d;
        if (input.WasKeyPressed(Key.F9))
            scene.TimeOfDay = scene.TimeOfDay switch
            {
                12 => 18,
                18 => 0,
                _ => 12
            };
        if (input.WasKeyPressed(Key.F10))
            scene.CloudsEnabled = !scene.CloudsEnabled;
    }
}
