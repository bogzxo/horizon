using System;
using System.Collections.Generic;
using System.IO;

using Horizon.Logging;

namespace Horizon.Audio;

/// <summary>
/// A sound read off disk whole, a .wav or an .ogg, kept as floats two to a frame (left, right) at the rate it was
/// recorded at, the mixer goes from that rate to the device's as it plays. For the short ones, a hit, a click, a call. A
/// piece of music is streamed instead (<see cref="OggStream"/>), a few minutes of it whole is tens of megabytes.
/// </summary>
public sealed class Sound
{
    // By where they are on disk, a sound is read the once whoever asks
    private static readonly Dictionary<string, Sound?> cache = [];

    /// <summary>The samples, left and right in turn.</summary>
    public float[] Samples { get; }

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

    public Sound(float[] samples, int sampleRate, string name = "")
    {
        if (samples.Length % 2 != 0) throw new ArgumentException("A sound is a left and a right sample a frame.", nameof(samples));
        Samples = samples;
        SampleRate = Math.Max(1, sampleRate);
        Name = name;
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
        var read = new List<float>((int)Math.Min(int.MaxValue / 2, reader.TotalSamples * 2));
        var block = new float[4096 * channels];
        int got;
        while ((got = reader.ReadSamples(block, 0, block.Length)) > 0)
        {
            for (int i = 0; i + channels <= got; i += channels)
            {
                float left = block[i];
                float right = channels > 1 ? block[i + 1] : left;
                read.Add(left);
                read.Add(right);
            }
        }

        return new Sound([.. read], reader.SampleRate, path);
    }
}
