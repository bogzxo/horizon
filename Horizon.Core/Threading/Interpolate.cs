using System.Numerics;
using System.Runtime.CompilerServices;

namespace Horizon.Core.Threading;

/// <summary>
/// The ways snapshots of the usual kinds of values are blended, for <see cref="IBlendable{T}"/>s to be written out of.
/// </summary>
public static class Interpolate
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Linear(float from, float to, float amount) => from + (to - from) * amount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Linear(double from, double to, float amount) => from + (to - from) * amount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Linear(Vector2 from, Vector2 to, float amount) => from + (to - from) * amount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Linear(Vector3 from, Vector3 to, float amount) => from + (to - from) * amount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Linear(Vector4 from, Vector4 to, float amount) => from + (to - from) * amount;

    /// <summary>
    /// An angle in degrees, the short way round: from 350 to 10 goes through 0, not back through 180.
    /// </summary>
    public static float Angle(float from, float to, float amount)
    {
        float difference = (to - from) % 360.0f;
        if (difference > 180.0f) difference -= 360.0f;
        else if (difference < -180.0f) difference += 360.0f;

        return from + difference * amount;
    }

    /// <summary>
    /// A colour packed into a <see cref="uint"/> a byte a channel (the way sprites carry theirs), channel by channel.
    /// </summary>
    public static uint PackedColor(uint from, uint to, float amount)
    {
        if (from == to)
            return from;

        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            float a = (from >> shift) & 0xFF, b = (to >> shift) & 0xFF;
            result |= (uint)(a + (b - a) * amount + 0.5f) << shift;
        }

        return result;
    }

    /// <summary>
    /// Whatever changes in steps: what it was until the moment is all the way at what it is.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Hold<T>(T from, T to, float amount) => amount >= 1.0f ? to : from;
}
