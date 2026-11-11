namespace StargateGame.Audio;

// Diagnostic output is drained on the game thread. The mixer writes only to a
// bounded native ring; it never touches files or invokes managed code.
public sealed class AudioRecording : IDisposable
{
    readonly AudioSystem audio;
    readonly BinaryWriter writer;
    readonly float[] buffer = new float[96000];
    long frames;
    public AudioRecording(AudioSystem system, string path)
    {
        if (system.SampleRate != 48000 || system.Channels != 2)
            throw new ArgumentException("Diagnostic capture requires stereo 48 kHz audio.", nameof(system));
        audio = system;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        writer = new(new BufferedStream(File.Create(path), 262144));
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)3);
        writer.Write((short)2);
        writer.Write(48000);
        writer.Write(48000 * 8);
        writer.Write((short)8);
        writer.Write((short)32);
        writer.Write("data"u8);
        writer.Write(0);
        audio.Capture(true);
    }
    public void Drain()
    {
        int count;
        while ((count = audio.ReadCapture(buffer)) > 0)
        {
            writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan(0, count * 2)));
            frames += count;
        }
    }
    public void Dispose()
    {
        audio.Capture(false);
        Drain();
        writer.Flush();
        writer.BaseStream.Position = 4;
        writer.Write(checked((int)(36 + frames * 8)));
        writer.BaseStream.Position = 40;
        writer.Write(checked((int)(frames * 8)));
        writer.Dispose();
    }
}
