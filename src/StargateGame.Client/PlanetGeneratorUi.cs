using System.Globalization;
using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class GameApp
{
    bool generatorTab = true, generatorLoaded, generatorSelectAll;
    int generatorFocus, generatorScroll;
    string generatorSeed = "7319", generatorTime = "11:00", generatorError = "";
    PlanetDebugSettings generatorDraft = new(7319, "temperate", new(), new(), new());
    UiRect GeneratorPanel => new(20, 20, Math.Min(1000, UiWidth - 40), Math.Max(380, UiHeight - 40));
    int GeneratorRows => Math.Clamp((int)((GeneratorPanel.H - 290) / 34), 2, 12);
    UiRect GeneratorTabButton => new(GeneratorPanel.Right - 250, GeneratorPanel.Y + 16, 230, 36);
    UiRect DestinationTabButton => new(Layout.Developer.Right - 280, Layout.Developer.Y + 16, 256, 36);
    UiRect GeneratorRow(int visible) => new(GeneratorPanel.X + 20, GeneratorPanel.Y + 120 + visible * 34, GeneratorPanel.W - 40, 30);
    UiRect GeneratorAction(int i) => new(GeneratorPanel.X + 20 + i % 3 * (GeneratorPanel.W - 40) / 3,
        GeneratorPanel.Bottom - 158 + i / 3 * 40, (GeneratorPanel.W - 52) / 3, 34);
    UiRect GeneratorPauseButton => new(Layout.Pause.X + 436, Layout.Pause.Bottom - 76, 380, 34);
    void OpenGenerator()
    {
        planetPanel = true; generatorTab = true; generatorLoaded = false; generatorError = "";
        if (session?.ReturnAddress.Length == 6) planetSeed = string.Join('-', session.ReturnAddress);
        SetPause(true); session?.ReadPlanetDebug();
    }
    void LoadGenerator()
    {
        if (session?.DebugHome is not { } home) return;
        generatorDraft = PlanetDebugSettings.Read(home);
        generatorSeed = generatorDraft.Seed.ToString(CultureInfo.InvariantCulture);
        SetGeneratorHour(generatorDraft.Environment.Hour);
        generatorLoaded = true; generatorError = "";
    }
    void SetGeneratorHour(double hour)
    {
        int minutes = (int)Math.Round(hour * 60) % 1440;
        generatorTime = $"{minutes / 60:D2}:{minutes % 60:D2}";
        generatorDraft = generatorDraft with { Environment = generatorDraft.Environment with { Hour = minutes / 60d } };
    }
    bool ParseGenerator()
    {
        if (!int.TryParse(generatorSeed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
        { generatorError = "Seed must be a signed 32-bit integer."; return false; }
        var time = generatorTime.Split(':');
        if (time.Length != 2 || !int.TryParse(time[0], out int h) || !int.TryParse(time[1], out int m) || h is < 0 or > 23 || m is < 0 or > 59)
        { generatorError = "Time must be HH:MM (00:00 to 23:59)."; return false; }
        generatorDraft = generatorDraft with { Seed = seed, Environment = generatorDraft.Environment with { Hour = h + m / 60d } };
        generatorError = ""; return true;
    }
    void GeneratorText(string text)
    {
        if (generatorFocus is not (0 or 9) || session?.PlanetDebugState?.Busy == true) return;
        if (!text.All(c => char.IsAsciiDigit(c) || c == (generatorFocus == 0 ? '-' : ':'))) return;
        ref string value = ref (generatorFocus == 0 ? ref generatorSeed : ref generatorTime);
        if (generatorSelectAll) { value = ""; generatorSelectAll = false; }
        if (value.Length + text.Length <= (generatorFocus == 0 ? 11 : 5)) value += text;
    }
    static string Cycle(string[] values, string value, int delta) => values[(Array.IndexOf(values, value) + delta + values.Length) % values.Length];
    void AdjustGenerator(int row, int delta)
    {
        var d = generatorDraft; var g = d.Generation;
        float Change(float value, float step, float min, float max) => Math.Clamp(MathF.Round((value + step * delta) * 100) / 100, min, max);
        switch (row)
        {
            case 1: generatorSeed = Random.Shared.NextInt64(int.MinValue, (long)int.MaxValue + 1).ToString(CultureInfo.InvariantCulture); break;
            case 2: d = d with { Preset = Cycle(PlanetSettings.Presets, d.Preset, delta) }; break;
            case 3: d = d with { Settlements = d.Settlements with { Type = Cycle(SettlementSettings.Types, d.Settlements.Type, delta) } }; break;
            case 4: d = d with { Settlements = d.Settlements with { Style = Cycle(SettlementSettings.Styles, d.Settlements.Style, delta) } }; break;
            case 5: g = g with { Relief = Change(g.Relief, .05f, .25f, 2) }; break;
            case 6: g = g with { WaterOffset = Change(g.WaterOffset, .25f, -3, 3) }; break;
            case 7: g = g with { TreeDensity = Change(g.TreeDensity, .1f, 0, 2) }; break;
            case 8: g = g with { RockDensity = Change(g.RockDensity, .1f, 0, 2) }; break;
            case 9: SetGeneratorHour((d.Environment.Hour + delta * .25 + 24) % 24); return;
            case 10: d = d with { Environment = d.Environment with { Fixed = !d.Environment.Fixed } }; break;
            case 13: if(scene is not null) scene.SetLandscapeView((LandscapeView)(((int)scene.LandscapeView+delta+6)%6)); return;
            case 12: g = g with { LandscapeMode = (LandscapeMode)(((int)g.LandscapeMode+delta+3)%3) }; break;
            case 11: d = d with { Environment = d.Environment with { Weather = (PlanetWeather)(((int)d.Environment.Weather + delta + 5) % 5) } }; break;
        }
        generatorDraft = d with { Generation = g };
    }
    bool GeneratorAvailable => session is { Developer: true, PlayerCount: 1, DebugHome: { Fixture: null } } && session.PlanetDebugState?.Busy != true && !session.PlanetVisualPending;
    void RunGeneratorAction(int action)
    {
        if (session is null) return;
        if (action == 5) { session.CancelPlanetDebug(); return; }
        if (action == 2 && session.PlanetDebugState?.Busy != true) { LoadGenerator(); session.ReadPlanetDebug(); return; }
        if (action == 3 && session.PlanetDebugState?.Busy != true) { generatorDraft = generatorDraft with { Generation = new() }; return; }
        if (action == 4) { generatorScroll = generatorFocus = 0; return; }
        if (!GeneratorAvailable || !ParseGenerator()) return;
        session.ApplyPlanetDebug(generatorDraft, action == 1);
    }
    void GeneratorInput(InputManager input)
    {
        if (!generatorLoaded && session?.DebugHome is not null) LoadGenerator();
        if (input.WasKeyPressed(Key.Escape)) { planetPanel = false; SetPause(false); return; }
        if (input.WasKeyPressed(Key.Tab))
            generatorFocus = (generatorFocus + (input.IsKeyDown(Key.LeftShift) ? 19 : 1)) % 20;
        if (input.WasKeyPressed(Key.Up)) generatorFocus = Math.Max(0, generatorFocus - 1);
        if (input.WasKeyPressed(Key.Down)) generatorFocus = Math.Min(19, generatorFocus + 1);
        if (input.WasKeyPressed(Key.PageDown)) generatorScroll += GeneratorRows;
        if (input.WasKeyPressed(Key.PageUp)) generatorScroll -= GeneratorRows;
        if (input.WasKeyPressed(Key.Tab) || input.WasKeyPressed(Key.Up) || input.WasKeyPressed(Key.Down))
            if (generatorFocus < 14) generatorScroll = Math.Clamp(generatorScroll, Math.Max(0, generatorFocus - GeneratorRows + 1), generatorFocus);
        generatorScroll = Math.Clamp(generatorScroll, 0, 14 - GeneratorRows);
        bool busy = session?.PlanetDebugState?.Busy == true;
        if (!busy && generatorFocus < 14)
        {
            if (input.IsKeyDown(Key.LeftControl) && input.WasKeyPressed(Key.A)) generatorSelectAll = true;
            if (generatorFocus is 0 or 9 && input.WasKeyPressed(Key.Backspace))
            {
                ref string value = ref (generatorFocus == 0 ? ref generatorSeed : ref generatorTime);
                value = generatorSelectAll || value.Length == 0 ? "" : value[..^1]; generatorSelectAll = false;
            }
            if (input.WasKeyPressed(Key.Left)) AdjustGenerator(generatorFocus, -1);
            if (input.WasKeyPressed(Key.Right) || input.WasKeyPressed(Key.Space)) AdjustGenerator(generatorFocus, 1);
        }
        if (generatorFocus >= 14 && (input.WasKeyPressed(Key.Space) || input.WasKeyPressed(Key.Enter))) RunGeneratorAction(generatorFocus - 14);
        if (!input.WasMouseButtonPressed(MouseButton.Left)) return;
        if (GeneratorTabButton.Contains(UiMouse)) { generatorTab = false; return; }
        for (int i = 0; i < GeneratorRows; i++)
        {
            var row = GeneratorRow(i);
            if (!row.Contains(UiMouse)) continue;
            generatorFocus = i + generatorScroll; generatorSelectAll = true;
            if (!busy && generatorFocus is not (0 or 9)) AdjustGenerator(generatorFocus, UiMouse.X < row.Right - 40 && UiMouse.X > row.Right - 80 ? -1 : 1);
        }
        for (int i = 0; i < 6; i++) if (GeneratorAction(i).Contains(UiMouse)) { generatorFocus = i + 14; RunGeneratorAction(i); }
    }
    string[] GeneratorValues(PlanetDebugSettings d, bool draft) => [draft ? generatorSeed : d.Seed.ToString(), "New random seed", d.Preset,
        d.Settlements.Type, d.Settlements.Style, $"{d.Generation.Relief:F2}x", $"{d.Generation.WaterOffset:+0.00;-0.00;0.00} m",
        $"{d.Generation.TreeDensity:F2}x", $"{d.Generation.RockDensity:F2}x", draft ? generatorTime : $"{(int)d.Environment.Hour:D2}:{(int)(d.Environment.Hour * 60) % 60:D2}",
        d.Environment.Fixed ? "Fixed sun" : "Day cycle", d.Environment.Weather.ToString(), d.Generation.LandscapeMode.ToString(), scene?.LandscapeView.ToString() ?? "None"];
    void DrawGenerator(RenderPass pass)
    {
        if (!generatorLoaded && session?.DebugHome is not null) LoadGenerator();
        var r = GeneratorPanel;
        Rect(pass, 0, 0, UiWidth, UiHeight, new Color(4, 10, 16, 180)); Panel(pass, r, true);
        Text(pass, r.X + 20, r.Y + 22, "PLANET GENERATOR", 20, UiTheme.Amber);
        Button(pass, GeneratorTabButton, "Destinations / F2");
        Text(pass, r.X + 20, r.Y + 66, Fit("4096 x 4096 m / Target: " + (session?.DebugHome?.Name ?? "Reading starting planet..."), r.W - 40, 14), 14);
        Text(pass, r.X + 20, r.Y + 92, "SETTING", 13, UiTheme.Muted);
        Text(pass, r.X + r.W * .36f, r.Y + 92, "CURRENT", 13, UiTheme.Muted);
        Text(pass, r.X + r.W * .64f, r.Y + 92, "PENDING", 13, UiTheme.Cyan);
        string[] labels = ["Seed", "Randomize", "Biome", "Settlement", "Style", "Terrain relief", "Water offset", "Tree density", "Rock density", "Time HH:MM", "Clock", "Weather", "Landscape", "Diagnostic view"];
        var values = GeneratorValues(generatorDraft, true);
        var current = GeneratorValues(session?.DebugHome is { } home ? PlanetDebugSettings.Read(home) : generatorDraft, false);
        generatorScroll = Math.Clamp(generatorScroll, 0, 14 - GeneratorRows);
        for (int i = 0; i < GeneratorRows; i++)
        {
            int index = i + generatorScroll; var row = GeneratorRow(i);
            ui.Frame(pass, row, selected: generatorFocus == index);
            Text(pass, row.X + 8, row.Y + 7, labels[index], 14);
            Text(pass, r.X + r.W * .36f, row.Y + 7, current[index], 14, UiTheme.Muted);
            Text(pass, r.X + r.W * .64f, row.Y + 7, values[index] + (generatorFocus == index && index is 0 or 9 ? " |" : ""), 14, UiTheme.Cyan);
            if (index is not (0 or 9)) Text(pass, row.Right - 70, row.Y + 7, "<   >", 14, UiTheme.Amber);
        }
        Text(pass, r.X + 20, r.Bottom - 185, "Scroll / TAB Focus / Arrows Adjust / CTRL+A Replace / ESC Close", 13, UiTheme.Muted);
        string[] actions = ["Apply Environment", "Regenerate Starting Planet", "Reload Current Settings", "Reset Parameters", "Scroll to top", "Cancel Generation"];
        for (int i = 0; i < actions.Length; i++) Button(pass, GeneratorAction(i), actions[i], selected: generatorFocus == 14 + i,
            size: 14, enabled: i == 5 ? session?.PlanetDebugState?.CanCancel == true : i is 0 or 1 ? GeneratorAvailable : session?.PlanetDebugState?.Busy != true);
        Text(pass, r.X + 20, r.Bottom - 72, Fit("Regeneration clears this planet's terrain edits and placed objects.", r.W - 40, 14), 14, UiTheme.Amber);
        var state = session?.PlanetDebugState;
        string status = generatorError.Length > 0 ? generatorError : state is null ? "Reading saved settings..." : $"{state.Phase} / {state.ElapsedSeconds:F1}s / {state.Detail}";
        Text(pass, r.X + 20, r.Bottom - 43, Fit(status, r.W - 40, 14), 14, generatorError.Length > 0 || state?.Phase == "Failed" ? UiTheme.Error : UiTheme.Cyan);
    }
}
