using System;
using System.Buffers.Binary;
using System.Text;

namespace Horizon.Audio;

/// <summary>
/// Reads a .wav, the RIFF kind every tool writes. Whole numbers of 8, 16, 24 and 32 bits or 32 bit floats, mono or
/// more channels (the first two are kept, mono is played on both), whatever the rate.
/// </summary>
public static class WaveFile
{
    public static Sound Read(ReadOnlySpan<byte> file, string name = "")
    {
        if (file.Length < 12 || !Is(file, 0, "RIFF") || !Is(file, 8, "WAVE")) throw new FormatException("it isn't a RIFF WAVE file.");

        int format = 0, channels = 0, rate = 0, bits = 0;
        ReadOnlySpan<byte> data = default;

        for (int at = 12; at + 8 <= file.Length;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(file[(at + 4)..]);
            if (size < 0 || at + 8 + size > file.Length) size = file.Length - at - 8;
            ReadOnlySpan<byte> chunk = file.Slice(at + 8, size);

            if (Is(file, at, "fmt ") && chunk.Length >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);

                // The extensible kind says what it really is further in
                if (format == 0xFFFE && chunk.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(chunk[24..]);
            }
            else if (Is(file, at, "data")) data = chunk;

            // Chunks are padded to an even length
            at += 8 + size + (size & 1);
        }

        if (channels < 1 || rate < 1 || data.IsEmpty) throw new FormatException("it has no format or no samples.");
        if (format != 1 && format != 3) throw new FormatException($"its samples are kind {format}, only plain whole numbers and floats are read.");

        int bytes = bits / 8;
        if (bytes < 1 || (format == 3 && bits != 32)) throw new FormatException($"its samples are {bits} bits.");

        int frames = data.Length / (bytes * channels);
        var samples = new short[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            int at = frame * bytes * channels;
            short left = Sample(data[at..], format, bytes);
            short right = channels > 1 ? Sample(data[(at + bytes)..], format, bytes) : left;
            samples[frame * 2] = left;
            samples[frame * 2 + 1] = right;
        }

        return new Sound(samples, rate, name);
    }

    // A sample as 16 bits, what a sound keeps. Sixteen bits are taken as they are, the rest go through a level
    private static short Sample(ReadOnlySpan<byte> at, int format, int bytes)
    {
        if (format == 3) return Sound.ToSample(BinaryPrimitives.ReadSingleLittleEndian(at));

        return bytes switch
        {
            1 => Sound.ToSample((at[0] - 128) / 128.0f),
            2 => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(at), (short)-short.MaxValue),
            3 => Sound.ToSample(((at[0] | (at[1] << 8) | (at[2] << 16)) << 8 >> 8) / 8388608.0f),
            _ => Sound.ToSample(BinaryPrimitives.ReadInt32LittleEndian(at) / 2147483648.0f)
        };
    }

    private static bool Is(ReadOnlySpan<byte> file, int at, string tag) =>
        at + 4 <= file.Length && file.Slice(at, 4).SequenceEqual(Encoding.ASCII.GetBytes(tag));
}
