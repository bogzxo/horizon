using System;

namespace Horizon.Audio;

/// <summary>
/// Something played as it is read rather than read whole first, music. The mixer asks it for frames (left and right in
/// turn) on the audio thread as it needs them.
/// </summary>
public interface IAudioStream : IDisposable
{
    /// <summary>How many frames a second it gives.</summary>
    int SampleRate { get; }

    /// <summary>Fills as much of a span as it can, two floats a frame, and says how many frames that was. 0 at its end.</summary>
    int Read(Span<float> frames);

    /// <summary>Back to the start, for a stream that loops.</summary>
    void Rewind();
}

/// <summary>
/// An .ogg read a little at a time as it plays (NVorbis), for music, a whole track decoded first would be tens of
/// megabytes. Mono is played on both sides, past two channels the rest are left out.
/// </summary>
public sealed class OggStream : IAudioStream
{
    private readonly NVorbis.VorbisReader reader;
    private readonly int channels;
    private float[] block = [];

    public int SampleRate => reader.SampleRate;

    /// <summary>Where it was read from.</summary>
    public string Name { get; }

    public OggStream(string path)
    {
        reader = new NVorbis.VorbisReader(path);
        channels = Math.Max(1, reader.Channels);
        Name = path;
    }

    public int Read(Span<float> frames)
    {
        int wanted = frames.Length / 2;
        if (block.Length < wanted * channels) block = new float[wanted * channels];

        int got = reader.ReadSamples(block, 0, wanted * channels) / channels;
        for (int i = 0; i < got; i++)
        {
            float left = block[i * channels];
            frames[i * 2] = left;
            frames[i * 2 + 1] = channels > 1 ? block[i * channels + 1] : left;
        }

        return got;
    }

    public void Rewind() => reader.SeekTo(0);

    public void Dispose() => reader.Dispose();
}
