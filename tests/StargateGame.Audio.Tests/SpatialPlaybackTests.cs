using System.Numerics;
namespace StargateGame.Audio.Tests;

public sealed class SpatialPlaybackTests
{
    [Fact]
    public void NativeStereoMovingSourceAndObstructionFilter()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        using (var w = new BinaryWriter(File.Create(file)))
        {
            w.Write("RIFF"u8);
            w.Write(36 + 96000);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(48000);
            w.Write(96000);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(96000);
            for (int i = 0; i < 48000; i++)
                w.Write((short)(12000 * Math.Sin(i * Math.Tau * 6000 / 48000)));
        }
        try
        {
            using var audio = new AudioSystem(new()
            {
                NoDevice = true,
                SampleRate = 48000,
                Channels = 2,
                ThrowOnInitializationFailure = true
            });
            audio.RegisterCue(new()
            {
                CueId = "test",
                Variants = [new() { Path = file }],
                Looping = true,
                DopplerFactor = 0
            });
            audio.SetListener(AudioListener.Default);
            audio.Capture(true);
            var voice = audio.PlayCue("test", new(new(2, 0, -2), Vector3.Zero));
            double[] Energy()
            {
                audio.Update(.2f);
                float[] samples = new float[48000];
                int n = audio.ReadCapture(samples);
                Assert.True(n > 0);
                double l = 0, r = 0;
                for (int i = 0; i < n; i++)
                {
                    l += samples[i * 2] * samples[i * 2];
                    r += samples[i * 2 + 1] * samples[i * 2 + 1];
                }
                return [l / n, r / n];
            }
            var right = Energy();
            Assert.True(right[1] > right[0] * 1.1, $"Right source L={right[0]} R={right[1]}");
            audio.SetEmitter(voice, new(new(-2, 0, -2), Vector3.Zero));
            var left = Energy();
            Assert.True(left[0] > left[1] * 1.1);
            audio.SetCutoff(voice, 1500);
            Energy();
            var muffled = Energy();
            Assert.True(muffled.Sum() < left.Sum() * .35);
            audio.Seek(voice, .7f);
            Assert.True(audio.IsActive(voice));
            audio.Stop(voice);
            var replacement = audio.PlayCue("test", new(Vector3.Zero, Vector3.Zero));
            audio.SetEmitter(voice, new(new(1000), Vector3.Zero));
            audio.SetCutoff(voice, 20);
            audio.Stop(voice);
            Assert.True(audio.IsActive(replacement));
            Assert.NotEqual(voice, replacement);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
