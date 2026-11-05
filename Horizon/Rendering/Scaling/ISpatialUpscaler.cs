using Horizon.Graphics;

namespace Horizon.Rendering.Scaling;

/// <summary>
/// An upscaler of somebody's own, a vendor's spatial one (Snapdragon GSR 1 is the sort of thing) or a game's. Spatial
/// means it has the colour of one frame to go on and nothing else, no depth, no motion and no history, which is all a
/// 2D frame with a UI in it has to give. A temporal one (FSR 2, Arm ASR, GSR 2) needs depth, motion vectors and a
/// jittered camera the engine doesn't make, and doesn't fit here.
/// <code>
/// engine.WindowManager.Scaling.Settings = RenderScaleSettings.At(0.6f) with { Upscaler = Upscaler.Custom, Custom = new MyUpscaler() };
/// </code>
/// Everything is called on the render thread. The engine keeps the upscaler for as long as it's in the settings and
/// disposes of it neither when it's swapped out nor at the end, it's whoever made it's.
/// </summary>
public interface ISpatialUpscaler
{
    /// <summary>What it's called in the log and in <see cref="RenderScalePlan"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Whether it can take a scene of one size to a window of another. If not, CAS (or bilinear, where CAS can't
    /// either) does that frame and the plan says why. Asked whenever the sizes change.
    /// </summary>
    bool Supports(uint sceneWidth, uint sceneHeight, uint outputWidth, uint outputHeight);

    /// <summary>
    /// Draws the blown up scene into what is bound, which is the window, with the viewport over all of it and nothing
    /// blended. It has to cover every pixel, and leave the window bound. Whatever it binds besides it may leave, the
    /// engine binds what it needs again.
    /// </summary>
    void Upscale(in SpatialUpscale pass);
}

/// <summary>What an <see cref="ISpatialUpscaler"/> has to work with for a frame.</summary>
/// <param name="Scene">The scene as it was drawn, its colours as they would have gone to the screen (sRGB encoded) and its alpha.</param>
/// <param name="SceneWidth">How wide the scene is, in pixels.</param>
/// <param name="SceneHeight">How high the scene is.</param>
/// <param name="OutputWidth">How wide the window is.</param>
/// <param name="OutputHeight">How high the window is.</param>
/// <param name="Sharpness">The sharpness the settings ask for, 0 to 1.</param>
public readonly record struct SpatialUpscale(Texture Scene, uint SceneWidth, uint SceneHeight, uint OutputWidth, uint OutputHeight, float Sharpness);
