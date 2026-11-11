using System.Numerics;
using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;

namespace StargateGame.Client;

readonly record struct HudPresentation(string World, int Explorers, float Heading, bool Equipped, bool Aiming, bool Visible)
{
    public static HudPresentation Read(ClientSession session, float yaw, bool menu, bool carrying)
        => new(session.CurrentWorldName.Length > 0 ? session.CurrentWorldName : session.World.Definition.Name,
            session.PlayerCount, UiLayout.Heading(yaw), session.LocalCharacter.Equipped && !carrying,
            session.LocalCharacter.Aiming && !carrying, !menu && !session.TransitLocked);
}

sealed partial class GameApp
{
    UiPainter ui = null!;
    float UiScale => UiLayout.ContentScale(window.Width, window.Height, window.ContentScale.X);
    float UiWidth => uiPreviewSize?.X ?? window.Width / UiScale;
    float UiHeight => uiPreviewSize?.Y ?? window.Height / UiScale;
    Vector2 UiPixelScale => uiPreviewSize is null ? window.FramebufferSize / new Vector2(UiWidth, UiHeight) : new(uiPreviewDpi);
    Vector2 UiMouse => window.MousePosition * new Vector2(UiWidth / window.Width, UiHeight / window.Height);
    UiLayout Layout => new(UiWidth, UiHeight);
    bool uiInputConsumed, uiMouseGuard;
    int pauseFocus;
    AmbientOcclusionSettings aoSettings = AmbientOcclusionSettings.Load();
    void CycleAmbientOcclusion()
    {
        var quality = scene?.AoSettings.Resolve(graphicsSettings.Quality) ?? aoSettings.Resolve(graphicsSettings.Quality);
        aoSettings = aoSettings with { Quality = quality switch {
            AmbientOcclusionQuality.Off => AmbientOcclusionQuality.Low,
            AmbientOcclusionQuality.Low => AmbientOcclusionQuality.Performance,
            AmbientOcclusionQuality.Performance => AmbientOcclusionQuality.High,
            _ => AmbientOcclusionQuality.Off } };
        if (scene is not null) scene.AoSettings = scene.AoSettings with { Quality = aoSettings.Quality };
        if (!UiPreview) aoSettings.Save();
    }
    string lastHudStatus = "";
    double hudStatusUntil;
    void PauseKeyboard(InputManager input)
    {
        if (!paused || bookOpen || planetPanel)
            return;
        if (exposureMenu) { ExposureKeyboard(input); return; }
        if (input.WasKeyPressed(Key.J)) { exposureMenu = true; return; }
        if (input.WasKeyPressed(Key.Tab))
            pauseFocus = (pauseFocus + (input.IsKeyDown(Key.LeftShift) ? (session?.Developer == true ? 10 : 9) : 1)) % (session?.Developer == true ? 11 : 10);
        if (input.WasKeyPressed(Key.O)) CycleAmbientOcclusion();
        if (input.WasKeyPressed(Key.H)) CycleGrassRenderer();
        if (!input.WasKeyPressed(Key.Space))
            return;
        if (pauseFocus == 10 && session?.Developer == true) OpenGenerator();
        else if (pauseFocus == 9) exposureMenu = true;
        else if (pauseFocus == 0)
            SetPause(false);
        else if (pauseFocus == 1)
            Disconnect("Disconnected. Ready to join again.");
        else if (pauseFocus == 2)
        {
            graphicsSettings = graphicsSettings.Next();
            scene?.SetGraphics(graphicsSettings);
        }
        else if (pauseFocus == 8) CycleAmbientOcclusion();
        else if (pauseFocus == 7 && audio is not null)
        {
            audio.Settings.Muted = !audio.Settings.Muted;
            audio.ApplySettings(!UiPreview);
        }
    }
    readonly Dictionary<UiRect, float> uiHover = [];
    Vector2 uiViewport;
    static readonly string[] CompassLabels = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
    static readonly Vector2[] ReticleDirections = [Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY];
    double uiLastTime;
    float uiDelta;
    Texture? rifleIcon;
    void DrawRifleIcon(RenderPass pass, UiRect r)
    {
        rifleIcon ??= window.Graphics.LoadTexture(Path.Combine(AppContext.BaseDirectory, "assets/gui/rifle-silhouette.png"));
        pass.DrawTexturedRectangle(r.X + 10, UiHeight - r.Y - 54, 96, 48, 0, 0, 1, 1, UiTheme.Amber, rifleIcon);
    }
    void UiBegin(double now)
    {
        if (uiViewport != new Vector2(UiWidth, UiHeight))
        {
            uiHover.Clear();
            uiViewport = new(UiWidth, UiHeight);
        }
        uiDelta = (float)Math.Clamp(now - uiLastTime, 0, .05);
        uiLastTime = now;
        ui.Viewport(UiHeight, UiPixelScale);
    }
    void Panel(RenderPass pass, UiRect rect, bool opaque = false) => ui.Frame(pass, rect, opaque: opaque);
    void Button(RenderPass pass, UiRect r, string text, bool selected = false, bool enabled = true, string? key = null, float size = 16)
    {
        // Geometry identifies controls; discard transitions when the viewport changes.
        bool hover = enabled && r.Contains(UiMouse);
        float old = uiHover.GetValueOrDefault(r), next = Math.Clamp(old + (hover ? 1 : -1) * uiDelta * 9, 0, 1);
        uiHover[r] = next;
        byte blend = (byte)(8 + next * 24);
        ui.Frame(pass, r, new Color(blend, (byte)(17 + next * 21), (byte)(25 + next * 15), 245), selected: selected || hover);
        Text(pass, r.X + 12, r.Y + (r.H - size) / 2, Fit(text, r.W - (key is null ? 24 : 86), size), size, enabled ? UiTheme.Text : UiTheme.Muted);
        if (selected)
            ui.Line(pass, r.X + 4, r.Y + 12, r.X + 4, r.Bottom - 12, UiTheme.Cyan, 3);
        if (key is not null)
            Keycap(pass, r.Right - 62, r.Y + (r.H - 24) / 2, key);
        if (!enabled)
            ui.Line(pass, r.X + 12, r.Bottom - 6, r.X + 36, r.Bottom - 6, UiTheme.Muted);
    }
    string Fit(string text, float width, float size)
    {
        if (font.Measure(text, size).X <= width)
            return text;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (font.Measure(text[..mid] + "...", size).X <= width)
                low = mid;
            else
                high = mid - 1;
        }
        return text[..low] + "...";
    }
    void Wrapped(RenderPass pass, UiRect r, string text, float size = 14, Color? color = null)
    {
        float y = r.Y;
        string rest = text.Replace('\n', ' ');
        while (rest.Length > 0 && y + size <= r.Bottom)
        {
            string line = Fit(rest, r.W, size);
            int used = line.EndsWith("...") && line != rest ? line.Length - 3 : line.Length;
            if (used == 0)
                break;
            if (used < rest.Length)
            {
                int space = rest.LastIndexOf(' ', Math.Max(0, used - 1), used);
                if (space > used / 2)
                    used = space;
            }
            bool last = y + size * 2 + 5 > r.Bottom;
            Text(pass, r.X, y, last ? Fit(rest, r.W, size) : rest[..used], size, color ?? UiTheme.Muted);
            rest = rest[used..].TrimStart();
            y += size + 5;
        }
    }
    void Keycap(RenderPass pass, float x, float y, string key)
    {
        float w = Math.Max(28, font.Measure(key, 14).X + 12);
        ui.Frame(pass, new(x, y, w, 24), new Color(20, 35, 44, 245));
        Text(pass, x + 6, y + 4, key, 14, UiTheme.Amber);
    }
    void DrawExpeditionHud(RenderPass pass)
    {
        if (session is null && !UiPreview)
            return;
        var l = Layout;
        var h = session is null ? new HudPresentation("Preview world / a long expedition world name", 1, 359, true, false, !paused && !bookOpen && !buildPaletteOpen) : HudPresentation.Read(session, yaw, paused || bookOpen || buildPaletteOpen || planetPanel, session.Carrying || dhdHover >= 0 || buildMode);
        Panel(pass, l.Identity);
        Text(pass, l.Identity.X + 14, l.Identity.Y + 10, "STARGATE / FIRST LANDING", 18, UiTheme.Amber);
        Text(pass, l.Identity.X + 14, l.Identity.Y + 36, Fit(h.World, 272, 14), 14, UiTheme.Text);
        Panel(pass, l.Explorers);
        Text(pass, l.Explorers.X + 14, l.Explorers.Y + 10, $"{h.Explorers} EXPLORER{(h.Explorers == 1 ? "" : "S")}", 14, UiTheme.Muted);
        var compass = l.Compass;
        float center = UiWidth / 2f;
        ui.Frame(pass, compass with
        {
            H = 44
        }, UiTheme.Panel);
        ui.Line(pass, compass.X, compass.Y + 32, compass.Right, compass.Y + 32, new Color(160, 173, 184, 130));
        for (int bearing = 0; bearing < 360; bearing += 5)
        {
            float delta = UiLayout.BearingDelta(bearing, h.Heading);
            float x = center + delta * compass.W / 100;
            if (x < compass.X + 12 || x > compass.Right - 12)
                continue;
            ui.Line(pass, x, compass.Y + 32, x, compass.Y + (bearing % 45 == 0 ? 23 : 28), UiTheme.Muted);
            if (bearing % 45 == 0)
            {
                string label = CompassLabels[bearing / 45];
                Text(pass, x - font.Measure(label, 14).X / 2, compass.Y + 2, label, 14, bearing % 90 == 0 ? UiTheme.Text : UiTheme.Muted);
            }
        }
        ui.Line(pass, center - 4, compass.Y + 39, center, compass.Y + 33, UiTheme.Amber, 2);
        ui.Line(pass, center, compass.Y + 33, center + 4, compass.Y + 39, UiTheme.Amber, 2);
        if (!h.Visible)
            return;
        if (!h.Aiming)
        {
            float cx = UiWidth / 2f, cy = UiHeight / 2f;
            foreach (var d in ReticleDirections)
            {
                ui.Line(pass, cx + d.X * 9, cy + d.Y * 9, cx + d.X * 18, cy + d.Y * 18, new Color(0, 0, 0, 160), 3);
                ui.Line(pass, cx + d.X * 9, cy + d.Y * 9, cx + d.X * 18, cy + d.Y * 18, UiTheme.Text);
            }
        }
        if (session is null)
        {
            Panel(pass, l.Equipment);
            DrawRifleIcon(pass, l.Equipment);
            Text(pass, l.Equipment.X + 112, l.Equipment.Y + 12, "RIFLE", 16, UiTheme.Amber);
            Text(pass, l.Equipment.X + 112, l.Equipment.Y + 36, "PREVIEW", 14);
            return;
        }
        if (pickupHint.Length > 0)
        {
            float x = UiWidth / 2f - 120, y = UiHeight / 2f + 36;
            Panel(pass, new(x - 10, y - 5, 260, 36));
            Keycap(pass, x, y, "E");
            Text(pass, x + 40, y + 4, Fit(pickupHint.StartsWith("E ") ? pickupHint[2..] : pickupHint, 200, 14), 14);
        }
        if (session.TerrainStatus != lastHudStatus)
        {
            lastHudStatus = session.TerrainStatus;
            hudStatusUntil = timer.Elapsed.TotalSeconds + 7;
        }
        if (buildMode)
        {
            Panel(pass, l.Notice);
            Text(pass, 34, l.Notice.Y + 8, Fit("BUILD / " + SelectedBuild, l.Notice.W - 28, 16), 16, UiTheme.Amber);
            Text(pass, 34, l.Notice.Y + 31, Fit(buildHint, l.Notice.W - 28, 14), 14, previewEdit?.Valid == true ? UiTheme.Cyan : UiTheme.Error);
            Text(pass, 34, l.Notice.Y + 53, Fit(session.BuildStatus.Length > 0 ? session.BuildStatus : "B Picker / Wheel Select / R Rotate / ESC Stop", l.Notice.W - 28, 14), 14);
        }
        else if (session.ShowItemNotice || session.TerrainStatus.Length > 0 && timer.Elapsed.TotalSeconds < hudStatusUntil)
        {
            Panel(pass, l.Notice);
            Wrapped(pass, l.Notice.Inset(14), session.ShowItemNotice ? session.ItemNotice : session.TerrainStatus);
        }
        if (h.Equipped && !buildMode)
        {
            Panel(pass, l.Equipment);
            DrawRifleIcon(pass, l.Equipment);
            Text(pass, l.Equipment.X + 112, l.Equipment.Y + 12, "RIFLE", 16, UiTheme.Amber);
            Text(pass, l.Equipment.X + 112, l.Equipment.Y + 36, h.Aiming ? "AIMING" : "EQUIPPED", 14);
        }
    }
    UiRect GrassSettingButton => new(Layout.Pause.X + 436, Layout.Pause.Y + 510, 400, 36);
    void CycleGrassRenderer() { if (scene is not null) scene.GroundCoverSettings = scene.GroundCoverSettings with { Hybrid = !scene.GroundCoverSettings.Hybrid }; }
    void DrawPauseConsole(RenderPass pass)
    {
        if (exposureMenu) { DrawExposureSettings(pass); return; }
        Rect(pass, 0, 0, UiWidth, UiHeight, new Color(4, 10, 16, 170));
        var l = Layout;
        var r = l.Pause;
        Panel(pass, r, true);
        Text(pass, r.X + 24, r.Y + 23, "EXPEDITION / PAUSED", 22, UiTheme.Amber);
        Button(pass, l.PauseAction(0), "Resume", selected: pauseFocus == 0, key: "ENTER");
        Button(pass, l.PauseAction(1), "Disconnect", selected: pauseFocus == 1, key: "Q");
        Button(pass, l.PauseAction(2), "Graphics / " + graphicsSettings.Quality, selected: pauseFocus == 2, key: "G");
        Button(pass, l.PauseAction(3), "Ambient occlusion / " + (scene?.AoSettings ?? aoSettings).Resolve(graphicsSettings.Quality), selected: pauseFocus == 8, key: "O");
        Text(pass, r.X + 24, r.Y + 292, "AUDIO", 14, UiTheme.Amber);
        DrawAudioSettings(pass);
        if (session?.Developer == true) Button(pass, GeneratorPauseButton, "Planet Generator / F4", selected: pauseFocus == 10);
        float x = r.X + 436, y = r.Y + 80;
        Text(pass, x, y, "CONTROLS", 16, UiTheme.Amber);
        string[] controls = ["WASD  Move / swim", "SHIFT Walk / swim slowly", "SPACE Jump / rise", "CTRL  Crouch / dive", "E     Use / pick up / drop", "I Inventory / 1-4 Slots / 0 Empty", "RMB   Aim    LMB Fire / throw", "R     Reload / rotate build", "B     Hold building picker", "TAB   Stargate addresses", "F3    Stats  SHIFT+F3 Collision", "F4    Developer planets", "ESC   Close menu / stop build"];
        for (int i = 0; i < controls.Length; i++)
            Text(pass, x, y + 36 + i * 27, controls[i], 14, UiTheme.Muted);
        Button(pass, GrassSettingButton, "Grass / " + (scene?.GroundCoverSettings.Hybrid == false ? "Mesh" : "Hybrid"), key: "H");
        Button(pass, ExposureSettingsButton, "Filmic / exposure", selected: pauseFocus == 9, key: "J");
        Text(pass, r.X + 24, r.Bottom - 36, "TAB Focus / SPACE Activate / Arrows Adjust", 14, UiTheme.Muted);
    }
    bool PauseClick(InputManager input)
    {
        if (!paused || planetPanel || bookOpen || !input.WasMouseButtonPressed(MouseButton.Left))
            return false;
        var l = Layout;
        var p = UiMouse;
        if (exposureMenu) return ExposureClick(p);
        if (session?.TransitLocked == true)
        {
            if (l.TravelResume.Contains(p))
            {
                SetPause(false);
                return true;
            }
            if (l.TravelDisconnect.Contains(p))
            {
                Disconnect("Disconnected. Ready to join again.");
                return true;
            }
            return false;
        }
        if (session?.Developer == true && GeneratorPauseButton.Contains(p)) { OpenGenerator(); return true; }
        if (l.PauseAction(0).Contains(p))
        {
            SetPause(false);
            return true;
        }
        if (l.PauseAction(1).Contains(p))
        {
            Disconnect("Disconnected. Ready to join again.");
            return true;
        }
        if (GrassSettingButton.Contains(p)) { CycleGrassRenderer(); return true; }
        if (ExposureSettingsButton.Contains(p)) { exposureMenu = true; return true; }
        if (l.PauseAction(3).Contains(p)) { CycleAmbientOcclusion(); return true; }
        if (l.PauseAction(2).Contains(p))
        {
            graphicsSettings = graphicsSettings.Next();
            scene?.SetGraphics(graphicsSettings);
            return true;
        }
        return false;
    }
    void DrawConnectionConsole(RenderPass pass)
    {
        var l = Layout;
        var r = l.Connection;
        Panel(pass, r, true);
        Text(pass, r.X + 24, r.Y + 26, "STARGATE / FIRST LANDING", 22, UiTheme.Amber);
        Text(pass, r.X + 24, r.Y + 62, "EXPEDITION CONNECTION TERMINAL", 14, UiTheme.Muted);
        string[] names = ["SERVER ADDRESS", "PORT", "EXPLORER NAME"];
        for (int i = 0; i < 3; i++)
        {
            var f = l.ConnectField(i);
            ui.Frame(pass, f, selected: i == field && !Joining);
            Text(pass, f.X + 12, f.Y + 7, names[i], 14, UiTheme.Muted);
            Text(pass, f.X + 12, f.Y + 30, Fit(fields[i] + (i == field && !Joining ? " |" : ""), f.W - 24, 16), 16);
        }
        Button(pass, l.Join, !Joining ? "Join expedition" : "Cancel connection", key: !Joining ? "ENTER" : "ESC");
        bool error = !Joining && startupFailure is not null || !Joining && !status.StartsWith("Enter a server") && !status.StartsWith("Disconnected") && !status.StartsWith("Cancelled") && !status.StartsWith("Connection cancelled");
        Wrapped(pass, new(r.X + 24, r.Y + 394, r.W - 48, 82), (error ? "! " : "") + status, 14, error ? UiTheme.Error : UiTheme.Muted);
        if (startupFailure is null && session is null && !error)
            Text(pass, r.X + 24, r.Y + 426, Fit(StartupStatus, r.W - 48, 14), 14, UiTheme.Cyan);
        Text(pass, r.X + 24, r.Bottom - 44, "TAB Next field / CTRL+A Replace / ESC Cancel", 14, UiTheme.Muted);
    }
    void DrawDeveloperConsole(RenderPass pass)
    {
        if (session is null && !UiPreview)
            return;
        if (generatorTab) { DrawGenerator(pass); return; }
        var l = Layout;
        var r = l.Developer;
        Rect(pass, 0, 0, UiWidth, UiHeight, new Color(4, 10, 16, 180));
        Panel(pass, r, true);
        Text(pass, r.X + 24, r.Y + 24, "DESTINATIONS", 18, UiTheme.Amber);
        Button(pass, DestinationTabButton, "Planet Generator / F2");
        var route = session?.Destinations.FirstOrDefault(d => string.Join('-', d.Symbols) == planetSeed);
        Button(pass, l.DeveloperAction(0), route?.Name ?? "Home / custom address", key: "TAB");
        Button(pass, l.DeveloperAction(1), "Address: " + planetSeed + " |", selected: true);
        Button(pass, l.DeveloperAction(2), "Inspect location (only you)", key: "ENTER");
        Button(pass, l.DeveloperAction(3), "Cancel loading", key: "C");
        Wrapped(pass, new(r.X + 24, r.Y + 320, 552, 54), session?.TerrainStatus ?? "Preview only / no active session");
        Text(pass, r.X + 24, r.Bottom - 38, "CTRL+A Replace / TAB Named route / ESC Close", 14, UiTheme.Muted);
    }
}
