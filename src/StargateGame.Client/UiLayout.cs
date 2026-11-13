using System.Numerics;

namespace StargateGame.Client;

readonly record struct UiRect(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public bool Contains(Vector2 p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
    public UiRect Inset(float amount) => new(X + amount, Y + amount, W - 2 * amount, H - 2 * amount);
}

// Logical window pixels. The painter snaps to framebuffer pixels at the final step.
sealed class UiLayout(float width, float height)
{
    public const float Margin = 20;
    public static float ContentScale(float w, float h, float dpi) => Math.Clamp(dpi, 1, Math.Max(1, Math.Min(w / 960, h / 600)));
    public float Width
    {
        get;
    } = width;
    public float Height
    {
        get;
    } = height;
    public UiRect Identity => new(Margin, Margin, 300, 62);
    public UiRect Compass => new(Width / 2 - Math.Min(460, Width - 664) / 2, 20, Math.Min(460, Width - 664), 52);
    public UiRect Explorers => new(Width - 300, 20, 280, 36);
    public UiRect Address => new(Width - 300, 66, 280, 156);
    public UiRect Equipment => new(Width - 270, Height - 82, 250, 62);
    public UiRect Notice => new(20, Height - 96, Math.Min(620, Width - 310), 76);
    public UiRect Connection => new((Width - 540) / 2, (Height - 552) / 2, 540, 552);
    public UiRect ConnectField(int index) => new(Connection.X + 24, Connection.Y + 112 + index * 70, 492, 58);
    public UiRect Join => new(Connection.X + 24, Connection.Y + 332, 492, 44);
    public UiRect Pause => new((Width - 860) / 2, (Height - 600) / 2, 860, 600);
    public UiRect PauseAction(int index) => new(Pause.X + 24, Pause.Y + 76 + index * 52, 374, 42);
    public UiRect PauseSecondaryAction(int index) => new(Pause.X + 436, Pause.Y + 432 + index * 44, 400, 34);
    public UiRect Inventory => new((Width - Math.Min(900, Width - 40)) / 2, (Height - 540) / 2, Math.Min(900, Width - 40), 540);
    public UiRect WeaponSlot(bool shared, int slot)
    {
        var p = Inventory; float half = (p.W - 64) / 2;
        return shared ? new(p.X + 40 + half + slot / 8 * (half / 2), p.Y + 106 + slot % 8 * 38, half / 2 - 6, 32)
            : new(p.X + 24, p.Y + 106 + slot * 58, half, 48);
    }
    public UiRect WeaponButton(int index) => new(Inventory.X + 24 + index * (Inventory.W - 48) / 4, Inventory.Bottom - 94, (Inventory.W - 72) / 4, 40);
    public UiRect Slider(int index) => new(Pause.X + 132, Pause.Y + 322 + index * 40, 218, 28);
    public UiRect Mute => new(Pause.X + 24, Pause.Y + 496, 374, 40);
    public UiRect TravelResume => new(Width / 2 - 210, Height / 2, 200, 44);
    public UiRect TravelDisconnect => new(Width / 2 + 10, Height / 2, 200, 44);
    public UiRect Developer => new((Width - 600) / 2, (Height - 440) / 2, 600, 440);
    public UiRect DeveloperAction(int i) => new(Developer.X + 24, Developer.Y + 76 + i * 58, 552, 44);
    public UiRect Book => new((Width - Math.Min(1060, Width - 40)) / 2, (Height - 550) / 2, Math.Min(1060, Width - 40), 550);
    public float BookColumn => (Book.W - 72) / 2;
    public UiRect BookClose => new(Book.Right - 94, Book.Y + 18, 70, 38);
    public UiRect BookCurrent => new(Book.X + 24, Book.Y + 404, BookColumn, 42);
    public UiRect BookRow(int row) => new(Book.X + 48 + BookColumn, Book.Y + 154 + row * 76, BookColumn, 70);
    public UiRect BookPage(bool next) => new(Book.X + 48 + BookColumn + (next ? BookColumn / 2 + 6 : 0), Book.Y + 472, BookColumn / 2 - 6, 40);
    public UiRect Picker => new((Width - Math.Min(1180, Width - 40)) / 2, 20, Math.Min(1180, Width - 40), Math.Min(780, Height - 40));
    public int Columns => Math.Max(1, (int)((Picker.W - 40) / 184));
    public int Rows => Math.Max(1, (int)((Picker.H - 160) / 164));
    public int PageSize => Columns * Rows;
    public UiRect Tile(int slot)
    {
        float w = (Picker.W - 40) / Columns;
        return new(Picker.X + 20 + slot % Columns * w, Picker.Y + 96 + slot / Columns * 164, w - 10, 154);
    }
    public UiRect PickerPage(bool next) => new(next ? Picker.Right - 180 : Picker.X + 20, Picker.Bottom - 54, 160, 36);
    public static float Heading(float yaw) => ((yaw * 180 / MathF.PI) % 360 + 360) % 360;
    public static float BearingDelta(float bearing, float heading) => (bearing - heading + 540) % 360 - 180;
    public static string Ellipsis(string value, int characters) => value.Length <= characters ? value : value[..Math.Max(0, characters - 3)] + "...";
}
