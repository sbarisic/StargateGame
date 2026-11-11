using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class ClientSession
{
    public readonly Queue<(GateState Gate, bool Baseline)> AudioGates = [];
    public readonly Queue<int> AudioPresses = [];
    public readonly Queue<WorldSoundEvent> AudioSounds = [];
    public int AudioErrors;
    public long AudioDropped;
    readonly SoundEventHistory audioHistory = new();
    bool HandleAudio(Message message)
    {
        if (message.Kind != "world-sound")
            return false;
        if (message.LocationId != World.Definition.LocationId || message.Epoch != Physics.Epoch)
            return true;
        if (message.Sounds is not { Length: <= 32 } sounds)
            return true;
        foreach (var sound in sounds)
            if (audioHistory.Accept(message.LocationId, message.Epoch, sound))
            {
                if (AudioSounds.Count < 128)
                    AudioSounds.Enqueue(sound);
                else
                    AudioDropped++;
            }
        return true;
    }
    void ClearAudioEvents()
    {
        AudioGates.Clear();
        AudioPresses.Clear();
        AudioSounds.Clear();
        AudioErrors = 0;
    }
}
