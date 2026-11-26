using System;
using System.Buffers.Binary;

namespace Horizon.Audio;

/// <summary>
/// Reads a .wav, the RIFF kind every tool writes. Whole numbers of 8, 16, 24 and 32 bits or 32 bit floats, mono or
/// more channels (the first two are kept, mono is played on both), whatever the rate.
/// </summary>
public static class WaveFile
{
    public static Sound Read(ReadOnlySpan<byte> file, string name = "")
    {
        if (file.Length < 12 || !Is(file, 0, "RIFF"u8) || !Is(file, 8, "WAVE"u8)) throw new FormatException("it isn't a RIFF WAVE file.");

        int format = 0, channels = 0, rate = 0, bits = 0;
        ReadOnlySpan<byte> data = default;

        for (int at = 12; at + 8 <= file.Length;)
        {
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 4)..]);
            long remaining = file.Length - at - 8;
            if (rawSize > remaining) throw new FormatException("it has a chunk larger than the file.");

            int size = (int)rawSize;
            ReadOnlySpan<byte> chunk = file.Slice(at + 8, size);

            if (Is(file, at, "fmt "u8) && chunk.Length >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);

                // The extensible kind says what it really is further in
                if (format == 0xFFFE && chunk.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(chunk[24..]);
            }
            else if (Is(file, at, "data"u8)) data = chunk;

            // Chunks are padded to an even length
            long nextAt = (long)at + 8 + size + (size & 1);
            if (nextAt > file.Length) break;
            at = (int)nextAt;
        }

        if (channels < 1 || rate < 1 || data.IsEmpty) throw new FormatException("it has no format or no samples.");
        if (format != 1 && format != 3) throw new FormatException($"its samples are kind {format}, only plain whole numbers and floats are read.");

        int bytes = bits / 8;
        if (bits % 8 != 0 || bytes < 1 || bytes > 4 || (format == 3 && bits != 32)) throw new FormatException($"its samples are {bits} bits.");

        long frameBytes = (long)bytes * channels;
        long frames = data.Length / frameBytes;
        if (frames * 2 > Array.MaxLength) throw new FormatException("it has too many samples to fit in memory.");

        int frameCount = (int)frames;
        var samples = new short[frameCount * 2];
        for (int frame = 0; frame < frameCount; frame++)
        {
            int at = (int)(frame * frameBytes);
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

    internal static bool Is(ReadOnlySpan<byte> file, int at, ReadOnlySpan<byte> tag) =>
        at >= 0 && tag.Length <= file.Length - at && file.Slice(at, tag.Length).SequenceEqual(tag);
}
