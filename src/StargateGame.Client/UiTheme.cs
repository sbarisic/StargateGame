using FishGfx;

namespace StargateGame.Client;

static class UiTheme
{
    public static readonly Color Panel = new(8, 17, 25, 191), Menu = new(8, 17, 25, 247); // HUD: 75% opacity, 25% transparent.
    public static readonly Color Diagnostics = Panel;
    public static readonly Color Amber = new(244, 181, 68), Text = new(233, 237, 240);
    public static readonly Color Muted = new(160, 173, 184), Cyan = new(80, 220, 230), Error = new(239, 106, 96);
    public static readonly Color Line = new(122, 98, 55), Hover = new(44, 48, 46, 245);
    public const float Heading = 22, Body = 16, Secondary = 14;
}
