using System.Text.Json;
namespace StargateGame.Audio;

public sealed class AudioSettings
{
    public float Master
    {
        get;
        set;
    } = .8f;
    public float Effects
    {
        get;
        set;
    } = 1;
    public float Ambience
    {
        get;
        set;
    } = .7f;
    public float Ui
    {
        get;
        set;
    } = .7f;
    public bool Muted
    {
        get;
        set;
    }
    public static string FilePath => Path.Combine(Environment.GetEnvironmentVariable("STARGATEGAME_USER_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StargateGame"), "audio.json");
    public static AudioSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AudioSettings>(File.ReadAllText(FilePath)) ?? new();
            settings.Clamp();
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }
    public void Clamp()
    {
        Master = Gain(Master);
        Effects = Gain(Effects);
        Ambience = Gain(Ambience);
        Ui = Gain(Ui);
    }
    static float Gain(float v) => float.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
    public void Apply(IAudioSystem audio)
    {
        Clamp();
        audio.SetBusGain(AudioBus.Master, Muted ? 0 : Master);
        audio.SetBusGain(AudioBus.Sfx, Effects);
        audio.SetBusGain(AudioBus.Ambience, Ambience);
        audio.SetBusGain(AudioBus.Ui, Ui);
    }
    public void Save()
    {
        Clamp();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, true);
    }
}
