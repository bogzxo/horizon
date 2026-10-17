using System;
using System.IO;
using System.Text;

using Horizon.Audio;

using Xunit;

namespace Horizon.Tests.Audio;

/// <summary>
/// The mixer with no device, mixed into an array. A sound comes out at its volume on its bus, at its pitch, panned, ends
/// when it ends unless it loops, a sound played too often takes over its own oldest copy, a fade gets to silence and
/// stops, and music streams and loops. And a .wav reads the way it was written.
/// </summary>
public class AudioMixerTests
{
    // A second of a constant level on both sides, at the mixer's own rate
    private static Sound Flat(float level, int frames = 100, int rate = 100)
    {
        var samples = new float[frames * 2];
        Array.Fill(samples, level);
        return new Sound(samples, rate);
    }

    [Fact]
    public void ASoundComesOutAtItsVolumeOnItsBus()
    {
        var mixer = new AudioMixer(100);
        mixer.SetVolume(AudioBus.Effects, 0.5f);
        mixer.Play(Flat(0.8f), AudioBus.Effects, volume: 0.5f);

        var output = new float[20];
        mixer.Mix(output);

        Assert.Equal(0.8f * 0.5f * 0.5f, output[0], 4);
        Assert.Equal(output[0], output[1], 4);
    }

    [Fact]
    public void ASoundEndsWhenItEndsUnlessItLoops()
    {
        var mixer = new AudioMixer(100);
        SoundHandle once = mixer.Play(Flat(0.5f, frames: 10));
        SoundHandle looped = mixer.Play(Flat(0.25f, frames: 10), loop: true);

        var output = new float[2 * 30];
        mixer.Mix(output);

        Assert.False(mixer.IsPlaying(once));
        Assert.True(mixer.IsPlaying(looped));
        Assert.Equal(0.75f, output[0], 4);
        Assert.Equal(0.25f, output[2 * 25], 4);
    }

    [Fact]
    public void PitchPlaysItQuicker()
    {
        var mixer = new AudioMixer(100);
        SoundHandle quick = mixer.Play(Flat(0.5f, frames: 20), pitch: 2.0f);

        mixer.Mix(new float[2 * 11]);

        Assert.False(mixer.IsPlaying(quick));
    }

    [Fact]
    public void PanPutsItOnOneSide()
    {
        var mixer = new AudioMixer(100);
        mixer.Play(Flat(0.5f), pan: -1.0f);

        var output = new float[4];
        mixer.Mix(output);

        Assert.Equal(0.5f, output[0], 4);
        Assert.Equal(0.0f, output[1], 4);
    }

    [Fact]
    public void ASoundPlayedTooOftenTakesOverItsOwnOldest()
    {
        var mixer = new AudioMixer(100);
        Sound hit = Flat(0.1f);
        hit.MaxPlaying = 2;

        SoundHandle first = mixer.Play(hit);
        mixer.Play(hit);
        mixer.Play(hit);

        Assert.Equal(2, mixer.Playing);
        Assert.False(mixer.IsPlaying(first));
    }

    [Fact]
    public void AFadeGetsToSilenceAndStops()
    {
        var mixer = new AudioMixer(100);
        SoundHandle sound = mixer.Play(Flat(0.5f), loop: true);
        mixer.Stop(sound, fade: 0.1f);

        var output = new float[2 * 20];
        mixer.Mix(output);

        Assert.False(mixer.IsPlaying(sound));
        Assert.True(output[2 * 5] < output[0]);
    }

    [Fact]
    public void MusicStreamsAndLoops()
    {
        var mixer = new AudioMixer(100);
        var stream = new CountingStream(frames: 30);
        SoundHandle music = mixer.PlayMusic(stream, fade: 0.0f);

        var output = new float[2 * 100];
        mixer.Mix(output);

        Assert.True(mixer.IsPlaying(music));
        Assert.True(stream.Rewound > 0);
        Assert.Equal(0.5f, output[2 * 40], 4);
    }

    [Fact]
    public void AWaveFileReadsTheWayItWasWritten()
    {
        // 16 bit mono at 8000, three samples, full up, nothing and full down
        var file = new MemoryStream();
        var writer = new BinaryWriter(file);
        short[] samples = [short.MaxValue, 0, short.MinValue];
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + samples.Length * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(8000 * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples.Length * 2);
        foreach (short sample in samples) writer.Write(sample);

        Sound sound = WaveFile.Read(file.ToArray());

        Assert.Equal(8000, sound.SampleRate);
        Assert.Equal(3, sound.Frames);
        Assert.Equal(1.0f, sound.Samples[0], 3);
        Assert.Equal(sound.Samples[0], sound.Samples[1]);
        Assert.Equal(-1.0f, sound.Samples[4], 3);
    }

    // A stream of a constant level that runs out every so many frames, counting the times it went back to the start
    private sealed class CountingStream(int frames) : IAudioStream
    {
        private int left = frames;
        public int Rewound { get; private set; }
        public int SampleRate => 100;

        public int Read(Span<float> into)
        {
            int count = Math.Min(left, into.Length / 2);
            into[..(count * 2)].Fill(0.5f);
            left -= count;
            return count;
        }

        public void Rewind()
        {
            left = frames;
            Rewound++;
        }

        public void Dispose() { }
    }
}
