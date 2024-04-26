using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Runtime.InteropServices;

using Silk.NET.OpenGL;

namespace Horizon.Core;

public static class Util
{
    public static byte[] GetBytes<T>(T str) where T : unmanaged
    {
        int size = Marshal.SizeOf(str);

        byte[] arr = new byte[size];

        GCHandle h = default;

        try
        {
            h = GCHandle.Alloc(arr, GCHandleType.Pinned);

            Marshal.StructureToPtr<T>(str, h.AddrOfPinnedObject(), false);
        }
        finally
        {
            if (h.IsAllocated)
            {
                h.Free();
            }
        }

        return arr;
    }

    public static T FromBytes<T>(byte[] arr) where T : unmanaged
    {
        T str = default;

        GCHandle h = default;

        try
        {
            h = GCHandle.Alloc(arr, GCHandleType.Pinned);

            str = Marshal.PtrToStructure<T>(h.AddrOfPinnedObject());
        }
        finally
        {
            if (h.IsAllocated)
            {
                h.Free();
            }
        }

        return str;
    }

    /// <summary>
    /// Splits a string into an array of lines, platform agnostic.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns></returns>
    public static IEnumerable<string> SplitToLines(this string input)
    {
        if (input == null)
        {
            yield break;
        }

        using System.IO.StringReader reader = new System.IO.StringReader(input);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    [Pure]
    public static float Clamp(float value, float min, float max)
    {
        return value < min
            ? min
            : value > max
                ? max
                : value;
    }

    [Conditional("DEBUG")]
    public static void CheckGlError(this GL gl, string title)
    {
        var error = gl.GetError();
        if (error != GLEnum.NoError)
        {
            Debug.Print($"{title}: {error}");
        }
    }
}