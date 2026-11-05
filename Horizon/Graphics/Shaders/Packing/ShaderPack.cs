using System.Security.Cryptography;

using Horizon.Content;

namespace Horizon.Graphics;

/// <summary>One stage of a packed module, where it starts and its work group size (zeros for a stage that has none).</summary>
public readonly record struct ShaderPackStage(ShaderStage Stage, string EntryPoint, uint X, uint Y, uint Z);

/// <summary>
/// One module in a pack, what the cache keeps as a .spv and a .notes. Its stages share the SPIR-V, and the Params
/// layout is the reflection the compiler gave, so a packed run sets uniforms the same as a compiled one.
/// </summary>
public sealed class ShaderPackEntry
{
    /// <summary>The key the module is looked up by, see <see cref="ShaderCompiler.CompilerFingerprint"/>.</summary>
    public string Key { get; }

    /// <summary>A file (or an inline program's name) the module was made from, for messages and nothing else.</summary>
    public string Origin { get; }

    public byte[] Spirv { get; }
    public IReadOnlyList<ShaderPackStage> Stages { get; }
    public IReadOnlyList<UniformBlockLayout.Member> Params { get; }

    public ShaderPackEntry(string key, string origin, byte[] spirv, IReadOnlyList<ShaderPackStage> stages, IReadOnlyList<UniformBlockLayout.Member> parameters)
    {
        Key = key;
        Origin = origin;
        Spirv = spirv;
        Stages = stages;
        Params = parameters;
    }

    internal static ShaderPackEntry FromModule(string key, string origin, ShaderModule module) => new(
        key, origin, module.Stages[0].Spirv,
        [.. module.Stages.Select(stage => new ShaderPackStage(stage.Stage, stage.EntryPoint, Group(stage, 0), Group(stage, 1), Group(stage, 2)))],
        [.. module.Params.Members]);

    internal ShaderModule ToModule() => new(
        [.. Stages.Select(stage => new CompiledStage(stage.Stage, stage.EntryPoint, Spirv, [stage.X, stage.Y, stage.Z]))],
        UniformBlockLayout.FromMembers(Params));

    private static uint Group(CompiledStage stage, int axis) => stage.ThreadGroupSize.Length == 3 ? stage.ThreadGroupSize[axis] : 0;

    /// <summary>What's different between two entries of the same key, empty when they're the same to the byte.</summary>
    public string DifferenceFrom(ShaderPackEntry other)
    {
        if (!Spirv.AsSpan().SequenceEqual(other.Spirv)) return $"the SPIR-V differs ({Spirv.Length} and {other.Spirv.Length} bytes)";
        if (!Stages.SequenceEqual(other.Stages)) return $"the stages differ ({Describe(Stages)} and {Describe(other.Stages)})";
        if (!Params.SequenceEqual(other.Params)) return $"the Params layout differs ({Params.Count} and {other.Params.Count} members)";
        return string.Empty;
    }

    private static string Describe(IReadOnlyList<ShaderPackStage> stages) =>
        string.Join(", ", stages.Select(stage => $"{stage.Stage} {stage.EntryPoint} {stage.X}x{stage.Y}x{stage.Z}"));
}

/// <summary>A pack that can't be read, with a message that names it and says how.</summary>
public sealed class ShaderPackException(string message) : Exception(message);

/// <summary>
/// Compiled shaders for a platform that has no compiler. The same modules the cache keeps, by the same keys, in one
/// file in content (<see cref="FOLDER"/>), made by <c>Horizon.Shaders.Build</c> out of a manifest.
/// <para>
/// The file is little endian. Eight bytes of magic, the format, the compiler fingerprint and SPIR-V profile it was
/// made with, the manifest's name, the bindless table sizes it was made for, then the entries sorted by key, then a
/// SHA-256 of all of that. Strings are a length and UTF-8. Nothing in it is a time or a path on the machine that
/// made it, so the same manifest and sources make the same bytes anywhere.
/// </para>
/// </summary>
public sealed class ShaderPack
{
    /// <summary>The content folder packs are found in.</summary>
    public const string FOLDER = "shaders/packed";

    /// <summary>What a pack's name ends in.</summary>
    public const string EXTENSION = ".hzshaders";

    internal const int FORMAT = 1;
    internal const int HASH_BYTES = 32;
    internal static ReadOnlySpan<byte> Magic => "HZSHADER"u8;

    // A pack bigger than this is somebody else's file or a broken stream, either way it isn't read to the end
    internal const int MAX_BYTES = 256 << 20;

    private readonly Dictionary<string, ShaderPackEntry> byKey;

    /// <summary>Where the pack was read from, for messages.</summary>
    public string Name { get; }

    public string Fingerprint { get; }
    public string TargetProfile { get; }

    /// <summary>The manifest the pack was made from, its file name alone.</summary>
    public string Manifest { get; }

    /// <summary>The bindless table sizes the pack holds programs for, ascending.</summary>
    public IReadOnlyList<uint> BindlessCapacities { get; }

    /// <summary>Every module, sorted by key.</summary>
    public IReadOnlyList<ShaderPackEntry> Entries { get; }

    internal ShaderPack(string name, string fingerprint, string targetProfile, string manifest, IReadOnlyList<uint> capacities, IReadOnlyList<ShaderPackEntry> entries)
    {
        Name = name;
        Fingerprint = fingerprint;
        TargetProfile = targetProfile;
        Manifest = manifest;
        BindlessCapacities = capacities;
        Entries = entries;
        byKey = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
    }

    public bool TryGet(string key, out ShaderPackEntry entry) => byKey.TryGetValue(key, out entry!);

