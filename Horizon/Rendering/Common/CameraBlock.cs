using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering;

/// <summary>
/// The camera as every shader sees it, one uniform block (shaders/common/camera.slang, binding 0) with the matrices,
/// how fast the camera is going, the size of what is drawn into and the clocks. It is written once per camera per
/// frame rather than two matrices per draw call per shader. Whoever is about to draw through a camera calls
/// <see cref="Use"/>, which only touches the GPU when the camera (or what it shows) is not the one the block holds already.
/// <code>
/// CameraBlock.Use(camera);
/// technique.Bind();
/// // draw, the shader reads uViewProjection and the rest from the block
/// </code>
/// The block goes into the frame's uniform arena, which the device hands out of (see <see cref="GraphicsDevice.SetCameraBlock"/>),
/// and the projection is turned the way Vulkan wants its clip space on the way (<see cref="GraphicsDevice.ClipCorrection"/>).
/// </summary>
public static class CameraBlock
{
    /// <summary>The binding point of the block, which is what shaders/common/camera.slang says.</summary>
    public const uint BINDING = 0;

    /// <summary>Must match the layout of the block in shaders/common/camera.slang.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Block
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        public Matrix4x4 ViewProjection;
        public Matrix4x4 InverseViewProjection;
        public Vector2 ProjectionScale;
        public Vector2 Padding2;
        public Vector2 ViewportSize;
        public Vector2 ViewportTexel;
        public float TotalTime;
        public float DeltaTime;
        public float Runtime;
        public float Padding;
    }

    // What the block holds, to spare the GPU being told again
    private static Block held;
    private static Camera? heldCamera;
    private static long heldFrame = -1, heldSequence = -1, frameNumber;
    private static float heldAlpha = float.NaN;
    private static bool dirty = true;

    private static Vector2 viewportSize;

    /// <summary>The camera the block was last written for, null before the first.</summary>
    public static Camera? Current => heldCamera;

    /// <summary>
    /// Starts the frame's block. Called by the engine at the start of every frame, with how big what is drawn into
    /// is and how the clocks stand.
    /// </summary>
    internal static void BeginFrame(Vector2 viewport, float dt)
    {
        viewportSize = viewport;
        held.ViewportSize = viewport;
        held.ViewportTexel = new Vector2(viewport.X > 0 ? 1.0f / viewport.X : 0.0f, viewport.Y > 0 ? 1.0f / viewport.Y : 0.0f);
        held.DeltaTime = dt;
        held.TotalTime = (float)GameEngine.Instance.TotalTime;
        held.Runtime = (float)GameEngine.Instance.Runtime;

        // Nothing of the camera is to be trusted from the last frame, what it shows moves with the frame
        frameNumber++;
        dirty = true;
        heldCamera = null;
    }

    /// <summary>The frame is drawn. The engine calls it.</summary>
    internal static void EndFrame()
    { }

    /// <summary>
    /// Has everything that is drawn from here on go through a camera. Cheap to call before every draw, the block is
    /// only written when the camera isn't the one it holds, or the frame moved on since it was.
    /// </summary>
    public static void Use(Camera camera)
    {
        RenderFrame frame = RenderFrame.Active;
        bool same = ReferenceEquals(camera, heldCamera)
            && frame.CurrentSequence == heldSequence
            && frame.Alpha == heldAlpha
            && frameNumber == heldFrame
            && !dirty;

        if (same) return;

        Set(camera.View, camera.Projection);

        heldCamera = camera;
        heldSequence = frame.CurrentSequence;
        heldAlpha = frame.Alpha;
        heldFrame = frameNumber;
        dirty = false;
    }

    /// <summary>
    /// Has what is drawn from here on go through matrices of somebody's own rather than a camera's, for what draws in
    /// a space of its own (a transition over the whole screen, say). The next <see cref="Use"/> puts a camera back.
    /// </summary>
    public static void Use(in Matrix4x4 view, in Matrix4x4 projection)
    {
        Set(view, projection);
        heldCamera = null;
        dirty = true;
    }

    /// <summary>How big what is drawn into is, as the block has it.</summary>
    public static Vector2 ViewportSize => viewportSize;

    private static void Set(in Matrix4x4 view, in Matrix4x4 projection)
    {
        // How much of the screen a unit of the world is, the camera's own projection before Vulkan's way round is applied to it
        held.ProjectionScale = new Vector2(projection.M11, projection.M22);

        Matrix4x4 corrected = projection * GraphicsDevice.ClipCorrection;
        Matrix4x4 viewProjection = view * corrected;
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse))
            inverse = Matrix4x4.Identity;

        held.View = view;
        held.Projection = corrected;
        held.ViewProjection = viewProjection;
        held.InverseViewProjection = inverse;

        GraphicsDevice.Current.SetCameraBlock(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref held, 1)));
    }
}
