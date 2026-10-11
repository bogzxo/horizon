using System.Numerics;

using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Physics.Colliders;
using Horizon.Rendering.Voxels;

using Silk.NET.Input;

using static AutoVoxel.Blocks.Blocks;

namespace AutoVoxel.Gameplay;

/// <summary>
/// The player. Reads the mouse and the keys, steers the <see cref="CharacterController3D"/> they walk about as, puts the
/// camera in their eyes, and breaks and places blocks where they point. Everything a player does goes through here,
/// so survival (what a block costs, what breaking it gives) is a matter of putting rules in the middle of it.
/// <code>
/// W A S D     walk, Ctrl sprints, Shift crouches (and won't walk off an edge)
/// Space       jump, twice quickly to fly, Shift and Space go down and up while flying
/// Mouse       look, left breaks, right places, middle picks the block pointed at
/// 1 - 9       the hotbar, the wheel goes round it
/// </code>
/// </summary>
public sealed class Player : GameComponent
{
    // How far a block can be reached, in blocks
    public const float REACH = 6.0f;

    // How long a button has to be held before it repeats, and how often then
    private const float REPEAT_DELAY = 0.3f, REPEAT_EVERY = 0.15f;

    // Two presses of space within this long is flying
    private const float DOUBLE_TAP = 0.3f;

    private readonly Camera3D camera;
    private readonly VoxelTree world;

    private float sinceSpace = float.MaxValue;
    private float breakHeld, placeHeld;

    public CharacterController3D Body { get; }

    /// <summary>Which way they face, round and up, in radians.</summary>
    public float Yaw { get; set; }

    public float Pitch { get; set; }

    /// <summary>Whether the column of a block is made, a world being made while it's played in has holes in it for a bit.</summary>
    public Func<int, int, bool>? IsMade { get; set; }

    /// <summary>Whether the keys and the mouse are theirs. Not while a menu is up.</summary>
    public bool Controlling { get; set; } = true;

    /// <summary>The blocks of the hotbar, and which one is in hand.</summary>
    public byte[] Hotbar { get; } = [.. Blocks.Blocks.Hotbar.Take(9)];

    public int Selected { get; private set; }

    public byte Holding => Hotbar[Selected];

    /// <summary>What they are pointing at, if anything.</summary>
    public VoxelHit? Target { get; private set; }

    /// <summary>Called when the block in hand changes, for the HUD to say what it is.</summary>
    public Action? SelectionChanged { get; set; }

    public Vector3 Forward => ForwardOf(Yaw, Pitch);

