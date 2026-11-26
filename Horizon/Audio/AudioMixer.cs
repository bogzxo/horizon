using System;
using System.Numerics;
using System.Threading;

namespace Horizon.Audio;

/// <summary>Which volume a sound goes by, each has its own on top of the master one.</summary>
public enum AudioBus
{
    Music,
    Effects,
    Interface
}

/// <summary>A sound that was started, for stopping it or changing it while it plays. Default is none.</summary>
public readonly record struct SoundHandle(int Slot, int Id)
{
    public bool IsNone => Id == 0;
}

/// <summary>
/// Everything that is playing put together into one stereo stream of floats, the device (<see cref="AudioDevice"/>) asks
/// for a few milliseconds of it at a time on a thread of its own. A fixed number of voices, sounds read whole and at most
/// one stream of music at a time with another fading out under it, each at its own pitch, volume, balance and bus, gone
/// from the rate it was recorded at to the device's on the way (linear, which is plenty for game sounds). Starting and
/// stopping are from any thread and make no garbage, the mixing makes none either. Nothing in here needs a device, so the
/// tests mix into an array.
/// </summary>
public sealed class AudioMixer
{
    /// <summary>How many sounds play at once at the most. One more starts in place of whichever has played longest.</summary>
    public const int MAX_VOICES = 48;

    // How many frames of a stream are decoded ahead of where it's playing
    private const int STREAM_FRAMES = 8192;

    private struct Voice
    {
        public Sound? Sound;
        public IAudioStream? Stream;
        public float[]? Buffer;
        public int Buffered;
        public bool Ended;

        public double Position;
        public float Volume, Pitch, Pan;
        public AudioBus Bus;
        public bool Loop;
        public int Id;
        public long Started;

        // Where the volume is fading to, how much it changes a frame, and whether it stops once it gets to nothing
        public float Fade, FadeTarget, FadeStep;
        public bool StopWhenSilent;

        public readonly bool IsPlaying => Sound is not null || Stream is not null;
    }

    private readonly Voice[] voices = new Voice[MAX_VOICES];
    private readonly float[] buses = [1.0f, 1.0f, 1.0f];
    private readonly object gate = new();
    private float masterVolume = 1.0f;
    private int nextId = 1;
    private long started;
    private SoundHandle music;

    // Stream currently being decoded outside gate, so Release defers disposal until decode finishes
    private IAudioStream? decodingStream;
    private bool disposeDecodingStream;

    /// <summary>The frames a second it mixes at, the device's.</summary>
    public int SampleRate { get; }

    /// <summary>The volume of everything, 0 to 1.</summary>
    public float MasterVolume
    {
        get => Volatile.Read(ref masterVolume);
        set => Volatile.Write(ref masterVolume, value);
    }

    public AudioMixer(int sampleRate)
    {
        SampleRate = Math.Max(1, sampleRate);
    }

    /// <summary>The volume of a bus, 0 to 1.</summary>
    public float VolumeOf(AudioBus bus) => Volatile.Read(ref buses[(int)bus]);

    public void SetVolume(AudioBus bus, float volume) => Volatile.Write(ref buses[(int)bus], Math.Clamp(volume, 0.0f, 1.0f));

    /// <summary>How many voices are playing, for a test or the debugger.</summary>
    public int Playing
    {
        get
        {
            lock (gate)
            {
                int count = 0;
                foreach (ref readonly Voice voice in voices.AsSpan())
                    if (voice.IsPlaying) count++;
                return count;
            }
        }
    }

    /// <summary>
    /// Plays a sound once (or for ever, looped, until it's stopped).
    /// </summary>
    /// <param name="pitch">How fast it's played, 2 an octave up and twice as quick.</param>
    /// <param name="pan">From -1 all on the left to 1 all on the right.</param>
    public SoundHandle Play(Sound? sound, AudioBus bus = AudioBus.Effects, float volume = 1.0f, float pitch = 1.0f, float pan = 0.0f, bool loop = false)
    {
        if (sound is null || sound.Frames == 0) return default;

        lock (gate)
        {
            int slot = FreeSlot(sound);
            ref Voice voice = ref voices[slot];
            voice = new Voice
            {
                Sound = sound,
                Volume = volume,
                Pitch = float.IsFinite(pitch) ? Math.Max(0.01f, pitch) : 1.0f,
                Pan = Math.Clamp(pan, -1.0f, 1.0f),
                Bus = bus,
                Loop = loop,
                Id = nextId++,
                Started = started++,
                Fade = 1.0f,
                FadeTarget = 1.0f
            };
            return new SoundHandle(slot, voice.Id);
        }
    }

