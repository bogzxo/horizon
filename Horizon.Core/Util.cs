using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Reflection;
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
public static class ListAdapter<T>
{
    private static readonly FieldInfo _arrayField = typeof(List<T>)
        .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
        .Single(x => x.FieldType == typeof(T[]));

    /// <summary>
    /// Converts
    /// <paramref name="listDONOTMODIFY"/>
    /// to an <see cref="Memory{T}"/>.
    /// </summary>
    /// <param name="listDONOTMODIFY">
    /// The list to convert.
    ///
    /// On each use of the returned memory object the list must have the same value of
    /// <see cref="List{T}.Count"/> as the original passed in value. Also between calls 
    /// you must not do any action that would cause the internal array of the list to
    /// be swapped out with another array.
    /// </param>
    /// <returns>
    /// A <see cref="Memory{T}"/> that is linked to the passed in list.
    /// </returns>
    public static Memory<T> ToMemory(
        List<T> listDONOTMODIFY)
    {
        Memory<T> fullArray = (T[])_arrayField.GetValue(
                listDONOTMODIFY);
        return fullArray[..listDONOTMODIFY.Count];
    }

    /// <summary>
    /// Converts
    /// <paramref name="listDONOTMODIFY"/>
    /// to an <see cref="ReadOnlyMemory{T}"/>.
    /// </summary>
    /// <param name="listDONOTMODIFY">
    /// The list to convert.
    /// On each use of the returned memory object the list must have the same value of
    /// <see cref="List{T}.Count"/> as the original passed in value. Also between calls 
    /// you must not do any action that would cause the internal array of the list to
    /// be swapped out with another array.
    /// </param>
    /// <returns>
    /// A <see cref="ReadOnlyMemory{T}"/> that is linked to the passed in list.
    /// </returns>
    public static ReadOnlyMemory<T> ToReadOnlyMemory(
        List<T> listDONOTMODIFY)
    {
        ReadOnlyMemory<T> fullArray = (T[])_arrayField.GetValue(
                listDONOTMODIFY);
        return fullArray[..listDONOTMODIFY.Count];
    }
}