    private static Vector3 ForwardOf(float yaw, float pitch) => new(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), -MathF.Cos(pitch) * MathF.Cos(yaw));

    // Where the look stood at the last tick, and where the mouse was then, for the render thread to carry on from (see LiveForward)
    private readonly Lock lookLock = new();
    private float tickYaw, tickPitch, tickSensitivity;
    private Vector2 tickMouse;
    private bool lookIsLive;

    public Player(CollisionWorld3D collision, VoxelTree world, Camera3D camera, Vector3 feet)
    {
        this.world = world;
        this.camera = camera;
        camera.LiveLook = LiveForward;
        Body = new CharacterController3D(collision, feet) { AutoJump = GameSettings.Current.AutoJump };
    }

    // The scene adds Body next to this, it moves in the physics step and this steers it in the update

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        var input = GameEngine.Instance.Input;
        var keys = input.Keyboard;
        var mouse = input.Mouse;

        if (Controlling)
        {
            // Look about
            float sensitivity = 0.0022f * GameSettings.Current.MouseSensitivity;
            Yaw += mouse.Delta.X * sensitivity;
            Pitch = Math.Clamp(Pitch - mouse.Delta.Y * sensitivity, -1.55f, 1.55f);

            // Walk about, the keys turned the way they face, flat
            var forward = new Vector3(MathF.Sin(Yaw), 0.0f, -MathF.Cos(Yaw));
            var right = new Vector3(MathF.Cos(Yaw), 0.0f, MathF.Sin(Yaw));
            Body.Move = forward * keys.Axis(Key.S, Key.W) + right * keys.Axis(Key.A, Key.D);
            Body.Sprinting = keys.IsDown(Key.ControlLeft) && keys.IsDown(Key.W);
            Body.Crouching = keys.IsDown(Key.ShiftLeft) && !Body.Flying;
            Body.Jumping = keys.IsDown(Key.Space);
            Body.Descending = keys.IsDown(Key.ShiftLeft);

            // Space twice is flying, and twice again is walking
            sinceSpace += dt;
            if (keys.WasPressed(Key.Space))
            {
                if (sinceSpace < DOUBLE_TAP)
                {
                    Body.Flying = !Body.Flying;
                    Body.Velocity = Vector3.Zero;
                    sinceSpace = float.MaxValue;
                }
                else sinceSpace = 0.0f;
            }

            // Landing flying ends it
            if (Body.Flying && Body.IsGrounded) Body.Flying = false;

            ChooseBlock(keys, mouse.Scroll);
            Interact(mouse, dt);
        }
        else
        {
            Body.Move = Vector3.Zero;
            Body.Jumping = false;
            Body.Descending = false;
        }

        // Eyes. The look of the frame comes from the mouse as it is when the frame is drawn, see LiveForward
        camera.Position = Body.Eye;
        camera.Look = Forward;

        lock (lookLock)
        {
            tickYaw = Yaw;
            tickPitch = Pitch;
            tickMouse = mouse.Position;
            tickSensitivity = 0.0022f * GameSettings.Current.MouseSensitivity;
            lookIsLive = Controlling && mouse.Captured;
        }

        // What's in front of them
        Target = world.Raycast(Body.Eye, Forward, REACH, foliage: true, liquid: false, out VoxelHit hit) ? hit : null;
    }

    /// <summary>Takes a block of the hotbar into the hand.</summary>
    public void Select(int slot)
    {
        Selected = Math.Clamp(slot, 0, 8);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Which way the eyes face for the frame being drawn, the last tick's look turned on by however far the mouse has
    /// moved since, so the picture turns with the hand every frame and not in lumps of a tick. Render thread. Zero, for
    /// the blended look, while the mouse isn't theirs (a menu, the tour).
    /// </summary>
    private Vector3 LiveForward()
    {
        lock (lookLock)
        {
            if (!lookIsLive) return Vector3.Zero;

            Vector2 moved = GameEngine.Instance.Input.Mouse.LivePosition - tickMouse;
            float yaw = tickYaw + moved.X * tickSensitivity;
            float pitch = Math.Clamp(tickPitch - moved.Y * tickSensitivity, -1.55f, 1.55f);
            return ForwardOf(yaw, pitch);
        }
    }

    private void ChooseBlock(Horizon.Input.Keyboard keys, float scroll)
    {
        int before = Selected;
        for (int i = 0; i < 9; i++)
        {
            if (keys.WasPressed(Key.Number1 + i)) Selected = i;
        }

        if (scroll > 0.1f) Selected = (Selected + 8) % 9;
        else if (scroll < -0.1f) Selected = (Selected + 1) % 9;

        if (Selected != before) SelectionChanged?.Invoke();
    }

    private void Interact(Horizon.Input.Mouse mouse, float dt)
    {
        // Break, at once on the click and then over and over while it's held
        if (mouse.IsDown(MouseButton.Left))
        {
            if (mouse.WasPressed(MouseButton.Left) || (breakHeld += dt) > REPEAT_DELAY + REPEAT_EVERY)
            {
                if (!mouse.WasPressed(MouseButton.Left)) breakHeld = REPEAT_DELAY;
                Break();
            }
        }
        else breakHeld = 0.0f;

        if (mouse.IsDown(MouseButton.Right))
        {
            if (mouse.WasPressed(MouseButton.Right) || (placeHeld += dt) > REPEAT_DELAY + REPEAT_EVERY)
            {
                if (!mouse.WasPressed(MouseButton.Right)) placeHeld = REPEAT_DELAY;
                Place();
            }
        }
        else placeHeld = 0.0f;

        if (mouse.WasPressed(MouseButton.Middle) && Target is { } target && target.Material != Air)
        {
            // The block pointed at, into the hand, from the hotbar if it's there already
            int already = Array.IndexOf(Hotbar, target.Material);
            if (already >= 0) Selected = already;
            else Hotbar[Selected] = target.Material;
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Breaks the block being pointed at, if any. What a left click does.</summary>
    public void Break()
    {
        if (Target is not { } target || target.Material == Bedrock) return;
        if (IsMade is { } made && !made(target.X, target.Z)) return;

        // Whatever grew on top of it goes with it, a flower doesn't hang in the air
        Span<VoxelEdit> edits = stackalloc VoxelEdit[2];
        int count = 0;
        edits[count++] = new VoxelEdit(target.X, target.Y, target.Z, Air);
        byte above = world.Get(target.X, target.Y + 1, target.Z);
        if (world.KindOf(above) == VoxelKind.Foliage) edits[count++] = new VoxelEdit(target.X, target.Y + 1, target.Z, Air);

        world.SetMany(edits[..count]);
    }

    /// <summary>Puts the block in hand on the face being pointed at, if there's room. What a right click does.</summary>
    public void Place()
    {
        if (Target is not { } target) return;

        // On foliage it takes its place, on anything else it goes on the face that was pointed at
        var (x, y, z) = world.KindOf(target.Material) == VoxelKind.Foliage ? (target.X, target.Y, target.Z) : target.Adjacent;
        if (x < 0 || y < 0 || z < 0 || x >= world.SizeX || y >= world.SizeY || z >= world.SizeZ) return;
        if (IsMade is { } made && !made(x, z)) return;

        byte there = world.Get(x, y, z);
        if (there != Air && world.KindOf(there) is not (VoxelKind.Foliage or VoxelKind.Liquid)) return;

        // Not into themselves
        if (world.Palette[Holding].IsSolid && Box3.Cell(x, y, z).Intersects(Body.Bounds.Inflate(-0.01f))) return;

        // Foliage only on something to grow on
        if (world.KindOf(Holding) == VoxelKind.Foliage && !world.IsSolid(x, y - 1, z)) return;

        world.Set(x, y, z, Holding);
    }
}
