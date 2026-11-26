using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;

using Horizon.Audio;
using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.Content.Disposers;
using Horizon.Content.Managers;
using Horizon.Graphics;
using Horizon.Input;

using Xunit;

namespace Horizon.Tests.Audio;

public class Pass1AudioContentInputTests
{
    private static Sound Ramp(int frames, int rate = 100)
    {
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = (i + 1) / (float)(frames + 1);
            samples[i * 2] = v;
            samples[i * 2 + 1] = -v;
        }
        return new Sound(samples, rate);
    }

    [Theory]
    [InlineData(4.0f)]
    [InlineData(7.5f)]
    [InlineData(16.25f)]
    public void AudioMixer_HighPitchLooping_WrapsWithoutIndexOutOfRange(float pitch)
    {
        var mixer = new AudioMixer(100);
        // Short 2-frame sound with pitch >= 4 skips multiple lengths per frame
        Sound sound = Ramp(frames: 2, rate: 100);
        SoundHandle handle = mixer.Play(sound, pitch: pitch, loop: true);

        var output = new float[2 * 64];
        bool active = mixer.Mix(output);

        Assert.True(active);
        Assert.True(mixer.IsPlaying(handle));
        for (int i = 0; i < output.Length; i++)
        {
            Assert.True(float.IsFinite(output[i]));
            Assert.InRange(output[i], -1.0f, 1.0f);
        }
    }

    [Fact]
    public void AudioMixer_NonFinitePitch_FallsBackSafely()
    {
        var mixer = new AudioMixer(100);
        Sound sound = Ramp(frames: 4, rate: 100);
        mixer.Play(sound, pitch: float.NaN, loop: true);
        mixer.Play(sound, pitch: float.PositiveInfinity, loop: true);

        var output = new float[2 * 16];
        bool active = mixer.Mix(output);

        Assert.True(active);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void AudioMixer_MixStream_OutputsFinalBufferedFrameBeforeRelease()
    {
        var mixer = new AudioMixer(100);
        // 3 distinct stereo frames, non-looping
        float[] frames = [0.2f, -0.2f, 0.5f, -0.5f, 0.8f, -0.8f];
        var stream = new FixedFramesStream(frames, sampleRate: 100);
        SoundHandle handle = mixer.PlayMusic(stream, fade: 0.0f, volume: 1.0f, loop: false);

        var output = new float[2 * 5];
        bool active = mixer.Mix(output);

        Assert.True(active);
        Assert.False(mixer.IsPlaying(handle));
        Assert.Equal(0.2f, output[0], 4);
        Assert.Equal(-0.2f, output[1], 4);
        Assert.Equal(0.5f, output[2], 4);
        Assert.Equal(-0.5f, output[3], 4);
        // Final buffered frame must be played, not dropped
        Assert.Equal(0.8f, output[4], 4);
        Assert.Equal(-0.8f, output[5], 4);
        // Subsequent frames after end are silent
        Assert.Equal(0.0f, output[6], 4);
        Assert.Equal(0.0f, output[7], 4);
    }

    [Fact]
    public void AudioMixer_EmptyLoopingStream_DoesNotHang()
    {
        var mixer = new AudioMixer(100);
        var stream = new FixedFramesStream([], sampleRate: 100);
        SoundHandle handle = mixer.PlayMusic(stream, fade: 0.0f, loop: true);

        var output = new float[2 * 8];
        mixer.Mix(output);

        Assert.False(mixer.IsPlaying(handle));
        Assert.All(output, sample => Assert.Equal(0.0f, sample));
    }

    [Fact]
    public void AudioMixer_MixStream_DoesNotHoldMixerLockDuringDecode()
    {
        var mixer = new AudioMixer(100);
        using var inRead = new ManualResetEventSlim(false);
        using var releaseRead = new ManualResetEventSlim(false);
        var stream = new BlockingStream(inRead, releaseRead);

        mixer.PlayMusic(stream, fade: 0.0f, loop: false);

        Exception? mixError = null;
        var mixThread = new Thread(() =>
        {
            try
            {
                mixer.Mix(new float[2 * 8]);
            }
            catch (Exception ex)
            {
                mixError = ex;
            }
        });
        mixThread.Start();

        Assert.True(inRead.Wait(TimeSpan.FromSeconds(5)));

        // Calling Play and StopAll while stream.Read is blocked must not deadlock
        SoundHandle sfx = mixer.Play(Ramp(frames: 4, rate: 100));
        Assert.False(sfx.IsNone);
        mixer.StopAll();

        releaseRead.Set();
        Assert.True(mixThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(mixError);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(33)]
    public void AudioMixer_ClampOutput_HandlesNaNInfAndOutOfRange(int length)
    {
        float[] pattern = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 2.5f, -3.25f, 0.5f, -0.75f, 0.0f];
        var buffer = new float[length];
        for (int i = 0; i < length; i++)
            buffer[i] = pattern[i % pattern.Length];

        AudioMixer.ClampOutput(buffer);

        for (int i = 0; i < length; i++)
        {
            float src = pattern[i % pattern.Length];
            float expected = float.IsNaN(src) ? 0.0f : Math.Clamp(src, -1.0f, 1.0f);
            Assert.Equal(expected, buffer[i]);
        }
    }

    [Fact]
    public void AudioMixer_Mix_ReturnsFalseWhenSilentAndTrueWhenActive()
    {
        var mixer = new AudioMixer(100);
        var output = new float[16];
        Array.Fill(output, 5.0f);

        Assert.False(mixer.Mix(output));
        Assert.All(output, sample => Assert.Equal(0.0f, sample));

        mixer.MasterVolume = 0.5f;
        mixer.SetVolume(AudioBus.Effects, 0.5f);
        Assert.Equal(0.5f, mixer.MasterVolume);
        Assert.Equal(0.5f, mixer.VolumeOf(AudioBus.Effects));

        mixer.Play(Ramp(frames: 8, rate: 100), AudioBus.Effects, volume: 1.0f);
        Assert.True(mixer.Mix(output));
        Assert.True(output[0] > 0.0f);
    }

    [Fact]
    public void WaveFile_Is_ValidatesSpansAndBounds()
    {
        ReadOnlySpan<byte> header = "RIFF\0\0\0\0WAVE"u8;

        Assert.True(WaveFile.Is(header, 0, "RIFF"u8));
        Assert.True(WaveFile.Is(header, 8, "WAVE"u8));
        Assert.False(WaveFile.Is(header, 0, "WAVE"u8));
        Assert.False(WaveFile.Is(header, -1, "RIFF"u8));
        Assert.False(WaveFile.Is(header, 10, "WAVE"u8));
    }

    [Theory]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(9999u)]
    public void WaveFile_Read_RejectsOverflowingOrTruncatedChunkSize(uint badChunkSize)
    {
        var bytes = new byte[24];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 16);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), badChunkSize);

        Assert.Throws<FormatException>(() => WaveFile.Read(bytes));
    }

    [Fact]
    public void WaveFile_Read_RejectsUnsupportedBitDepth()
    {
        var bytes = new byte[48];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 40);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);     // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);     // mono
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 44100);  // rate
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 12);    // invalid 12-bit PCM
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 4);

        Assert.Throws<FormatException>(() => WaveFile.Read(bytes));
    }

    [Fact]
    public void AssetManager_DuplicateName_DoesNotLeakOrReplaceOriginal()
    {
        TestAssetFactory.Reset();
        TestAssetDisposer.Reset();
        using var manager = new AssetManager<TestAsset, TestAssetFactory, TestAssetDescription, TestAssetDisposer>();

        bool firstOk = manager.TryCreate("hero", new TestAssetDescription(1), out var first);
        bool secondOk = manager.TryCreate("hero", new TestAssetDescription(2), out var second);

        Assert.True(firstOk);
        Assert.False(secondOk);
        Assert.Equal(AssetCreationStatus.Failed, second.Status);
        Assert.Equal(1, TestAssetFactory.CreatedCount);
        Assert.Empty(TestAssetDisposer.DisposedHandles);
        Assert.Single(manager.OwnedAssets);
        Assert.Same(first.Asset, manager.NamedAssets["hero"]);
    }

    [Fact]
    public void AssetManager_ReleaseScopeAndRemoveUnnamedExcept_CleanUpCorrectly()
    {
        TestAssetFactory.Reset();
        TestAssetDisposer.Reset();
        using var manager = new AssetManager<TestAsset, TestAssetFactory, TestAssetDescription, TestAssetDisposer>();

        var scopeA = new AssetScope("A");
        var scopeB = new AssetScope("B");

        TestAsset? unnamedA;
        TestAsset? sharedNamed;
        TestAsset? onlyANamed;
        TestAsset? globalUnnamed;

        using (scopeA.Enter())
        {
            unnamedA = manager.Create(new TestAssetDescription(10));
            onlyANamed = manager.CreateOrGet("onlyA", new TestAssetDescription(11));
            sharedNamed = manager.CreateOrGet("shared", new TestAssetDescription(12));
        }

        using (scopeB.Enter())
        {
            var reused = manager.CreateOrGet("shared", new TestAssetDescription(99));
            Assert.Same(sharedNamed, reused);
        }

        using (AssetScope.EnterGlobal())
        {
            globalUnnamed = manager.Create(new TestAssetDescription(13));
        }

        // Release scope A: unnamedA and onlyANamed are freed; sharedNamed (still used by B) and globalUnnamed stay
        int freedByA = manager.Release(scopeA);
        Assert.Equal(2, freedByA);
        Assert.Contains(unnamedA!.Handle, TestAssetDisposer.DisposedHandles);
        Assert.Contains(onlyANamed!.Handle, TestAssetDisposer.DisposedHandles);
        Assert.DoesNotContain(sharedNamed!.Handle, TestAssetDisposer.DisposedHandles);
        Assert.DoesNotContain(globalUnnamed!.Handle, TestAssetDisposer.DisposedHandles);
        Assert.True(manager.Has("shared"));
        Assert.False(manager.Has("onlyA"));

        // Create an unscoped unnamed asset, then sweep with RemoveUnnamedExcept
        HashSet<uint> keep = manager.GetOwnedHandles();
        TestAsset? transient = manager.Create(new TestAssetDescription(14));
        int swept = manager.RemoveUnnamedExcept(keep);

        Assert.Equal(1, swept);
        Assert.Contains(transient!.Handle, TestAssetDisposer.DisposedHandles);
        Assert.True(manager.Has("shared"));
        Assert.Contains(globalUnnamed, manager.OwnedAssets);

        // Releasing scope B now frees sharedNamed
        int freedByB = manager.Release(scopeB);
        Assert.Equal(1, freedByB);
        Assert.False(manager.Has("shared"));
        Assert.Contains(sharedNamed.Handle, TestAssetDisposer.DisposedHandles);
    }

    [Fact]
    public void IndexedContent_SpanLookup_NormalizesNonCanonicalPaths()
    {
        var content = new InMemoryIndexedContent(["assets/ui/button.png", "assets/audio/hit.wav"]);

        Assert.True(content.Exists("assets/ui/button.png"));
        Assert.True(content.Exists(@"assets\ui\button.png"));
        Assert.True(content.Exists("./assets/ui/../ui/button.png"));
        Assert.False(content.Exists("../assets/ui/button.png"));
        Assert.True(content.DirectoryExists(@"assets\ui\"));
        Assert.True(content.DirectoryExists("./assets/audio/../ui"));
        Assert.Equal("assets/ui/button.png", content.ResolvePath(@"assets\ui\.\button.png"));
    }

    [Fact]
    public void InputScript_And_InputManager_UpdateAndResetState()
    {
        var script = new InputScript()
            .Hold(0.0f, GamepadInput.A, 0.2f)
            .Hold(0.0f, GamepadInput.B, -1.0f) // negative duration clamped to 0
            .HoldKey(0.0f, Key.Space, 0.2f)
            .Chord(0.3f, Key.ControlLeft, Key.G);

        Assert.True(script.Length >= 0.3f);

        using var input = new InputManager { ReadsGamepads = false };
        input.Play(script);

        // Negative or NaN dt must not advance or corrupt playback
        input.UpdateState(-0.5f);
        input.UpdateState(float.NaN);

        // First real tick presses A and Space
        input.UpdateState(0.05f);
        Assert.True(input.Keyboard.IsDown(Key.Space));
        Assert.True(input.Keyboard.WasPressed(Key.Space));
        Assert.True(input.Gamepads[0].IsDown(GamepadInput.A));
        Assert.False(input.Gamepads[0].IsDown(GamepadInput.B));

        // Stopping the script releases held script keys and clears script pad
        input.Play(null);
        input.UpdateState(0.05f);
        Assert.False(input.Keyboard.IsDown(Key.Space));
        Assert.True(input.Keyboard.WasReleased(Key.Space));
        Assert.False(input.Gamepads[0].IsDown(GamepadInput.A));

        // Disabled InputManager leaves state untouched
        input.Enabled = false;
        input.Post(InputEvent.KeyChange(Key.A, true));
        input.UpdateState(0.05f);
        Assert.False(input.Keyboard.IsDown(Key.A));
    }

    private sealed class FixedFramesStream(float[] interleaved, int sampleRate) : IAudioStream
    {
        private int position;
        public int SampleRate => sampleRate;

        public int Read(Span<float> frames)
        {
            int available = (interleaved.Length - position) / 2;
            int count = Math.Min(available, frames.Length / 2);
            if (count <= 0) return 0;

            interleaved.AsSpan(position, count * 2).CopyTo(frames);
            position += count * 2;
            return count;
        }

        public void Rewind() => position = 0;
        public void Dispose() { }
    }

    private sealed class BlockingStream(ManualResetEventSlim entered, ManualResetEventSlim release) : IAudioStream
    {
        public bool Disposed { get; private set; }
        public int SampleRate => 100;

        public int Read(Span<float> frames)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return 0;
        }

        public void Rewind() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class TestAsset(uint handle) : IGpuObject
    {
        public uint Handle { get; } = handle;
    }

    private readonly record struct TestAssetDescription(uint Handle) : IAssetDescription;

    private sealed class TestAssetFactory : IAssetFactory<TestAsset, TestAssetDescription>
    {
        public static int CreatedCount { get; private set; }
        public static void Reset() => CreatedCount = 0;

        public static bool TryCreate(in TestAssetDescription description, out AssetCreationResult<TestAsset> result)
        {
            CreatedCount++;
            result = new AssetCreationResult<TestAsset>
            {
                Asset = new TestAsset(description.Handle),
                Status = AssetCreationStatus.Success,
                Message = string.Empty
            };
            return true;
        }
    }

    private sealed class TestAssetDisposer : IGameAssetFinalizer<TestAsset>
    {
        public static List<uint> DisposedHandles { get; } = [];
        public static void Reset() => DisposedHandles.Clear();

        public static void Dispose(in TestAsset asset) => DisposedHandles.Add(asset.Handle);

        public static void DisposeAll(in IEnumerable<TestAsset> assets)
        {
            foreach (var asset in assets)
                DisposedHandles.Add(asset.Handle);
        }
    }

    private sealed class InMemoryIndexedContent(IEnumerable<string> paths) : IndexedContent(paths)
    {
        protected override Stream OpenFile(string path) => new MemoryStream();
    }
}
