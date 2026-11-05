using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Slangc.NET;

namespace Horizon.Graphics;

// What a compiled module is kept and shipped under
public static partial class ShaderCompiler
{
    // Goes up when what is kept beside a module or what goes into a key changes, so a cache or a pack made the old way
    // misses rather than loading something that isn't what it says. 3 is the key with every part's length in front
    // of it and source with \n line endings alone
    private const int CACHE_FORMAT = 3;

    /// <summary>
    /// What the SPIR-V depends on besides the source, the compiler's version, the SPIR-V it targets and what it's told.
    /// A Slang that was updated or a flag that changed compiles everything again rather than loading what the old one
    /// made, and a pack made by another one says so instead of missing quietly. Only the managed assembly is asked for
    /// its version, so this works without Slang's natives.
    /// </summary>
    public static string CompilerFingerprint => fingerprint ??= string.Join('|',
        CACHE_FORMAT,
        typeof(SlangCompiler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(SlangCompiler).Assembly.GetName().Version?.ToString() ?? "slang",
        string.Join(' ', SlangArguments));

    // Made on first use, the arguments are a field of another file of this class and static fields of partials start in no promised order
    private static string? fingerprint;

    internal static string KeyFor(string source, string language, string entry) => KeyFor(source, language, entry, CompilerFingerprint);

    /// <summary>
    /// The hash a module is kept under. Every part goes in with its length in front, so moving text from the end of
    /// one part to the start of the next can't give the same key.
    /// </summary>
    internal static string KeyFor(string source, string language, string entry, string fingerprint)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, fingerprint);
        Append(hash, language);
        Append(hash, entry);
        Append(hash, source);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string part)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(part);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, bytes.LongLength);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
