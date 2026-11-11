using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;
namespace StargateGame.Client;

sealed partial class GameApp
{
    WorldAudioController? audio;
    readonly List<double> audioTimes = [];
    bool audioSettingsDirty, observerMoved;
    int audioSlider = -1;
    void SmokeAudio()
    {
        if (!args.Contains("--audio-observer") || observerMoved || session?.Gate is null)
            return;
        observerMoved = true;
        session.InspectDestination(new[] { 28, 26, 5, 36, 11, 29 });
    }
    void AudioInput(InputManager input)
    {
        if (!paused || planetPanel || bookOpen || exposureMenu || audio is null)
            return;
        if (input.WasKeyPressed(Key.M))
        {
            audio.Settings.Muted = !audio.Settings.Muted;
            audio.ApplySettings(!UiPreview);
        }
        var point = UiMouse;
        var layout = Layout;
        if (input.WasMouseButtonPressed(MouseButton.Left) && layout.Mute.Contains(point))
        {
            audio.Settings.Muted = !audio.Settings.Muted;
            audio.ApplySettings(!UiPreview);
        }
        if (input.WasMouseButtonPressed(MouseButton.Left))
        {
            audioSlider = -1;
            for (int i = 0; i < 4; i++)
                if (layout.Slider(i).Contains(point))
                {
                    audioSlider = i;
                    pauseFocus = i + 3;
                }
        }
        if (audioSlider >= 0 && input.IsMouseButtonDown(MouseButton.Left))
            SetAudioValue(audioSlider, Math.Clamp((point.X - layout.Slider(audioSlider).X) / layout.Slider(audioSlider).W, 0, 1));
        if (pauseFocus is >= 3 and <= 6 && (input.WasKeyPressed(Key.Left) || input.WasKeyPressed(Key.Right)))
        {
            int index = pauseFocus - 3;
            float[] values = [audio.Settings.Master, audio.Settings.Effects, audio.Settings.Ambience, audio.Settings.Ui];
            SetAudioValue(index, Math.Clamp(values[index] + (input.WasKeyPressed(Key.Left) ? -.05f : .05f), 0, 1));
        }
        if (audioSettingsDirty && !input.IsMouseButtonDown(MouseButton.Left))
        {
            audio.ApplySettings(!UiPreview);
            audioSettingsDirty = false;
            audioSlider = -1;
        }
    }
    void SetAudioValue(int index, float value)
    {
        if (audio is null)
            return;
        switch (index)
        {
            case 0:
                audio.Settings.Master = value;
                break;
            case 1:
                audio.Settings.Effects = value;
                break;
            case 2:
                audio.Settings.Ambience = value;
                break;
            case 3:
                audio.Settings.Ui = value;
                break;
        }
        audio.ApplySettings(false);
        audioSettingsDirty = true;
    }
    void DrawAudioSettings(RenderPass pass)
    {
        var l = Layout;
        if (audio is null)
        {
            Text(pass, l.Pause.X + 24, l.Pause.Y + 330, "Audio disabled for this session", 14, UiTheme.Muted);
            return;
        }
        string[] names = ["Master", "Effects", "Ambience", "UI"];
        float[] values = [audio.Settings.Master, audio.Settings.Effects, audio.Settings.Ambience, audio.Settings.Ui];
        for (int i = 0; i < 4; i++)
        {
            var r = l.Slider(i);
            if (pauseFocus == i + 3)
                ui.Frame(pass, r.Inset(-3), selected: true);
            Text(pass, l.Pause.X + 24, r.Y + 5, names[i], 16);
            Rect(pass, r.X, r.Y + 10, r.W, 6, new Color(42, 56, 66));
            Rect(pass, r.X, r.Y + 10, r.W * values[i], 6, UiTheme.Amber);
            Rect(pass, r.X + r.W * values[i] - 3, r.Y + 4, 6, 18, UiTheme.Cyan);
            Text(pass, r.Right + 12, r.Y + 5, $"{values[i]:P0}", 14);
        }
        Button(pass, l.Mute, audio.Settings.Muted ? "Unmute audio" : "Mute audio", selected: audio.Settings.Muted || pauseFocus == 7, key: "M");
    }
}
