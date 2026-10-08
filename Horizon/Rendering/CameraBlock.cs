using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Logging;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

/// <summary>
/// The camera as every shader sees it: one uniform block (shaders/common/camera.glsl, binding 0) with the matrices,
/// how fast the camera is going, the size of what is drawn into and the clocks. It is written once per camera per
/// frame rather than two matrices per draw call per shader: whoever is about to draw through a camera calls
/// <see cref="Use"/>, which only touches the GPU when the camera (or what it shows) is not the one the block holds already.
/// <code>
/// CameraBlock.Use(camera);
/// technique.Bind();
/// // draw, the shader reads uViewProjection and the rest from the block
/// </code>
/// The engine binds the block at the start of every frame; nothing else ever binds a uniform buffer at 0.
/// </summary>
public static class CameraBlock
{
    /// <summary>The binding point of the block, which is what shaders/common/camera.glsl says.</summary>
    public const uint BINDING = 0;

    /// <summary>Must match the std140 layout of the block in shaders/common/camera.glsl.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Block
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        public Matrix4x4 ViewProjection;
        public Matrix4x4 InverseViewProjection;
        public Vector2 CameraVelocity;
        public Vector2 MotionScale;
        public Vector2 ViewportSize;
        public Vector2 ViewportTexel;
        public float TotalTime;
        public float DeltaTime;
        public float Runtime;
        public float Padding;
    }

    private static BufferObject? buffer;
    private static bool unavailable;

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
    /// Makes the block if it isn't there yet and binds it, for the frame. Called by the engine at the start of every
    /// frame, with how big what is drawn into is and how the clocks stand.
    /// </summary>
    internal static void BeginFrame(Vector2 viewport, float dt)
    {
        if (!Ensure()) return;

        viewportSize = viewport;
        held.ViewportSize = viewport;
        held.ViewportTexel = new Vector2(viewport.X > 0 ? 1.0f / viewport.X : 0.0f, viewport.Y > 0 ? 1.0f / viewport.Y : 0.0f);
        held.DeltaTime = dt;
        held.TotalTime = (float)GameEngine.Instance.TotalTime;
        held.Runtime = (float)GameEngine.Instance.Runtime;

        // Nothing of the camera is to be trusted from the last frame: what it shows moves with the frame
        frameNumber++;
        dirty = true;
        heldCamera = null;

        buffer!.BindBase(BufferTargetARB.UniformBuffer, BINDING);
    }

    /// <summary>
    /// Has everything that is drawn from here on go through a camera. Cheap to call before every draw: the block is
    /// only written when the camera isn't the one it holds, or the frame moved on since it was.
    /// </summary>
    public static void Use(Camera camera)
    {
        if (!Ensure()) return;

        RenderFrame frame = RenderFrame.Active;
        bool same = ReferenceEquals(camera, heldCamera)
            && frame.CurrentSequence == heldSequence
            && frame.Alpha == heldAlpha
            && frameNumber == heldFrame
            && !dirty;

        if (same) return;

        Matrix4x4 view = camera.View, projection = camera.Projection;
        Matrix4x4 viewProjection = view * projection;
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse))
            inverse = Matrix4x4.Identity;

        held.View = view;
        held.Projection = projection;
        held.ViewProjection = viewProjection;
        held.InverseViewProjection = inverse;
        held.CameraVelocity = camera.Velocity;
        held.MotionScale = new Vector2(projection.M11, projection.M22);

        Write();

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
        if (!Ensure()) return;

        held.View = view;
        held.Projection = projection;
        held.ViewProjection = view * projection;
        if (!Matrix4x4.Invert(held.ViewProjection, out held.InverseViewProjection))
            held.InverseViewProjection = Matrix4x4.Identity;
        held.CameraVelocity = Vector2.Zero;
        held.MotionScale = new Vector2(projection.M11, projection.M22);

        Write();

        heldCamera = null;
        dirty = true;
    }

    /// <summary>How big what is drawn into is, as the block has it.</summary>
    public static Vector2 ViewportSize => viewportSize;

    private static unsafe void Write()
    {
        fixed (Block* block = &held)
            buffer!.Update(block, (nuint)sizeof(Block));
    }

    private static unsafe bool Ensure()
    {
        if (buffer is not null) return true;
        if (unavailable) return false;

        // The engine's, not the scene's that happened to draw first
        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        if (!ObjectManager.Instance.Buffers.TryCreate(
                new BufferObjectDescription
                {
                    Type = BufferTargetARB.UniformBuffer,
                    IsStorageBuffer = true,
                    Size = (uint)sizeof(Block),
                    StorageMasks = BufferStorageMask.DynamicStorageBit
                },
                out var result))
        {
            Log.Error($"[CameraBlock] The camera's uniform block couldn't be made, nothing will draw where it should: {result.Message}");
            unavailable = true;
            return false;
        }

        buffer = result.Asset;
        return true;
    }
}
