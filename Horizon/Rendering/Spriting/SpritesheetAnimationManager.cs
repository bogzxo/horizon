using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Core;
using Horizon.Core.Components;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// An internal component used to keep track of animated regions of a spritesheet.
/// </summary>
/// <seealso cref="Horizon.GameEntity.Components.IGameComponent" />
public class SpriteSheetAnimationManager : GameComponent
{
    public bool AnimateFrames { get; set; } = true;

    /// <summary>
    /// The animations by name. Simulation thread: they are moved along in the updates, and only read by the frames that
    /// are drawn with the simulation standing still.
    /// </summary>
    public Dictionary<string, SpriteAnimationDefinition> Animations { get; init; }
    public Vector2 SpriteSize { get; set; }

    public SpriteSheetAnimationManager(in Vector2 spriteSize)
    {
        this.SpriteSize = spriteSize;
        this.Animations = new();
    }

    public SpriteSheetAnimationManager(in SpriteSheet sheet)
        : this(sheet.SpriteSize) { }

    public (SpriteDefinition definition, uint index) this[string name]
    {
        get => GetFrame(name);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (SpriteDefinition definition, uint index) GetFrame(string name)
    {
        if (!Animations.TryGetValue(name, out SpriteAnimationDefinition value))
        {
            //Entity.ConcurrentLogger.Instance.Log(
            //    Logging.LogLevel.Error,
            //    $"Attempt to get animation '{name}' which doesn't exist!"
            //); TODO: FIX
            return default;
        }

        return (value.FirstFrame, value.Index);
    }
    public uint GetFrameCount(string name)
    {
        if (!Animations.TryGetValue(name, out SpriteAnimationDefinition value))
        {
            //Entity.ConcurrentLogger.Instance.Log(
            //    Logging.LogLevel.Error,
            //    $"Attempt to get animation '{name}' which doesn't exist!"
            //); TODO: FIX
            return 0;
        }

        return value.Index;
    }

    public void AddAnimation(
        string name,
        Vector2 position,
        uint length,
        float frameTime = 0.1f,
        Vector2? inSize = null,
        uint span = 0
    )
    {
        if (Animations.ContainsKey(name))
        {
            //Entity.ConcurrentLogger.Instance.Log(
            //    Logging.LogLevel.Error,
            //    $"Attempt to add animation '{name}' which already exists!"
            //); TODO: FIX
            return;
        }

        this.Animations.Add(
            name,
            new SpriteAnimationDefinition()
            {
                Index = 0,
                Length = length,
                FirstFrame = new SpriteDefinition
                {
                    Position = position,
                    Size = inSize ?? SpriteSize,
                    Span = span
                },
                FrameTime = frameTime,
                Fuzz = Random.Shared.NextSingle() * 0.2f + 0.8f
            }
        );
    }

    public override void UpdateState(float dt)
    {
        if (!Enabled || !AnimateFrames) return;

        // Changed where they are, rather than taken out and put back: that was a copy of every key and a new node
        // for every animation, every tick, for the garbage collector to clear up after
        foreach (string name in Animations.Keys)
        {
            ref SpriteAnimationDefinition frame = ref CollectionsMarshal.GetValueRefOrNullRef(Animations, name);

            if (frame.Length < 1)
            {
                frame.Index = 0;
                continue;
            }

            frame.Timer += dt * frame.Fuzz;
            if (frame.Timer < frame.FrameTime)
                continue;

            // Whatever went over carries on into the next frame, or the animation runs slow by however much the
            // updates overshoot every frame. A long update moves it along as many frames as it took
            uint steps = frame.FrameTime > 0.0f ? (uint)(frame.Timer / frame.FrameTime) : 1;
            frame.Timer = frame.FrameTime > 0.0f ? frame.Timer - steps * frame.FrameTime : 0.0f;
            frame.Index = (frame.Index + steps) % frame.Length;
        }
    }

    public (bool reset, uint index) IncrementFrame(string name)
    {
        ref SpriteAnimationDefinition frame = ref CollectionsMarshal.GetValueRefOrNullRef(Animations, name);
        if (Unsafe.IsNullRef(ref frame))
            throw new KeyNotFoundException($"There is no animation '{name}'.");

        if (frame.Length < 1)
        {
            frame.Index = 0;
            return (true, 0);
        }

        bool finished = frame.Index + 1 >= frame.Length;
        frame.Index = (frame.Index + 1) % frame.Length;
        return (finished, frame.Index);
    }

    public bool SetFrame(string name, uint index, bool invert = false)
    {
        ref SpriteAnimationDefinition frame = ref CollectionsMarshal.GetValueRefOrNullRef(Animations, name);
        if (Unsafe.IsNullRef(ref frame))
            throw new KeyNotFoundException($"There is no animation '{name}'.");

        if (frame.Length < 1)
        {
            frame.Index = 0;
            return true;
        }

        frame.Index = invert ? frame.Length - index - 1 : index;
        return frame.Index >= frame.Length - 1;
    }
}