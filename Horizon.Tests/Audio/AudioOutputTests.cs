using System;

using Horizon.Audio;
using Horizon.Platform;

using Xunit;

namespace Horizon.Tests.Audio;

/// <summary>
/// The device over a pretend output that's pulled by hand, so no speakers are needed. It opens at what it asked for,
/// mixes at the rate the output really gave, starts only once the mixer is there, holds every voice still while paused
/// and carries on after, and lets go of the output once and for all. An output that won't open is no sound, not a crash.
/// </summary>
public class AudioOutputTests
{
    // An output that does what it's told and is pulled from the test itself
    private sealed class FakeOutput : IAudioOutput
    {
        public AudioFormat? Wanted;
        public Func<AudioFormat, AudioFormat> Gives = wanted => wanted;
        public bool ThrowOnOpen;
        public bool PullOnStart;
        public AudioRender? Render;
        public int Starts, Pauses, Resumes, Disposals;
        public bool PulledOnStart;

        public AudioFormat Open(AudioFormat wanted)
        {
            Wanted = wanted;
            if (ThrowOnOpen) throw new InvalidOperationException("no speakers in here");
            return Gives(wanted);
        }

        public void Start(AudioRender render)
        {
            Starts++;
            Render = render;
            if (!PullOnStart) return;
            render(new float[64]);
            PulledOnStart = true;
        }

        public void Pause() => Pauses++;
        public void Resume() => Resumes++;
        public void Dispose() => Disposals++;

        // Asks for a buffer the way a real output's thread would
        public (bool Heard, float[] Samples) Pull(int frames)
        {
            var buffer = new float[frames * 2];
            Array.Fill(buffer, 9.0f);
            bool heard = Render!(buffer);
            return (heard, buffer);
        }
    }

    // A sound that rises by a thousandth every frame, so where a voice has got to can be read off what comes out
    private static Sound Ramp(int frames, int rate)
    {
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++) samples[i * 2] = samples[i * 2 + 1] = i / 1000.0f;
        return new Sound(samples, rate);
    }

    [Fact]
    public void ItAsksForStereoFloatsAt48000AndMixesAtWhatItGot()
    {
        var output = new FakeOutput { Gives = wanted => wanted with { SampleRate = 44100 } };
        using var device = new AudioDevice(output);

        Assert.Equal(new AudioFormat(48000, 2, 512), output.Wanted);
        Assert.True(device.IsOpen);
        Assert.Equal(44100, device.Mixer.SampleRate);
        Assert.Equal(44100, device.Format.SampleRate);
        Assert.Equal(1, output.Starts);

        device.Mixer.Play(Ramp(1000, 44100));
        var (heard, samples) = output.Pull(16);

        Assert.True(heard);
        Assert.Equal(32, samples.Length);
        for (int frame = 0; frame < 16; frame++)
        {
            Assert.Equal(frame / 1000.0f, samples[frame * 2], 4);
            Assert.Equal(samples[frame * 2], samples[frame * 2 + 1], 4);
        }
    }

    [Fact]
    public void NothingPlayingIsSilenceAndSaysSo()
    {
        var output = new FakeOutput();
        using var device = new AudioDevice(output);

        var (heard, samples) = output.Pull(8);

        Assert.False(heard);
        Assert.All(samples, sample => Assert.Equal(0.0f, sample));
    }

    [Fact]
    public void ThePullCanComeStraightAwayBecauseTheMixerIsThereFirst()
    {
        var output = new FakeOutput { PullOnStart = true };
        using var device = new AudioDevice(output);

        Assert.True(output.PulledOnStart);
        Assert.True(device.IsOpen);
    }

    [Fact]
    public void AnOutputThatWontOpenIsNoSoundAndIsLetGo()
    {
        var output = new FakeOutput { ThrowOnOpen = true };
        using var device = new AudioDevice(output);

        Assert.False(device.IsOpen);
        Assert.Equal(0, output.Starts);
        Assert.Equal(1, output.Disposals);
        Assert.Equal(48000, device.Mixer.SampleRate);

        // The mixer still works for whoever mixes it by hand
        device.Mixer.Play(Ramp(100, 48000));
        Assert.Equal(1, device.Mixer.Playing);
    }

    [Fact]
    public void AnOutputThatGivesBackMonoIsNoSound()
    {
        var output = new FakeOutput { Gives = wanted => wanted with { Channels = 1 } };
        using var device = new AudioDevice(output);

        Assert.False(device.IsOpen);
        Assert.Equal(0, output.Starts);
        Assert.Equal(1, output.Disposals);
        Assert.Equal(48000, device.Mixer.SampleRate);
    }

    [Fact]
    public void NoOutputAtAllStillHasAMixer()
    {
        using var none = new AudioDevice((IAudioOutput?)null);
        using var application = new AudioDevice(open: false);

        Assert.False(none.IsOpen);
        Assert.False(application.IsOpen);
        Assert.Equal(48000, application.Mixer.SampleRate);

        application.Pause();
        Assert.True(application.IsPaused);
        application.Resume();
        Assert.False(application.IsPaused);
    }

    [Theory]
    [InlineData("off", true)]
    [InlineData("OFF", true)]
    [InlineData("on", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HorizonAudioOffSwitchesItOff(string? value, bool off) => Assert.Equal(off, AudioDevice.SaysOff(value));

    [Fact]
    public void PausedNothingMovesOnAndResumeCarriesOnFromTheSamePlace()
    {
        var output = new FakeOutput();
        using var device = new AudioDevice(output);
        var handle = device.Mixer.Play(Ramp(1000, 48000));

        output.Pull(10);
        device.Pause();

        Assert.True(device.IsPaused);
        Assert.Equal(1, output.Pauses);

        // A buffer asked for anyway while paused is silence, and pausing twice tells the output once
        for (int i = 0; i < 5; i++)
        {
            var (heard, samples) = output.Pull(32);
            Assert.False(heard);
            Assert.All(samples, sample => Assert.Equal(0.0f, sample));
        }

        device.Pause();
        Assert.Equal(1, output.Pauses);
        Assert.True(device.Mixer.IsPlaying(handle));

        device.Resume();
        Assert.False(device.IsPaused);
        Assert.Equal(1, output.Resumes);

        var (after, resumed) = output.Pull(4);
        Assert.True(after);
        Assert.Equal(10 / 1000.0f, resumed[0], 4);
        Assert.Equal(13 / 1000.0f, resumed[6], 4);

        device.Resume();
        Assert.Equal(1, output.Resumes);
    }

    [Fact]
    public void DisposingLetsTheOutputGoOnceAndNothingIsMixedAfter()
    {
        var output = new FakeOutput();
        var device = new AudioDevice(output);
        device.Mixer.Play(Ramp(1000, 48000));

        device.Dispose();
        device.Dispose();

        Assert.Equal(1, output.Disposals);
        Assert.Equal(0, device.Mixer.Playing);

        // A late pull from a thread that hasn't quite stopped is silence, and nothing goes to the output any more
        var (heard, samples) = output.Pull(16);
        Assert.False(heard);
        Assert.All(samples, sample => Assert.Equal(0.0f, sample));

        device.Pause();
        device.Resume();
        Assert.Equal(0, output.Pauses);
        Assert.Equal(0, output.Resumes);
    }
}
