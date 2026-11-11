using System.Numerics;
using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;
namespace StargateGame.Client;

sealed partial class GameApp
{
    static string BookPath => Path.Combine(StargateGame.Shared.UserData.Root, "addresses.json");
    AddressBookState addressBook = AddressBookState.Load(BookPath);
    readonly DhdAddressHint addressHint = new();
    readonly HashSet<int> hintCaptures = [];
    void CaptureDhdHint(RenderPass pass)
    {
        if (!args.Contains("--dhd-hint-check") || scene is null || session is null)
            return;
        int key = scene.DhdHint.Strength > .98f ? scene.DhdHint.Key : scene.DhdHint.Key < 0 ? 0 : -1;
        if (key < 0 || !hintCaptures.Add(key))
            return;
        Capture(pass, Path.Combine(captureDirectory, $"hint-{key:D2}.png"));
        Console.WriteLine($"DHD HINT key={key} strength={scene.DhdHint.Strength:F3} localHour={session.World.Definition.Environment!.Phase(session.EnvironmentSeconds) * 24:F2}");
    }
    GlyphRenderer glyphs = null!;
    bool bookOpen, bookInputConsumed;
    int bookScroll;
    Guid bookWorld;
    readonly HashSet<string> bookCaptures = [];
    int bookCheckStage;
    void InspectBook(double age)
    {
        if (!args.Contains("--address-book-check"))
            return;
        if (bookCheckStage == 0 && age > 4)
        {
            SetBook(true);
            bookCheckStage++;
        }
        if (bookCheckStage == 1 && age > 8)
        {
            SetBook(false);
            bookCheckStage++;
        }
        if (bookCheckStage == 2 && age > 12)
        {
            window.SetClientSize(960, 600);
            SetBook(true);
            bookCheckStage++;
        }
        if (bookCheckStage == 3 && age > 18 && CurrentAddress is { } current)
        {
            SelectBook(current);
            SetBook(false);
            bookCheckStage++;
        }
    }
    void CaptureBook(RenderPass pass, double age)
    {
        if (!args.Contains("--address-book-check"))
            return;
        string? name = age > 20 ? "book-selected-hud" : age > 15 ? "book-960" : age is > 9 and < 12 ? "book-hud" : age is > 6 and < 8 ? "book-1920" : null;
        if (name is not null && bookCaptures.Add(name))
            Capture(pass, Path.Combine(captureDirectory, name + ".png"));
    }
    Guid BookUniverse => session?.World.Definition.UniverseId ?? (UiPreview ? PreviewUniverse : Guid.Empty);
    BookAddress? CurrentAddress => session is null ? (UiPreview ? addressBook.History(BookUniverse).FirstOrDefault() : null) : addressBook.History(BookUniverse).FirstOrDefault(e => e.Location == session.World.Definition.LocationId);
    void SaveBook()
    {
        if (UiPreview || UiChecking)
            return;
        try
        {
            addressBook.Save(BookPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("Address book could not save: " + e.Message);
        }
    }
    void UpdateBook()
    {
        if (session is null)
            return;
        var world = session.World.Definition;
        if (session.ReturnAddress.Length != 6)
            return;
        if (bookWorld == world.LocationId)
            return;
        bookWorld = world.LocationId;
        bookScroll = 0;
        if (addressBook.Visit(new(world.UniverseId, world.LocationId, session.CurrentWorldName.Length > 0 ? session.CurrentWorldName : world.Name, session.ReturnAddress)))
            SaveBook();
    }
    void SetBook(bool open)
    {
        bookOpen = open;
        SetPause(open);
        if (open)
        {
            buildMode = false;
            scene?.Preview(null, false);
        }
    }
    void BookInput(InputManager input)
    {
        if (input.WasKeyPressed(Key.Tab) || input.WasKeyPressed(Key.Escape))
        {
            SetBook(false);
            return;
        }
        var l = Layout;
        var p = UiMouse;
        var entries = addressBook.History(BookUniverse).ToArray();
        if (input.WasKeyPressed(Key.Down) || input.WasKeyPressed(Key.PageDown))
            bookScroll = Math.Min(Math.Max(0, entries.Length - 4), bookScroll + 1);
        if (input.WasKeyPressed(Key.Up) || input.WasKeyPressed(Key.PageUp))
            bookScroll = Math.Max(0, bookScroll - 1);
        bookScroll = Math.Clamp(bookScroll, 0, Math.Max(0, entries.Length - 4));
        if (!input.WasMouseButtonPressed(MouseButton.Left))
            return;
        if (l.BookClose.Contains(p))
        {
            SetBook(false);
            return;
        }
        if (l.BookCurrent.Contains(p) && CurrentAddress is { } current)
            SelectBook(current);
        for (int row = 0; row < 4; row++)
            if (l.BookRow(row).Contains(p) && bookScroll + row < entries.Length)
                SelectBook(entries[bookScroll + row]);
        if (l.BookPage(false).Contains(p))
            bookScroll = Math.Max(0, bookScroll - 4);
        if (l.BookPage(true).Contains(p))
            bookScroll = Math.Min(Math.Max(0, entries.Length - 4), bookScroll + 4);
    }
    void SelectBook(BookAddress selected)
    {
        addressBook.Select(selected);
        SaveBook();
    }
    static string BookName(string name, int limit = 34) => name.Length > limit ? name[..(limit - 3)] + "..." : name;
    void DrawBook(RenderPass pass)
    {
        var selected = addressBook.Selection(BookUniverse);
        var l = Layout;
        if (!bookOpen)
        {
            var dialed = session?.Gate?.Symbols ?? [];
            if (UiPreview && selected is not null)
                dialed = selected.Symbols.Concat(new[] { 1 }).ToArray();
            if (Smoke && args.Contains("--address-hud-check") && selected is not null)
                dialed = timer.Elapsed.TotalSeconds - connectedAt < 8 ? selected.Symbols.Take(3).ToArray() : selected.Symbols.Concat(new[] { 1 }).ToArray();
            if (selected is null && dialed.Length == 0)
                return;
            var a = l.Address;
            Panel(pass, a with
            {
                H = selected is not null && dialed.Length > 0 ? 156 : 84
            });
            float rowY = a.Y + 9;
            if (selected is not null)
            {
                Text(pass, a.X + 12, rowY, Fit(selected.Name, a.W - 24, 16), 16, UiTheme.Amber);
                glyphs.Address(pass, selected.Symbols, a.X + 12, rowY + 27, 30, UiHeight, UiTheme.Amber);
                rowY += 76;
            }
            if (dialed.Length > 0)
            {
                Text(pass, a.X + 12, rowY, "CURRENT DIAL / " + dialed.Length + " SYMBOLS", 14, UiTheme.Muted);
                glyphs.Address(pass, dialed, a.X + 12, rowY + 24, 28, UiHeight, UiTheme.Cyan);
            }
            return;
        }
        var r = l.Book;
        float x = r.X + 24, y = r.Y;
        float right = l.BookRow(0).X;
        Rect(pass, 0, 0, UiWidth, UiHeight, new Color(4, 10, 16, 180));
        Panel(pass, r, true);
        Text(pass, x, y + 25, "STARGATE / NAVIGATION", 22, UiTheme.Amber);
        Button(pass, l.BookClose, "Close");
        ui.Line(pass, x, y + 76, r.Right - 24, y + 76, UiTheme.Line);
        Text(pass, x, y + 104, "SELECTED DESTINATION", 14, UiTheme.Muted);
        if (selected is not null)
        {
            Text(pass, x, y + 134, Fit(selected.Name, l.BookColumn, 18), 18, UiTheme.Amber);
            glyphs.Address(pass, selected.Symbols, x, y + 170, 48, UiHeight, UiTheme.Amber);
            Text(pass, x, y + 228, string.Join("  ", selected.Symbols.Select(n => n.ToString("D2"))), 14, UiTheme.Muted);
            if (selected.Symbols.Contains(13))
                Wrapped(pass, new(x, y + 252, l.BookColumn, 42), "! Glyph 13 unavailable on a standard DHD.", 14, UiTheme.Error);
        }
        else
            Text(pass, x, y + 154, "No destination selected", 16, UiTheme.Muted);
        Text(pass, x, y + 306, "CURRENT WORLD", 14, UiTheme.Muted);
        if (CurrentAddress is { } current)
        {
            Text(pass, x, y + 332, Fit(current.Name, l.BookColumn, 16), 16);
            glyphs.Address(pass, current.Symbols, x, y + 360, 30, UiHeight, UiTheme.Text);
        }
        else
            Text(pass, x, y + 342, "Waiting for gate address...", 16, UiTheme.Muted);
        Button(pass, l.BookCurrent, "Select current world", enabled: CurrentAddress is not null);
        Wrapped(pass, new(x, y + 470, l.BookColumn, 60), "Six symbols, then local origin on the DHD. TAB / ESC closes this console.");
        Text(pass, right, y + 104, "TRAVEL HISTORY", 16, UiTheme.Amber);
        var entries = addressBook.History(BookUniverse).ToArray();
        bookScroll = Math.Clamp(bookScroll, 0, Math.Max(0, entries.Length - 4));
        for (int row = 0; row < Math.Min(4, entries.Length - bookScroll); row++)
        {
            var entry = entries[bookScroll + row];
            var rr = l.BookRow(row);
            bool active = entry.Location == selected?.Location;
            ui.Frame(pass, rr, rr.Contains(UiMouse) ? UiTheme.Hover : UiTheme.Panel, selected: active);
            Text(pass, rr.X + 12, rr.Y + 8, Fit((active ? "> " : "") + entry.Name, rr.W - 24, 16), 16, active ? UiTheme.Amber : UiTheme.Text);
            glyphs.Address(pass, entry.Symbols, rr.X + 12, rr.Y + 34, 28, UiHeight, active ? UiTheme.Amber : UiTheme.Muted);
        }
        if (entries.Length == 0)
            Text(pass, right, y + 178, "Visited worlds appear here.", 14, UiTheme.Muted);
        Button(pass, l.BookPage(false), "< Previous", enabled: bookScroll > 0);
        Button(pass, l.BookPage(true), "Next >", enabled: bookScroll + 4 < entries.Length);
        Text(pass, right, y + 523, $"{entries.Length} RECORDED WORLDS", 14, UiTheme.Muted);
    }
}