    /// <summary>Reads a pack out of content. Throws <see cref="ShaderPackException"/> for anything that isn't a whole pack of this format.</summary>
    public static ShaderPack Load(IContentFiles content, string path)
    {
        try
        {
            using Stream stream = content.OpenRead(path);
            return Read(stream, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ShaderPackException($"The shader pack {path} couldn't be read, {e.Message}");
        }
    }

    /// <summary>Reads a pack from a stream, which may not seek and may hand over a few bytes at a time.</summary>
    public static ShaderPack Read(Stream stream, string name) => Read(stream, name, MAX_BYTES);

    internal static ShaderPack Read(Stream stream, string name, int maxBytes) => Parse(ReadBounded(stream, name, maxBytes), name);

    /// <summary>Helper method to read a stream to its end, stopping with an error past so many bytes rather than reading forever.</summary>
    private static byte[] ReadBounded(Stream stream, string name, int maxBytes)
    {
        var bytes = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytes.Length + read > maxBytes)
                throw new ShaderPackException($"The shader pack {name} is bigger than {maxBytes} bytes, which no pack is. It's the wrong file or a broken one.");
            bytes.Write(buffer, 0, read);
        }

        return bytes.ToArray();
    }

    internal static ShaderPack Parse(byte[] bytes, string name)
    {
        int head = Magic.Length + 4;
        if (bytes.Length < Magic.Length || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new ShaderPackException($"{name} isn't a shader pack ({bytes.Length} bytes without the HZSHADER header). Make it with Horizon.Shaders.Build.");
        if (bytes.Length < head + HASH_BYTES)
            throw new ShaderPackException($"The shader pack {name} is cut short, {bytes.Length} bytes is less than an empty pack.");

        var reader = new PackReader(bytes, name, bytes.Length - HASH_BYTES) { At = Magic.Length };
        int format = (int)reader.UInt();
        if (format != FORMAT)
            throw new ShaderPackException($"The shader pack {name} is format {format} and this engine reads format {FORMAT}. Build it again with the Horizon.Shaders.Build of this engine.");

        byte[] expected = SHA256.HashData(bytes.AsSpan(0, bytes.Length - HASH_BYTES));
        if (!expected.AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - HASH_BYTES)))
            throw new ShaderPackException($"The shader pack {name} is corrupt or cut short, its checksum doesn't match ({bytes.Length} bytes).");

        string fingerprint = reader.String();
        string profile = reader.String();
        string manifest = reader.String();

        uint capacityCount = reader.Count(4);
        var capacities = new uint[capacityCount];
        for (int i = 0; i < capacities.Length; i++) capacities[i] = reader.UInt();

        uint entryCount = reader.Count(4 * 6);
        var entries = new ShaderPackEntry[entryCount];
        for (int i = 0; i < entries.Length; i++)
        {
            string key = reader.String();
            if (i > 0 && string.CompareOrdinal(entries[i - 1].Key, key) >= 0)
                throw reader.Malformed($"its keys aren't sorted and unique at {key}");

            string origin = reader.String();

            var stages = new ShaderPackStage[reader.Count(4 * 5)];
            for (int s = 0; s < stages.Length; s++)
            {
                string stageName = reader.String();
                if (!Enum.TryParse(stageName, ignoreCase: false, out ShaderStage stage) || !Enum.IsDefined(stage))
                    throw reader.Malformed($"{key} has a stage called '{stageName}'");
                stages[s] = new ShaderPackStage(stage, reader.String(), reader.UInt(), reader.UInt(), reader.UInt());
            }

            if (stages.Length == 0) throw reader.Malformed($"{key} has no stages");

            var members = new UniformBlockLayout.Member[reader.Count(4 * 6)];
            for (int m = 0; m < members.Length; m++)
                members[m] = new UniformBlockLayout.Member(reader.String(), reader.String(), reader.Int(), reader.Int(), reader.Int(), reader.Int());

            byte[] spirv = reader.Bytes();
            if (spirv.Length < 20 || spirv.Length % 4 != 0 || BitConverter.ToUInt32(spirv, 0) != 0x07230203)
                throw reader.Malformed($"{key} isn't SPIR-V");

            entries[i] = new ShaderPackEntry(key, origin, spirv, stages, members);
        }

        if (reader.At != bytes.Length - HASH_BYTES)
            throw reader.Malformed("it has bytes after its last entry");

        return new ShaderPack(name, fingerprint, profile, manifest, capacities, entries);
    }

    /// <summary>Reads the parts of a pack, every read checked against the end so a bad length is an error and not a huge array.</summary>
    private sealed class PackReader(byte[] bytes, string name, int end)
    {
        // No string in a pack is anywhere near this, a bigger one is a bad length
        private const int MAX_STRING = 1 << 20;

        public int At { get; set; }

        public ShaderPackException Malformed(string what) => new($"The shader pack {name} is malformed, {what}. Build it again with Horizon.Shaders.Build.");

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > end - At)
                throw new ShaderPackException($"The shader pack {name} is cut short or malformed, it wants {count} bytes at {At} with {end - At} left.");
            var span = bytes.AsSpan(At, count);
            At += count;
            return span;
        }

        public uint UInt() => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int Int() => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        /// <summary>A count of things at least so many bytes each, which has to fit in what's left.</summary>
        public uint Count(int leastBytesEach)
        {
            uint count = UInt();
            if ((long)count * leastBytesEach > end - At) throw Malformed($"it counts {count} things at {At - 4} and there isn't room for them");
            return count;
        }

        public string String()
        {
            uint length = UInt();
            if (length > MAX_STRING) throw Malformed($"a string at {At - 4} is {length} bytes long");
            return System.Text.Encoding.UTF8.GetString(Take((int)length));
        }

        public byte[] Bytes()
        {
            uint length = UInt();
            return Take(length > int.MaxValue ? -1 : (int)length).ToArray();
        }
    }
}
