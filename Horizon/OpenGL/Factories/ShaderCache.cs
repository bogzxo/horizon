using System.Security.Cryptography;
using System.Text;

using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Factories;

/// <summary>
/// Keeps every shader program that is linked on disk, as the driver hands it out, and hands it back to the driver the
/// next time the same program is asked for: no compiling and no linking, which on some drivers is a good part of a
/// second for a scene with a dozen shaders it hasn't had yet. A program is looked up by everything that went into it
/// (every stage after the preprocessor) and by the driver it was made by, so a changed shader or a new driver simply
/// makes it anew. A driver that turns down what it handed out last time (they may, after an update) is no problem either.
/// <para>
/// The programs go into the local application data of the user, under the name of the game. Set
/// <c>HORIZON_SHADER_CACHE=off</c> to leave it alone, or to a folder to keep them there instead.
/// </para>
/// GL thread, like everything to do with shaders.
/// </summary>
internal static class ShaderCache
{
    private const string VARIABLE = "HORIZON_SHADER_CACHE";

    // Whether the driver can hand programs out at all, and where they are kept. Worked out the first time
    private static bool checkedSupport, supported;
    private static string? directory;
    private static string driver = string.Empty;

    /// <summary>
    /// The name a program made of these stages is kept under, null if programs aren't kept (switched off, or a driver
    /// that can't hand them out).
    /// </summary>
    public static string? KeyFor((ShaderType Type, string Source)[] stages)
    {
        if (!IsAvailable())
            return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(driver));

        foreach (var (type, source) in stages)
        {
            hash.AppendData(BitConverter.GetBytes((int)type));
            hash.AppendData(Encoding.UTF8.GetBytes(source));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// Hands a program that was kept to the driver. False if there is none, or the driver turned it down: the program
    /// is to be compiled then, and is no worse off for having tried.
    /// </summary>
    public static unsafe bool TryLoad(uint program, string key)
    {
        string path = PathOf(key);
        if (!File.Exists(path))
            return false;

        try
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length <= sizeof(int))
                return false;

            var gl = ObjectManager.GL;
            GLEnum format = (GLEnum)BitConverter.ToInt32(file, 0);

            fixed (byte* binary = &file[sizeof(int)])
                gl.ProgramBinary(program, format, binary, (uint)(file.Length - sizeof(int)));

            gl.GetProgram(program, GLEnum.LinkStatus, out int linked);
            if (linked != 0)
                return true;

            // Turned down, most likely made by another version of the driver: it is made anew and kept again
            File.Delete(path);
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Asks the driver to keep what the program links to where it can be handed out. Before it is linked.</summary>
    public static void PrepareToKeep(uint program) =>
        ObjectManager.GL.ProgramParameter(program, ProgramParameterPName.BinaryRetrievableHint, 1);

    /// <summary>Writes a program that was just linked to disk, for next time. Whatever goes wrong only means it isn't kept.</summary>
    public static unsafe void Keep(uint program, string key)
    {
        try
        {
            var gl = ObjectManager.GL;
            gl.GetProgram(program, GLEnum.ProgramBinaryLength, out int length);
            if (length <= 0)
                return;

            byte[] file = new byte[length + sizeof(int)];
            uint written;
            GLEnum format;

            fixed (byte* binary = &file[sizeof(int)])
                gl.GetProgramBinary(program, (uint)length, &written, &format, binary);

            if (written == 0)
                return;

            BitConverter.TryWriteBytes(file.AsSpan(0, sizeof(int)), (int)format);

            Directory.CreateDirectory(directory!);

            // Written next to where it goes and moved there whole, so a game that is closed halfway leaves no half a program
            string path = PathOf(key), partial = path + ".partial";
            File.WriteAllBytes(partial, file.AsSpan(0, (int)written + sizeof(int)).ToArray());
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception)
        {
            // Not kept, made again next time
        }
    }

    private static string PathOf(string key) => Path.Combine(directory!, key + ".program");

    /// <summary>
    /// Helper method to find out once whether programs can be kept: the driver has to be able to hand them out, and
    /// there has to be somewhere to keep them.
    /// </summary>
    private static bool IsAvailable()
    {
        if (checkedSupport)
            return supported;

        checkedSupport = true;

        string? setting = Environment.GetEnvironmentVariable(VARIABLE);
        if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
            return supported = false;

        try
        {
            var gl = ObjectManager.GL;
            gl.GetInteger(GLEnum.NumProgramBinaryFormats, out int formats);
            if (formats <= 0)
            {
                Horizon.Logging.Log.Info("[ShaderCache] The driver can't hand compiled programs out, shaders are compiled every time.");
                return supported = false;
            }

            driver = $"{gl.GetStringS(StringName.Vendor)}|{gl.GetStringS(StringName.Renderer)}|{gl.GetStringS(StringName.Version)}";

            // By the game rather than the process, which is "dotnet" for a game that is started through it
            string game = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Horizon";

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            if (string.IsNullOrWhiteSpace(local))
                local = AppContext.BaseDirectory;

            directory = string.IsNullOrWhiteSpace(setting)
                ? Path.Combine(local, "Horizon", game, "ShaderCache")
                : setting;

            Horizon.Logging.Log.Info($"[ShaderCache] Compiled programs are kept in '{directory}'.");
            return supported = true;
        }
        catch (Exception)
        {
            return supported = false;
        }
    }
}
