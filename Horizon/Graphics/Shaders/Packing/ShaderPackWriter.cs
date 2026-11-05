using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Horizon.Graphics;

/// <summary>Writes the bytes of a <see cref="ShaderPack"/>. The same entries in any order make the same bytes.</summary>
public static class ShaderPackWriter
{
    /// <summary>
    /// The pack of some entries. Entries come out sorted by key, capacities ascending, and the manifest is its file name
    /// alone, so where the build ran never ends up in the pack. Two entries with one key have to be the same module.
    /// </summary>
    public static byte[] Write(string fingerprint, string targetProfile, string manifest, IEnumerable<uint> capacities, IEnumerable<ShaderPackEntry> entries)
    {
        var sorted = new SortedDictionary<string, ShaderPackEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (sorted.TryGetValue(entry.Key, out var already))
            {
                string difference = already.DifferenceFrom(entry);
                if (difference.Length > 0)
                    throw new ShaderPackException($"Two modules of key {entry.Key} ({already.Origin} and {entry.Origin}) aren't the same, {difference}.");

                // The same module from two files, the origin that sorts first names it so the bytes don't depend on the order
                if (string.CompareOrdinal(entry.Origin, already.Origin) < 0) sorted[entry.Key] = entry;
                continue;
            }

            if (entry.Stages.Count == 0) throw new ShaderPackException($"The module {entry.Key} of {entry.Origin} has no stages.");
            sorted.Add(entry.Key, entry);
        }

        using var bytes = new MemoryStream();
        bytes.Write(ShaderPack.Magic);
        UInt(bytes, ShaderPack.FORMAT);
        String(bytes, fingerprint);
        String(bytes, targetProfile);
        String(bytes, Path.GetFileName(manifest.Replace('\\', '/')));

        uint[] ordered = [.. capacities.Distinct().Order()];
        UInt(bytes, (uint)ordered.Length);
        foreach (uint capacity in ordered) UInt(bytes, capacity);

        UInt(bytes, (uint)sorted.Count);
        foreach (var entry in sorted.Values)
        {
            String(bytes, entry.Key);
            String(bytes, entry.Origin);

            UInt(bytes, (uint)entry.Stages.Count);
            foreach (var stage in entry.Stages)
            {
                String(bytes, stage.Stage.ToString());
                String(bytes, stage.EntryPoint);
                UInt(bytes, stage.X);
                UInt(bytes, stage.Y);
                UInt(bytes, stage.Z);
            }

            UInt(bytes, (uint)entry.Params.Count);
            foreach (var member in entry.Params)
            {
                String(bytes, member.Name);
                String(bytes, member.Type);
                Int(bytes, member.Offset);
                Int(bytes, member.Size);
                Int(bytes, member.ArrayLength);
                Int(bytes, member.ArrayStride);
            }

            UInt(bytes, (uint)entry.Spirv.Length);
            bytes.Write(entry.Spirv);
        }

        bytes.Write(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
        return bytes.ToArray();
    }

    private static void UInt(Stream stream, uint value)
    {
        Span<byte> span = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        stream.Write(span);
    }

    private static void Int(Stream stream, int value)
    {
        Span<byte> span = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        stream.Write(span);
    }

    private static void String(Stream stream, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        UInt(stream, (uint)utf8.Length);
        stream.Write(utf8);
    }
}
