using System.Numerics;

using Horizon.Graphics;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// The effects a <see cref="Renderer2D"/> puts its picture through before it is shown, in the order they run:
/// <code>
/// renderer.PostProcessing.Add(new CrtEffect());
/// renderer.PostProcessing.Add(new CrtEffect { PixelSize = 2 });
/// </code>
/// The picture is handed from one effect to the next through two targets that take turns, however many effects
/// there are, and the last one draws straight to where the renderer is shown. A renderer without any effects (or
/// with all of them off) doesn't go through here at all: it costs what it cost before there was any of this.
/// Effects can be added, removed and switched on and off from any thread.
/// A <see cref="PostLayer"/> has one of these as well, for what is laid over the picture rather than part of it.
/// </summary>
public sealed class PostProcessor : IDisposable
{
    private readonly Lock effectsLock = new();
    private readonly List<PostEffect> effects = [];

    // Replaced wholesale on every change, so the render thread can walk the array it has while another adds to the list
    private PostEffect[] snapshot = [];

    // The ones that are on this frame, kept between frames so finding them doesn't allocate
    private PostEffect[] active = [];
    private int activeCount;

    private readonly PostTarget?[] targets = new PostTarget?[2];
    private readonly PostContext context = new();

    private PostTechnique? copy;

    public IReadOnlyList<PostEffect> Effects => snapshot;

    /// <summary>Whether any of the effects is on, which is to say whether there is anything to run. From any thread.</summary>
    public bool IsActive
    {
        get
        {
            foreach (PostEffect effect in snapshot)
            {
                if (effect.Enabled)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Adds an effect after the ones that are there.</summary>
    /// <returns>The effect, to change or switch off later.</returns>
    public T Add<T>(T effect) where T : PostEffect
    {
        lock (effectsLock)
        {
            if (!effects.Contains(effect))
            {
                effects.Add(effect);
                snapshot = [.. effects];
            }
        }

        return effect;
    }

    /// <summary>Takes an effect out. What it made on the GPU is the caller's to free (<see cref="PostEffect.Dispose"/>) once it isn't drawn with any more.</summary>
    public bool Remove(PostEffect effect)
    {
        lock (effectsLock)
        {
            if (!effects.Remove(effect))
                return false;

            snapshot = [.. effects];
            return true;
        }
    }

    /// <summary>The first effect of a kind, null if there is none.</summary>
    public T? Get<T>() where T : PostEffect
    {
        foreach (PostEffect effect in snapshot)
        {
            if (effect is T found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Finds the effects that are on. Has to be called once a frame, before <see cref="Run"/>.
    /// </summary>
    /// <returns>Whether there are any.</returns>
    internal bool Prepare()
    {
        PostEffect[] all = snapshot;
        if (active.Length < all.Length)
            active = new PostEffect[all.Length];

        activeCount = 0;
        foreach (PostEffect effect in all)
        {
            if (effect.Enabled)
                active[activeCount++] = effect;
        }

        return activeCount > 0;
    }

    /// <summary>
    /// Runs the picture through the effects that are on.
    /// </summary>
    /// <param name="size">How big the picture is, in pixels.</param>
    /// <param name="picture">The picture as it is, null if it only exists once <paramref name="resolve"/> has drawn it.</param>
    /// <param name="resolve">Draws the picture into whatever is bound, for a renderer that has to work it out first (by lighting it).</param>
    /// <param name="bindOutput">Binds where the picture is shown, set up for however it is to be put there.</param>
    /// <param name="outputSize">How big that is.</param>
    internal void Run(Vector2 size, float dt, Texture? picture, Action resolve, Action bindOutput, Vector2 outputSize)
    {
        int holding = -1;

        if (picture is null)
        {
            // The picture has to be somewhere for the first effect to read
            holding = 0;
            Target(0, size).Bind();
            resolve();
            picture = targets[0]!.Texture;
        }

        context.Processor = this;
        context.DeltaTime = dt;
        context.SourceSize = size;

        for (int i = 0; i < activeCount; i++)
        {
            bool last = i == activeCount - 1;

            // Whichever of the two the picture isn't in right now
            int into = holding == 0 ? 1 : 0;

            context.Source = picture;
            context.OutputSize = last ? outputSize : size;
            context.Into = last ? null : Target(into, size);
            context.Output = bindOutput;
            context.PassedOn = false;

            active[i].Run(context);

            // An effect that had nothing to do left the picture where it was, for the next one to read from there
            if (!last && !context.PassedOn)
            {
                holding = into;
                picture = targets[into]!.Texture;
            }
        }
    }

    private PostTarget Target(int index, Vector2 size)
    {
        if (targets[index] is { } existing && existing.Fits((uint)size.X, (uint)size.Y))
            return existing;

        targets[index]?.Dispose();
        return targets[index] = new PostTarget((uint)size.X, (uint)size.Y);
    }

    internal PostTechnique CopyTechnique => copy ??= new PostTechnique("copy");

    /// <summary>
    /// Puts a picture where it is shown as it is, without any effect having a go at it.
    /// </summary>
    /// <param name="bindOutput">Binds where the picture is shown, set up for however it is to be put there.</param>
    internal void Blit(Texture picture, Action bindOutput)
    {
        PostTechnique technique = CopyTechnique;

        technique.Bind();
        picture.Bind(0);

        bindOutput();
        DrawScreen();

        technique.Unbind();
    }

    /// <summary>
    /// Draws one triangle that covers everything that is bound, with whatever technique is bound. See <see cref="ScreenTriangle"/>.
    /// </summary>
    internal void DrawScreen() => ScreenTriangle.Draw();

    public void Dispose()
    {
        foreach (PostEffect effect in snapshot)
            effect.Dispose();

        for (int i = 0; i < targets.Length; i++)
        {
            targets[i]?.Dispose();
            targets[i] = null;
        }
    }
}
