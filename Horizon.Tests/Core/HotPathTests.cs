using System.Reflection;

using Horizon.Core;
using Horizon.Core.Threading;
using Horizon.Graphics;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Tiling;
using Horizon.Rendering.Voxels;
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

        // The sound, mixed on its own thread all the time, and started and stopped all through a fight
        { typeof(Horizon.Audio.AudioMixer), "Mix" },
        { typeof(Horizon.Audio.AudioMixer), "MixSound" },
        { typeof(Horizon.Audio.AudioMixer), "MixStream" },
        { typeof(Horizon.Audio.AudioMixer), "Play" },
        { typeof(Horizon.Audio.AudioMixer), "Stop" },

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
        { typeof(UICompositor), "DrawGlassy" },
        { typeof(UICompositor), "SameBlur" },
        { typeof(UICompositor), "BlendGlass" },
        { typeof(UICompositor), "Seed" },
        { typeof(UICompositor), "Mix" },
        { typeof(Horizon.UI.Drawing.UIRenderer), "Draw" },
        { typeof(Horizon.UI.Drawing.UIRenderer), "Begin" },
        { typeof(Horizon.UI.Drawing.UIRenderer), "DrawRange" },
        { typeof(Horizon.UI.Drawing.UIGlassPass), "Draw" },
        { typeof(Horizon.UI.Drawing.UIGlassPass), "Blur" },
        { typeof(Horizon.UI.Drawing.UIGlassPass), "Pixels" },
        { typeof(UIDrawList), "Glass" },
        { typeof(UIDrawList), "SilverLining" },
        { typeof(UIDrawList), "Popup" },
        { typeof(UIDrawList), "Lifted" },
        { typeof(UIDrawList), "Shadow" },
        { typeof(UIDrawList), "BeginEntrance" },
        { typeof(UIDrawList), "BeginExit" },
        { typeof(Horizon.UI.Components.MenuPanel), "PaintHighlight" },
        { typeof(Horizon.UI.Components.MenuBar), "PaintClosing" },
        { typeof(Horizon.UI.Components.ContextMenu), "PaintClosing" },
        { typeof(Horizon.UI.Components.Dropdown), "PaintClosing" },
        { typeof(Horizon.UI.Components.StatusBar), "Paint" },
        { typeof(Horizon.UI.Components.ToggleButton), "PaintSwitch" },
        { typeof(Horizon.UI.Components.MenuPanel), "Paint" },
        { typeof(Horizon.UI.Components.Dropdown), "PaintPopup" },
        { typeof(Horizon.UI.Components.Notifications), "Paint" },
        { typeof(Horizon.UI.Components.Sparks), "Paint" },
        { typeof(Horizon.UI.Components.Sparks), "Update" },
        { typeof(Horizon.UI.Components.Lights), "Paint" },
        { typeof(Horizon.UI.Components.Lights), "Update" },
        { typeof(Horizon.UI.Components.Lights), "Shine" },
        { typeof(Horizon.UI.Components.Lights), "Ripple" },
        { typeof(Horizon.UI.Components.ToggleButton), "Paint" },
        { typeof(Horizon.UI.Drawing.UITooltip), "Paint" },
        { typeof(UIDrawList), "Text" },
        { typeof(UIDrawList), "Quad" },
        { typeof(UIDrawList), "Icon" },
        { typeof(UISkin), "TryGetIcon" },
        { typeof(UISkin), "TryGetRegion" },
        { typeof(UIFont), "Measure" },
        { typeof(UIFont), "Resolve" },
        { typeof(Horizon.UI.Components.UIComponent), "UpdateTree" },
        { typeof(Horizon.UI.Components.UIComponent), "PaintTree" },
        { typeof(Horizon.UI.Components.UIComponent), "StepHover" },
        { typeof(Horizon.UI.Components.UIComponent), "ApplyHover" },
        { typeof(Horizon.UI.Components.Collapsible), "Paint" },
        { typeof(Horizon.UI.Components.Collapsible), "PaintChildren" },
        { typeof(Horizon.UI.Components.Label), "Paint" },
        { typeof(Horizon.UI.Components.Label), "Measure" },
        { typeof(UIRichText), "Paint" },
        { typeof(Horizon.UI.Components.CodeEditor), "Paint" },
        { typeof(Horizon.UI.Components.CodeEditor), "UpdateTooltip" },
        { typeof(Horizon.UI.Components.CodeEditor), "PaintArt" },
        { typeof(Horizon.UI.Components.CodeEditor), "DrawArt" },
        { typeof(Horizon.UI.Components.CodeEditor), "PaintArtLarge" },
        { typeof(Horizon.UI.Components.CodeEditor), "ArtWidthOn" },
        { typeof(Horizon.UI.Components.CodeEditor), "TexelsOf" },
        { typeof(Horizon.UI.Components.CodeEditor), "FindIcon" },
        { typeof(Horizon.UI.Components.CodeEditor), "FindSprite" },
        { typeof(Horizon.UI.Components.CodeEditor), "PaintCode" },
        { typeof(Horizon.UI.Components.CodeEditor), "PaintCaret" },
        { typeof(Horizon.UI.Components.Sparks), "Update" },
        { typeof(Horizon.UI.Components.Sparks), "Paint" },
        { typeof(Horizon.UI.Components.Sparks), "Emit" },
        { typeof(Horizon.UI.Components.Sparks), "Burst" },
        { typeof(Horizon.UI.Components.CodeEditor), "PaintCompletions" },

        // The terminal, every frame a shell writes anything
        { typeof(Horizon.UI.Components.TerminalView), "PaintTerminal" },
        { typeof(Horizon.UI.Components.TerminalView), "PaintRow" },
        { typeof(Horizon.UI.Components.TerminalView), "PaintRun" },
        { typeof(Horizon.UI.Components.TerminalView), "PaintCell" },
        { typeof(Horizon.UI.Components.TerminalView), "Update" },
        { typeof(Horizon.UI.Components.TerminalView), "UpdateInline" },
        { typeof(Horizon.UI.Components.TerminalView), "PaintInline" },
        { typeof(Horizon.UI.Components.TerminalView), "Paint" },
        { typeof(Horizon.UI.Components.TerminalPanel), "Update" },
        { typeof(Horizon.UI.Components.TerminalPanel), "ShowBackdrop" },
        { typeof(Horizon.UI.Components.TerminalPanel), "SetEdges" },
        { typeof(Horizon.UI.Components.TerminalPanel), "ArrangeRoom" },
        { typeof(Horizon.UI.Components.TerminalPanel), "Paint" },
        { typeof(Horizon.UI.Components.TerminalPanel), "PaintChildren" },
        { typeof(Horizon.UI.Components.TerminalHost), "Update" },
        { typeof(Horizon.UI.Components.TerminalHost), "TakeChanges" },
        { typeof(Horizon.UI.Components.TerminalHost), "KeyPressed" },
        { typeof(Horizon.UI.Components.TerminalCommands), "Match" },
        { typeof(UIDrawList), "PushFont" },
        { typeof(UIDrawList), "PopFont" },
        { typeof(UISkin), "TryGetImage" },
        { typeof(Horizon.UI.Components.Image), "Paint" },

        // The path traced lighting, every frame it is on, and the exposure that follows the picture
        { typeof(PathTracedLighting2D), "Run" },
        { typeof(ExposureEffect), "Render" },
        { typeof(BloomEffect), "Render" },

        // The rooms, told where the player is every tick
        { typeof(TileMapRooms), "Walk" },
        { typeof(TileMapRooms), "DoorAt" },

        // The voxel path tracer, every frame, and what it sends up of the world
        { typeof(VoxelRenderer), "BeforeResolve" },
        { typeof(VoxelRenderer), "Upload" },
        { typeof(VoxelRenderer), "WriteFrame" },
        { typeof(VoxelRenderer), "RunBloom" },
        { typeof(VoxelRenderer), "BindWorld" },

        // Sprites and tiles
        { typeof(SpriteBatch), "Draw" },
        { typeof(SpriteBatch), "BeginItems" },
        { typeof(SpriteBatch), "DrawItems" },
        { typeof(Sprite), "TryCreateItem" },
        { typeof(TileMap), "ResolveTileLocked" },
        { typeof(TileMapGpu), "Sync" },

        // The performance overlay, which is there to count the garbage and had better not be it
        { typeof(PerformanceSample), "Read" },
        { typeof(PerformanceSample), "ReadTimelines" },
        { typeof(PerformanceFace), "Show" },
        { typeof(PerformanceFace), "ShowGraphs" },
        { typeof(PerformanceFace), "ShowPill" },
        { typeof(PerformanceFace), "ShowCard" },
        { typeof(PerformanceFace), "ShowGpu" },
        { typeof(PerformanceFace), "ShowThreads" },
        { typeof(Horizon.UI.Components.Readout), "Paint" },
        { typeof(Horizon.UI.Components.Graph), "Paint" },
        { typeof(Horizon.UI.Components.SegmentBar), "Paint" },
        { typeof(Horizon.UI.Components.Meter), "Paint" },
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