    /// <summary>
    /// Plays music, fading whatever music was playing out under it over the same time. A stream the mixer is handed is
    /// the mixer's, it's disposed once it's done with.
    /// </summary>
    public SoundHandle PlayMusic(IAudioStream stream, float fade = 1.0f, float volume = 1.0f, bool loop = true)
    {
        lock (gate)
        {
            StopLocked(music, fade);

            int slot = FreeSlot(null);
            ref Voice voice = ref voices[slot];
            voice = new Voice
            {
                Stream = stream,
                Buffer = new float[STREAM_FRAMES * 2],
                Volume = volume,
                Pitch = 1.0f,
                Bus = AudioBus.Music,
                Loop = loop,
                Id = nextId++,
                Started = started++,
                Fade = fade > 0.0f ? 0.0f : 1.0f,
                FadeTarget = 1.0f,
                FadeStep = fade > 0.0f ? 1.0f / (fade * SampleRate) : 0.0f
            };
            return music = new SoundHandle(slot, voice.Id);
        }
    }

    /// <summary>Fades the music out (or stops it at once with no fade).</summary>
    public void StopMusic(float fade = 1.0f)
    {
        lock (gate) StopLocked(music, fade);
    }

    /// <summary>Stops a sound, fading it out over that many seconds, at once for none.</summary>
    public void Stop(SoundHandle handle, float fade = 0.0f)
    {
        lock (gate) StopLocked(handle, fade);
    }

    /// <summary>Changes the volume of a sound that's playing (a loop that gets louder, rain that dies down).</summary>
    public void SetVolume(SoundHandle handle, float volume)
    {
        lock (gate)
        {
            if (IsCurrent(handle)) voices[handle.Slot].Volume = volume;
        }
    }

    /// <summary>Whether a sound is still playing.</summary>
    public bool IsPlaying(SoundHandle handle)
    {
        lock (gate) return IsCurrent(handle);
    }

    /// <summary>Stops everything at once.</summary>
    public void StopAll()
    {
        lock (gate)
        {
            for (int i = 0; i < voices.Length; i++) Release(ref voices[i]);
        }
    }

    /// <summary>
    /// Mixes the next stretch of everything that plays into a span, two floats (left, right) a frame, as many frames as
    /// fit. What was in it is written over. Returns whether any voice was active.
    /// </summary>
    public bool Mix(Span<float> output)
    {
        output.Clear();
        int frames = output.Length / 2;
        bool active = false;
        bool lockTaken = false;

        try
        {
            Monitor.Enter(gate, ref lockTaken);
            for (int i = 0; i < voices.Length; i++)
            {
                ref Voice voice = ref voices[i];
                if (!voice.IsPlaying) continue;
                active = true;

                if (voice.Stream is not null) MixStream(i, output, frames, ref lockTaken);
                else MixSound(ref voice, output, frames);
            }
        }
        finally
        {
            if (lockTaken) Monitor.Exit(gate);
        }

        if (!active) return false;

        // Too many loud things at once is clipped rather than wrapped round into a crack
        ClampOutput(output);
        return true;
    }

