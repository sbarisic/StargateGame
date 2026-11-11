using System.Numerics;
using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Building;

namespace StargateGame.Client;

sealed partial class GameApp
{
    bool buildPaletteOpen, paletteMouseGuard, paletteInspectionHolding;
    int palettePage;
    readonly Dictionary<string, Texture> buildThumbnails = [];
    readonly HashSet<string> failedBuildThumbnails = [];
    int paletteInspectionStage;
    readonly HashSet<string> paletteCaptures = [];
    int BuildItemCount => BuildingPrefabs.Palette.Count + 2;
    int BuildPaletteItemCount => BuildItemCount + 1;
    string BuildName(int index) => index < BuildingPrefabs.Palette.Count ? BuildingPrefabs.Palette[index].Title : index == BuildingPrefabs.Palette.Count ? "Demolish" : "Clear scenery";
    void SetBuildPalette(bool open)
    {
        if (args.Contains("--building-picker-check") && buildPaletteOpen != open)
            Console.WriteLine($"BUILD PICKER {(open ? "open" : "closed")} selected={SelectedBuild}");
        uiInputConsumed = true;
        uiMouseGuard = true;
        buildPaletteOpen = open;
        window.CaptureCursor = !open && !paused && !Smoke && !UiPreview;
        mouseDelta = Vector2.Zero;
        jumpQueued = false;
        CancelDemolition();
        scene?.Demolition(null, [], false);
        paletteMouseGuard = true;
        scene?.Preview(null, false);
        nextBuildPreview = 0;
    }
    int paletteLastSize;
    void ReflowPalette()
    {
        buildSelection = Math.Clamp(buildSelection, 0, BuildItemCount - 1);
        int size = Layout.PageSize;
        if (paletteLastSize != size)
        {
            palettePage = (paletteLastSize == 0 ? 0 : palettePage * paletteLastSize) / size;
            paletteLastSize = size;
        }
        palettePage = Math.Clamp(palettePage, 0, (BuildPaletteItemCount - 1) / size);
    }
    bool PaletteInput(InputManager input)
    {
        ReflowPalette();
        if (buildPaletteOpen && (!input.IsKeyDown(Key.B) && !paletteInspectionHolding || paused))
        {
            SetBuildPalette(false);
            return true;
        }
        if (!paused && !planetPanel && input.WasKeyPressed(Key.B))
        {
            buildMode = true;
            palettePage = 0;
            SetBuildPalette(true);
        }
        if (!buildPaletteOpen)
            return false;
        if (input.WasKeyPressed(Key.Escape))
        {
            buildMode = false;
            SetBuildPalette(false);
            return true;
        }
        var l = Layout;
        var p = UiMouse;
        if (input.WasKeyPressed(Key.PageDown) || input.WasKeyPressed(Key.Right))
            palettePage = Math.Min((BuildPaletteItemCount - 1) / l.PageSize, palettePage + 1);
        if (input.WasKeyPressed(Key.PageUp) || input.WasKeyPressed(Key.Left))
            palettePage = Math.Max(0, palettePage - 1);
        if (input.WasMouseButtonPressed(MouseButton.Left))
        {
            for (int slot = 0; slot < l.PageSize; slot++)
                if (l.Tile(slot).Contains(p))
                {
                    int index = palettePage * l.PageSize + slot;
                    if (index == 0)
                    {
                        buildMode = false;
                        SetBuildPalette(false);
                        return true;
                    }
                    index--; // The exit action is not a selectable building tool.
                    if (index < BuildItemCount)
                    {
                        buildSelection = index;
                        buildRotation = 0;
                        nextBuildPreview = 0;
                    }
                }
            if (l.PickerPage(false).Contains(p))
                palettePage = Math.Max(0, palettePage - 1);
            if (l.PickerPage(true).Contains(p))
                palettePage = Math.Min((BuildPaletteItemCount - 1) / l.PageSize, palettePage + 1);
        }
        return true;
    }
    void DrawBuildingPalette(RenderPass pass)
    {
        if (!buildPaletteOpen)
            return;
        ReflowPalette();
        var l = Layout;
        var r = l.Picker;
        Rect(pass, 0, 0, UiWidth, UiHeight, new Color(4, 10, 16, 170));
        Panel(pass, r, true);
        Text(pass, r.X + 20, r.Y + 20, "CONSTRUCTION / BUILDING KIT", 22, UiTheme.Amber);
        Text(pass, r.X + 20, r.Y + 56, "Hold B to browse / Click a piece / Release B to place", 16, UiTheme.Muted);
        for (int slot = 0; slot < l.PageSize; slot++)
        {
            int index = palettePage * l.PageSize + slot - 1;
            if (index >= BuildItemCount)
                break;
            var tile = l.Tile(slot);
            if (index == -1)
            {
                ui.Frame(pass, tile, tile.Contains(UiMouse) ? UiTheme.Hover : UiTheme.Panel);
                Text(pass, tile.X + 24, tile.Y + 36, "[ EXIT ]", 24, UiTheme.Amber);
                Text(pass, tile.X + 8, tile.Y + 110, Fit("Exit build mode", tile.W - 16, 14), 14, UiTheme.Text);
                continue;
            }
            bool selected = index == buildSelection;
            ui.Frame(pass, tile, tile.Contains(UiMouse) ? UiTheme.Hover : UiTheme.Panel, selected: selected);
            if (index < BuildingPrefabs.Palette.Count)
            {
                var prefab = BuildingPrefabs.Palette[index];
                if (!buildThumbnails.TryGetValue(prefab.Id, out var tex) && !failedBuildThumbnails.Contains(prefab.Id))
                {
                    string path = Path.Combine(AppContext.BaseDirectory, "assets/gui/building", prefab.Id + ".png");
                    try
                    {
                        tex = window.Graphics.LoadTexture(path, new TextureLoadOptions { MipLevels = 4, Sampling = new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear) });
                        buildThumbnails.Add(prefab.Id, tex);
                    }
                    // GDI+ also reports malformed/unsupported image data as OutOfMemoryException.
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
                    {
                        failedBuildThumbnails.Add(prefab.Id);
                        Console.Error.WriteLine($"Building thumbnail unavailable: {path}: {ex.Message}");
                    }
                }
                if (tex is not null)
                    pass.DrawTexturedRectangle(tile.X + 6, UiHeight - tile.Y - 107, tile.W - 12, 101, 0, 0, 1, 1, Color.White, tex);
                else
                    Text(pass, tile.X + 12, tile.Y + 48, "[ preview unavailable ]", 14, UiTheme.Muted);
                Text(pass, tile.X + 8, tile.Y + 132, Fit(prefab.Group, tile.W - 16, 14), 14, UiTheme.Muted);
            }
            else
                Text(pass, tile.X + 46, tile.Y + 36, index == BuildingPrefabs.Palette.Count ? "[ X ]" : "[ / ]", 30, UiTheme.Amber);
            Text(pass, tile.X + 8, tile.Y + 110, Fit((selected ? "> " : "") + BuildName(index), tile.W - 16, 14), 14, selected ? UiTheme.Amber : UiTheme.Text);
        }
        Button(pass, l.PickerPage(false), "< Previous", enabled: palettePage > 0);
        Text(pass, r.X + r.W / 2 - 80, r.Bottom - 44, $"PAGE {palettePage + 1} / {(BuildPaletteItemCount + l.PageSize - 1) / l.PageSize}", 16);
        Button(pass, l.PickerPage(true), "Next >", enabled: (palettePage + 1) * l.PageSize < BuildPaletteItemCount);
    }
    void DisposeBuildThumbnails()
    {
        foreach (var t in buildThumbnails.Values)
            t.Dispose();
        buildThumbnails.Clear();
    }
    void InspectBuildingPicker(double age)
    {
        if (!Smoke || !args.Contains("--building-picker-check"))
            return;
        paletteInspectionHolding = age is >= 4 and < 18;
        if (paletteInspectionHolding)
        {
            buildMode = true;
            if (!buildPaletteOpen)
                SetBuildPalette(true);
        }
        if (age >= 9 && paletteInspectionStage == 0)
        {
            palettePage = 1;
            paletteInspectionStage++;
        }
        if (age >= 14 && paletteInspectionStage == 1)
        {
            window.SetClientSize(960, 600);
            paletteInspectionStage++;
        }
        if (age >= 18 && paletteInspectionStage == 2)
        {
            SetBuildPalette(false);
            window.SetClientSize(1920, 1080);
            paletteInspectionStage++;
        }
    }
    void CaptureBuildingPicker(RenderPass pass, double age)
    {
        if (!Smoke || !args.Contains("--building-picker-check"))
            return;
        string? name = age >= 20 ? "picker-released" : age >= 16 ? "picker-small" : age >= 11 ? "picker-fortification" : age >= 6 ? "picker-village" : null;
        if (name is not null && paletteCaptures.Add(name))
            Capture(pass, Path.Combine(captureDirectory, name + ".png"));
    }
}
