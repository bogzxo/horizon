using System.Reflection;

using Horizon.Core;
using Horizon.Core.Threading;
using Horizon.Graphics;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests;

/// <summary>
/// What runs every frame, every tick or every time something is drawn must not make a closure. A lambda that uses
/// anything of the method it is written in has that put on the heap for it, and the object it is put in is made as
/// the method (or the block the thing is declared in) starts, not when the lambda is reached. A method with one
/// lambda on a path it takes once a year makes garbage every time it is called.
/// <code>
/// void ApplyPending()
/// {
///     if (pending is not { } settings) return;        // leaves here nearly every time, the garbage is made already
///     OnWindowThread(() => Apply(settings));
/// }
/// </code>
/// That one was 48 bytes a frame out of the window manager and all the garbage the render thread made. The cure is
/// the lambda in a method of its own that is handed what it needs. This reads the engine as it was compiled and
/// fails if any of the methods below makes such an object anywhere in it.
/// </summary>
public class HotPathTests
{
    private const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    // What a "new" of something is in the bytes of a method, the token of the constructor comes after it
    private const byte NEWOBJ = 0x73;

    public static TheoryData<Type, string> EveryFrame => new()
    {
        // The frame itself
        { typeof(WindowManager), "ApplyPendingDisplay" },
        { typeof(WindowManager), "DoExclusiveWorkTimed" },
        { typeof(WindowManager), "DrawFrame" },
        { typeof(LoopStatistics), "Record" },
        { typeof(GraphicsDevice), "BeginFrame" },
        { typeof(GraphicsDevice), "EndFrame" },
        { typeof(GraphicsDevice), "Flush" },
        { typeof(GraphicsDevice), "PrepareDraw" },

        // Buffers, which are rewritten, thrown away and made anew all frame long
        { typeof(GraphicsDevice), "AllocateBuffer" },
        { typeof(GraphicsDevice), "RetireBuffer" },
        { typeof(GraphicsDevice), "UpdateBuffer" },
        { typeof(GraphicsDevice), "FillBuffer" },
        { typeof(GpuBuffer), "Upload" },
        { typeof(GpuBuffer), "Update" },

        // The UI, painted every tick and drawn every frame
        { typeof(UICompositor), "UpdateState" },
        { typeof(UICompositor), "PaintPending" },
        { typeof(UICompositor), "Capture" },
        { typeof(UICompositor), "Render" },
        { typeof(UICompositor), "UploadCaptured" },
        { typeof(UICompositor), "DrawList" },
        { typeof(UIDrawList), "Text" },
        { typeof(UIDrawList), "Quad" },
        { typeof(UIDrawList), "Icon" },
        { typeof(UISkin), "TryGetIcon" },
        { typeof(UISkin), "TryGetRegion" },
        { typeof(UIFont), "Measure" },
        { typeof(UIFont), "Resolve" },

        // Sprites and tiles
        { typeof(SpriteBatch), "Draw" },
        { typeof(Sprite), "TryCreateItem" },
        { typeof(TileMap), "ResolveTileLocked" },
        { typeof(TileMapGpu), "Sync" },

        // The performance overlay, which is there to count the garbage and had better not be it
        { typeof(PerformanceSample), "Read" },
        { typeof(PerformanceSample), "ReadTimelines" },
        { typeof(PerformanceBoard), "Lay" },
    };

    [Theory]
    [MemberData(nameof(EveryFrame))]
    public void What_runs_every_frame_makes_no_closure(Type type, string name)
    {
        MethodInfo[] methods = [.. type.GetMethods(ALL).Where(method => method.Name == name)];

        // A name that is not there any more has been renamed, and the list above wants the new one
        Assert.True(methods.Length > 0, $"{type.Name} has no method called {name} any more, the list in this test wants looking at.");

        foreach (MethodInfo method in methods)
        {
            string? closure = ClosureMadeIn(method);
            Assert.True(closure is null, $"{type.Name}.{name} makes a {closure} every time it is called. Put the lambda in a method of its own, see the top of this file.");
        }
    }

    [Fact]
    public void A_closure_is_found_where_there_is_one()
    {
        // The test above passes for anything if this finds nothing, so it is shown one
        MethodInfo guilty = typeof(HotPathTests).GetMethod(nameof(Guilty), ALL)!;
        MethodInfo innocent = typeof(HotPathTests).GetMethod(nameof(Innocent), ALL)!;

        Assert.NotNull(ClosureMadeIn(guilty));
        Assert.Null(ClosureMadeIn(innocent));
    }

    private static int Guilty(int[] numbers, int limit) => limit < 0 ? 0 : Array.FindIndex(numbers, number => number > limit);

    private static int Innocent(int[] numbers) => Array.FindIndex(numbers, static number => number > 3);

    /// <summary>
    /// Helper method to find out whether a method makes one of the objects the compiler writes for what a lambda
    /// keeps hold of, by looking through its bytes for a new of one.
    /// </summary>
    /// <returns>What the compiler called it, null if the method makes none.</returns>
    private static string? ClosureMadeIn(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            return null;

        Type[]? ofType = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        Type[]? ofMethod = method.IsGenericMethod ? method.GetGenericArguments() : null;

        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != NEWOBJ)
                continue;

            // Not every 0x73 in there is a new, some are part of a number. One that doesn't name a constructor isn't
            MethodBase? made;
            try
            {
                made = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), ofType, ofMethod);
            }
            catch (Exception)
            {
                continue;
            }

            if (made?.DeclaringType is { } declared && declared.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
                return declared.Name;
        }

        return null;
    }
}
