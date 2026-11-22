using Horizon.Content;
using Horizon.Graphics;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// The parts of pictures an atlas was asked for brought down to a size (<see cref="TextureAtlas.Request(string, string, int, int, int, int, string?, int)"/>
/// with a most), kept on the disk beside the shaders' and the fonts' cache as they came out. Decoding a big picture
/// and shrinking it every launch for the same few hundred pixels was most of what the atlas did as a program came up.
/// Keyed by the picture's file, its size and when it was written, and the part and the size it was brought to, so a
/// picture changed is read again. Anything that goes wrong is the picture read the usual way.
/// </summary>
internal static class ShrunkCache
{
    // What a kept part starts with, and the version of how it's written
    private const uint MAGIC = 0x534B4E52;

    /// <summary>Where a part is kept, null with no cache or a picture that isn't a file of its own.</summary>
    public static string? FileOf(FoundContent file, int x, int y, int width, int height, string? layer, int most)
    {
        if (ShaderCompiler.CacheDirectory() is not { } shaders)
            return null;
        try
        {
            var info = new FileInfo(file.Path);
            if (!info.Exists)
                return null;
            string said = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{x},{y},{width},{height}|{layer}|{most}";
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(said)))[..24];
            return Path.Combine(Path.GetDirectoryName(shaders) ?? shaders, "Atlas", key + ".rgba");
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static SpritePixels? Read(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            using var reader = new BinaryReader(File.OpenRead(file));
            if (reader.ReadUInt32() != MAGIC)
                return null;
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024)
                return null;
            byte[] pixels = reader.ReadBytes(width * height * 4);
            return pixels.Length == width * height * 4 ? new SpritePixels(pixels, width, height) : null;
        }
        catch (Exception)
        {
            // Read the usual way and kept over
            return null;
        }
    }

    // Written to a file of its own and moved into place, so one being read never finds half of it
    public static void Write(string file, in SpritePixels part)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string writing = file + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
            using (var writer = new BinaryWriter(File.Create(writing)))
            {
                writer.Write(MAGIC);
                writer.Write(part.Width);
                writer.Write(part.Height);
                writer.Write(part.Data, 0, part.Width * part.Height * 4);
            }
            File.Move(writing, file, overwrite: true);
        }
        catch (Exception)
        {
            // Not kept, read the usual way the next time
        }
    }
}
