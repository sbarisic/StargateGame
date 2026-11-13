using System.Numerics;
using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class GameApp
{
    ToneMappingSettings toneMappingSettings = ToneMappingSettings.Load();
    bool exposureMenu;
    UiRect ExposureSettingsButton => Layout.PauseSecondaryAction(0);
    UiRect ExposureAction(int row) => new(Layout.Pause.X + 32, Layout.Pause.Y + 118 + row * 62, 520, 44);
    UiRect ExposureStep(bool increase) => new(Layout.Pause.X + (increase ? 464 : 384), Layout.Pause.Y + 180, 72, 44);

    void SetExposureSettings(ToneMappingSettings settings)
    {
        toneMappingSettings = settings;
        if (scene is not null) scene.ToneMapping = settings;
        if (!UiPreview) settings.Save();
    }
    void ToggleExposure() => SetExposureSettings(toneMappingSettings with { Mode = toneMappingSettings.Mode == ExposureMode.Auto ? ExposureMode.Manual : ExposureMode.Auto });
    void StepExposure(int direction) => SetExposureSettings(toneMappingSettings with { Compensation = MathF.Round(Math.Clamp(toneMappingSettings.Compensation + direction * .1f, -2, 2) * 10) / 10 });
    void ExposureKeyboard(InputManager input)
    {
        if (input.WasKeyPressed(Key.J)) { exposureMenu = false; return; }
        if (input.WasKeyPressed(Key.Space)) ToggleExposure();
        if (input.WasKeyPressed(Key.Left)) StepExposure(-1);
        if (input.WasKeyPressed(Key.Right)) StepExposure(1);
        if (input.WasKeyPressed(Key.Backspace)) SetExposureSettings(new());
    }
    bool ExposureClick(Vector2 p)
    {
        if (ExposureAction(0).Contains(p)) ToggleExposure();
        else if (ExposureStep(false).Contains(p)) StepExposure(-1);
        else if (ExposureStep(true).Contains(p)) StepExposure(1);
        else if (ExposureAction(2).Contains(p)) SetExposureSettings(new());
        else if (ExposureAction(3).Contains(p)) exposureMenu = false;
        return true;
    }
    void DrawExposureSettings(RenderPass pass)
    {
        Rect(pass, 0, 0, UiWidth, UiHeight, new Color(4, 10, 16, 170));
        var r = Layout.Pause;
        Panel(pass, r, true);
        Text(pass, r.X + 32, r.Y + 28, "FILMIC / EXPOSURE", 22, UiTheme.Amber);
        Text(pass, r.X + 32, r.Y + 76, "AgX / Natural highlights and restrained adaptation", 16, UiTheme.Muted);
        Button(pass, ExposureAction(0), "Exposure / " + toneMappingSettings.Mode, key: "SPACE");
        var ev = ExposureAction(1);
        Text(pass, ev.X, ev.Y + 12, $"{(toneMappingSettings.Mode == ExposureMode.Auto ? "Compensation" : "Exposure")} / {toneMappingSettings.Compensation:+0.0;-0.0;0.0} stops", 16);
        Button(pass, ExposureStep(false), "-", key: "LEFT");
        Button(pass, ExposureStep(true), "+", key: "RIGHT");
        Button(pass, ExposureAction(2), "Reset to automatic / 0.0 stops", key: "BACK");
        Button(pass, ExposureAction(3), "Back to pause menu", key: "J");
        Text(pass, r.X + 32, r.Y + 408, "Automatic adjusts gradually. Nights remain dark.", 16, UiTheme.Muted);
        Text(pass, r.X + 32, r.Y + 438, "Positive values brighten the scene; UI brightness is unchanged.", 16, UiTheme.Muted);
        if (scene is not null)
        {
            string current = scene.ExposureEv?.ToString("+0.00;-0.00;0.00") ?? "pending";
            string target = scene.ExposureTargetEv?.ToString("+0.00;-0.00;0.00") ?? "pending";
            Text(pass, r.X + 32, r.Y + 488, $"Current {current} / target {target} stops{(scene.ExposureHeld ? " / held" : "")}", 14, UiTheme.Muted);
            Text(pass, r.X + 32, r.Y + 514, $"GPU meter {scene.ExposureGpuMilliseconds:F3} ms / filmic {scene.ToneMapGpuMilliseconds:F3} ms", 14, UiTheme.Muted);
        }
    }
}