    internal static void ClampOutput(Span<float> output)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && output.Length >= Vector<float>.Count)
        {
            int width = Vector<float>.Count;
            int limit = output.Length - width;
            var min = new Vector<float>(-1.0f);
            var max = new Vector<float>(1.0f);

            for (; i <= limit; i += width)
            {
                var samples = new Vector<float>(output.Slice(i));
                // Replace NaN lanes with silence deterministically across x86 and ARM
                samples = Vector.ConditionalSelect(Vector.Equals(samples, samples), samples, Vector<float>.Zero);
                samples = Vector.Min(Vector.Max(samples, min), max);
                samples.CopyTo(output.Slice(i));
            }
        }

        for (; i < output.Length; i++)
        {
            float sample = output[i];
            output[i] = float.IsNaN(sample) ? 0.0f : Math.Clamp(sample, -1.0f, 1.0f);
        }
    }

    private void MixSound(ref Voice voice, Span<float> output, int frames)
    {
        Sound sound = voice.Sound!;
        short[] samples = sound.Samples;
        int length = sound.Frames;
        double step = voice.Pitch * sound.SampleRate / SampleRate;
        Gains(in voice, out float left, out float right);

        // The samples are 16 bits, made levels by the gain they are multiplied by anyway
        left *= Sound.SCALE;
        right *= Sound.SCALE;

        for (int frame = 0; frame < frames; frame++)
        {
            if (voice.Position >= length || voice.Position < 0.0)
            {
                if (!voice.Loop)
                {
                    Release(ref voice);
                    return;
                }

                voice.Position %= length;
                if (voice.Position < 0.0) voice.Position += length;
                if (voice.Position >= length) voice.Position = 0.0;
            }

            int at = (int)voice.Position;
            float t = (float)(voice.Position - at);
            int next = at + 1 < length ? at + 1 : voice.Loop ? 0 : at;
            float fade = StepFade(ref voice);

            output[frame * 2] += (samples[at * 2] + (samples[next * 2] - samples[at * 2]) * t) * left * fade;
            output[frame * 2 + 1] += (samples[at * 2 + 1] + (samples[next * 2 + 1] - samples[at * 2 + 1]) * t) * right * fade;
            voice.Position += step;

            if (voice.StopWhenSilent && voice.Fade <= 0.0f)
            {
                Release(ref voice);
                return;
            }
        }
    }

    private void MixStream(int slot, Span<float> output, int frames, ref bool lockTaken)
    {
        ref Voice voice = ref voices[slot];
        int id = voice.Id;
        IAudioStream stream = voice.Stream!;
        float[] buffer = voice.Buffer!;
        double step = voice.Pitch * stream.SampleRate / SampleRate;
        Gains(in voice, out float left, out float right);

        for (int frame = 0; frame < frames; frame++)
        {
            int at = (int)voice.Position;

            // Running out of what was decoded, what's left is moved to the front and more read in after it
            if (at + 1 >= voice.Buffered && !voice.Ended)
            {
                if (!RefillUnlocked(slot, id, ref lockTaken))
                    return;

                voice = ref voices[slot];
                Gains(in voice, out left, out right);
                at = (int)voice.Position;
            }

            if (at >= voice.Buffered)
            {
                Release(ref voice);
                return;
            }

            int next = at + 1 < voice.Buffered ? at + 1 : at;
            float t = (float)(voice.Position - at);
            float fade = StepFade(ref voice);
            output[frame * 2] += (buffer[at * 2] + (buffer[next * 2] - buffer[at * 2]) * t) * left * fade;
            output[frame * 2 + 1] += (buffer[at * 2 + 1] + (buffer[next * 2 + 1] - buffer[at * 2 + 1]) * t) * right * fade;
            voice.Position += step;

            if (voice.StopWhenSilent && voice.Fade <= 0.0f)
            {
                Release(ref voice);
                return;
            }
        }
    }

    // Decodes the next chunk of a stream with gate released so Play/Stop/SetVolume on the simulation thread never stall
    private bool RefillUnlocked(int slot, int id, ref bool lockTaken)
    {
        ref Voice voice = ref voices[slot];
        IAudioStream stream = voice.Stream!;
        float[] buffer = voice.Buffer!;
        int from = Math.Max(0, (int)voice.Position);
        int keep = Math.Max(0, voice.Buffered - from);
        if (keep > 0 && from > 0)
            Array.Copy(buffer, from * 2, buffer, 0, keep * 2);
        voice.Position -= from;
        voice.Buffered = keep;

        int start = voice.Buffered;
        int room = buffer.Length / 2 - start;
        bool loop = voice.Loop;
        bool ended = voice.Ended;

        decodingStream = stream;
        disposeDecodingStream = false;

        int added = 0;
        bool threw = false;

        Monitor.Exit(gate);
        lockTaken = false;
        try
        {
            bool rewoundEmpty = false;
            while (room > 0 && !ended)
            {
                int got = stream.Read(buffer.AsSpan((start + added) * 2, room * 2));
                if (got > 0)
                {
                    added += got;
                    room -= got;
                    rewoundEmpty = false;
                }
                else if (loop && !rewoundEmpty)
                {
                    stream.Rewind();
                    rewoundEmpty = true;
                }
                else
                {
                    ended = true;
                }
            }
        }
        catch
        {
            threw = true;
            throw;
        }
        finally
        {
            Monitor.Enter(gate, ref lockTaken);
            decodingStream = null;
            bool shouldDispose = disposeDecodingStream;
            disposeDecodingStream = false;

            if (shouldDispose)
            {
                Monitor.Exit(gate);
                lockTaken = false;
                try
                {
                    stream.Dispose();
                }
                finally
                {
                    Monitor.Enter(gate, ref lockTaken);
                }
            }
            else if (threw && voices[slot].Id == id && ReferenceEquals(voices[slot].Stream, stream))
            {
                Release(ref voices[slot]);
            }
        }

        if (voices[slot].Id != id || !ReferenceEquals(voices[slot].Stream, stream))
            return false;

        voices[slot].Buffered += added;
        voices[slot].Ended = ended;
        return true;
    }

    private float VolumeOf(in Voice voice) => voice.Volume * Volatile.Read(ref buses[(int)voice.Bus]) * Volatile.Read(ref masterVolume);

    private void Gains(in Voice voice, out float left, out float right)
    {
        float volume = VolumeOf(voice);
        left = volume * Math.Min(1.0f, 1.0f - voice.Pan);
        right = volume * Math.Min(1.0f, 1.0f + voice.Pan);
    }

    private static float StepFade(ref Voice voice)
    {
        if (voice.FadeStep > 0.0f)
        {
            voice.Fade = voice.Fade < voice.FadeTarget
                ? Math.Min(voice.FadeTarget, voice.Fade + voice.FadeStep)
                : Math.Max(voice.FadeTarget, voice.Fade - voice.FadeStep);
            if (voice.Fade == voice.FadeTarget) voice.FadeStep = 0.0f;
        }

        return voice.Fade;
    }

    private void StopLocked(SoundHandle handle, float fade)
    {
        if (!IsCurrent(handle)) return;
        ref Voice voice = ref voices[handle.Slot];

        if (fade <= 0.0f)
        {
            Release(ref voice);
            return;
        }

        voice.FadeTarget = 0.0f;
        voice.FadeStep = 1.0f / (fade * SampleRate);
        voice.StopWhenSilent = true;
    }

    private bool IsCurrent(SoundHandle handle) =>
        !handle.IsNone && (uint)handle.Slot < voices.Length && voices[handle.Slot].Id == handle.Id && voices[handle.Slot].IsPlaying;

    // A slot that's free, or for a sound already playing as many times as it may, the oldest of those, or failing that
    // the oldest of anything that isn't music
    private int FreeSlot(Sound? sound)
    {
        int free = -1, same = 0, oldestSame = -1, oldest = -1;
        for (int i = 0; i < voices.Length; i++)
        {
            ref Voice voice = ref voices[i];
            if (!voice.IsPlaying)
            {
                if (free < 0) free = i;
                continue;
            }

            if (sound is not null && ReferenceEquals(voice.Sound, sound))
            {
                same++;
                if (oldestSame < 0 || voice.Started < voices[oldestSame].Started) oldestSame = i;
            }

            if (voice.Stream is null && (oldest < 0 || voice.Started < voices[oldest].Started)) oldest = i;
        }

        int chosen = sound is not null && same >= sound.MaxPlaying ? oldestSame : free >= 0 ? free : oldest >= 0 ? oldest : 0;
        Release(ref voices[chosen]);
        return chosen;
    }

    private void Release(ref Voice voice)
    {
        if (voice.Stream is { } stream)
        {
            if (ReferenceEquals(stream, decodingStream))
                disposeDecodingStream = true;
            else
                stream.Dispose();
        }
        voice = default;
    }
}
