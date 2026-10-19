using System;
using System.Collections.Generic;
using System.IO;

using Horizon.Logging;

namespace Horizon.Audio;

/// <summary>
/// A sound read off disk whole, a .wav or an .ogg, kept as 16 bit samples two to a frame (left, right) at the rate it
/// was recorded at, the mixer goes from that rate to the device's as it plays. For the short ones, a hit, a click, a
/// call. A piece of music is streamed instead (<see cref="OggStream"/>), a few minutes of it whole is tens of megabytes.
/// <para>
/// Sixteen bits and not floats, half the memory, which is CD quality and more than any of it was mixed at. A fight's
/// sounds were 15 MB of floats (the rain loop alone 9.5), the mixer pays a multiply a sample for it, folded into the gain.
/// </para>
/// </summary>
public sealed class Sound
{
    // By where they are on disk, a sound is read the once whoever asks
    private static readonly Dictionary<string, Sound?> cache = [];

    /// <summary>What a sample of <see cref="Samples"/> is multiplied by to be a level, -1 to 1.</summary>
    public const float SCALE = 1.0f / short.MaxValue;

    /// <summary>The samples, left and right in turn, <see cref="short.MaxValue"/> being as loud as it gets. See <see cref="Level"/>.</summary>
    public short[] Samples { get; }

    /// <summary>A sample as a level, -1 to 1, the way it was written.</summary>
    public float Level(int sample) => Samples[sample] * SCALE;

    /// <summary>How many frames a second it was recorded at.</summary>
    public int SampleRate { get; }

    /// <summary>How many frames it is (a frame is a left and a right).</summary>
    public int Frames => Samples.Length / 2;

    /// <summary>How long it is, in seconds.</summary>
    public float Duration => Frames / (float)SampleRate;

    /// <summary>
    /// How many of this sound play at once at the most, a fourth starts in place of the oldest. Twelve hits in a combo
    /// are not twelve copies on top of each other, which is a wall of noise.
    /// </summary>
    public int MaxPlaying { get; set; } = 3;

    /// <summary>Where it was read from, for the logs.</summary>
    public string Name { get; }

    public Sound(short[] samples, int sampleRate, string name = "")
    {
        if (samples.Length % 2 != 0) throw new ArgumentException("A sound is a left and a right sample a frame.", nameof(samples));
        Samples = samples;
        SampleRate = Math.Max(1, sampleRate);
        Name = name;
    }

    /// <summary>A sound out of levels, -1 to 1, left and right in turn. Anything louder is clipped.</summary>
    public Sound(ReadOnlySpan<float> levels, int sampleRate, string name = "") : this(ToSamples(levels), sampleRate, name) { }

    /// <summary>A level, -1 to 1, as a sample, clipped and rounded.</summary>
    public static short ToSample(float level) => (short)MathF.Round(Math.Clamp(level, -1.0f, 1.0f) * short.MaxValue);

    private static short[] ToSamples(ReadOnlySpan<float> levels)
    {
        var samples = new short[levels.Length];
        for (int i = 0; i < levels.Length; i++) samples[i] = ToSample(levels[i]);
        return samples;
    }

    /// <summary>
    /// Reads a sound, the once, null if it can't be (which is logged). From any thread.
    /// </summary>
    public static Sound? Load(string path)
    {
        string full = Path.GetFullPath(path);
        lock (cache)
        {
            if (cache.TryGetValue(full, out Sound? known)) return known;
        }

        Sound? read = null;
        try
        {
            read = Path.GetExtension(full).Equals(".ogg", StringComparison.OrdinalIgnoreCase) ? ReadOgg(full) : WaveFile.Read(File.ReadAllBytes(full), full);
        }
        catch (Exception exception)
        {
            Log.Warning($"[Sound] Couldn't read '{path}': {exception.Message}");
        }

        lock (cache) return cache[full] = read;
    }

    /// <summary>Forgets every sound that was read, for when they've changed on disk.</summary>
    public static void Forget()
    {
        lock (cache) cache.Clear();
    }

    private static Sound ReadOgg(string path)
    {
        using var reader = new NVorbis.VorbisReader(path);
        int channels = Math.Max(1, reader.Channels);

        // Straight into 16 bits as it is decoded, a list of floats the size of the whole sound on the way was twice what is kept
        var read = new short[(int)Math.Min(int.MaxValue / 2, Math.Max(0, reader.TotalSamples) * 2)];
        int count = 0;
        var block = new float[4096 * channels];
        int got;
        while ((got = reader.ReadSamples(block, 0, block.Length)) > 0)
        {
            for (int i = 0; i + channels <= got; i += channels)
            {
                if (count + 2 > read.Length) Array.Resize(ref read, Math.Max(4096, read.Length * 2));

                short left = ToSample(block[i]);
                read[count++] = left;
                read[count++] = channels > 1 ? ToSample(block[i + 1]) : left;
            }
        }

        if (count != read.Length) Array.Resize(ref read, count);
        return new Sound(read, reader.SampleRate, path);
    }
}
