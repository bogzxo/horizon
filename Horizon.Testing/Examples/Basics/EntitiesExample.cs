using System.Numerics;
using System.Text;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Input;
using Horizon.OpenGL;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;
using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.Testing.Examples.Basics;

/// <summary>
/// Entities and components, the stuff everything in Horizon is built out of. Three hives keep hatching little bugs
/// that fly about for a few seconds and get binned, and the panel on the left is the scene's actual entity tree,
/// read live, so you can watch things get added, set up, switched off and destroyed.
/// <para>
/// What to look at: <see cref="Entity"/> and <see cref="GameObject"/> (a thing with children and components),
/// <see cref="GameComponent"/> (a bit of behaviour you bolt onto one), <see cref="Entity.AddEntity{T}(T)"/> and
/// <see cref="Entity.AddComponent{T}(T)"/>, <see cref="Entity.Initialize"/> against the constructor, the turns
/// everything gets (<see cref="Entity.UpdateState"/>, <see cref="Entity.UpdatePhysics"/>, <see cref="Entity.Capture"/>
/// and <see cref="Entity.Render"/>), <see cref="Entity.Enabled"/>, <see cref="Entity.Destroy"/> with
/// <see cref="Entity.Dispose"/>, and the timers <see cref="IntervalRunnerSubStep"/> and <see cref="IntervalRunnerFixedStep"/>.
/// </para>
/// <para>
/// In your own game:
/// <code>
/// public sealed class Spin : GameComponent
/// {
///     private TransformComponent2D transform = null!;
///
///     // Render thread, once, before its first turn. Parent is set by now
///     public override void Initialize() => transform = Parent.GetComponent&lt;TransformComponent2D&gt;()!;
///
///     // Simulation thread, the same dt every step
///     public override void UpdatePhysics(float dt) => transform.Rotation += 90.0f * dt;
/// }
///
/// // In your scene's Initialize
/// var crate = batch.AddEntity(new Sprite(new Vector2(32)));
/// crate.ConfigureAtlas(atlas, sheet, "star");
/// crate.AddComponent(new Spin());
/// batch.Add(crate);
/// </code>
/// </para>
/// </summary>
public class EntitiesExample : Scene, ITestControls
{
    // The camera and the UI both see exactly this much of the world, so a spot in the UI is the same spot in the world
    private static readonly Vector2 DesignSize = new(1600, 900);

    private static readonly Vector4 PanelColour = new(0.1f, 0.12f, 0.17f, 0.92f);
    private static readonly Vector4 TreeColour = new(0.75f, 0.85f, 1.0f, 1.0f);
    private static readonly Vector4 DimColour = new(0.93f, 0.95f, 1.0f, 0.6f);

    // How often a wave hatches, how many bugs a switched on hive gets out of one (or out of a burst), and how long
    // (in seconds) a bug lives
    private const float WAVE_EVERY = 0.5f;
    private const int BUGS_PER_WAVE = 2;
    private const int BUGS_PER_BURST = 10;
    private const float BUG_LIFETIME = 6.0f;

    // How often the panel is rewritten. Four times a second is plenty for something you read
    private const float READOUT_EVERY = 0.25f;

    /// <summary>Where a hive sits, its colours, its art and which way its bugs go round.</summary>
    private readonly record struct HiveKind(string Title, Vector2 Position, Vector4 Colour, string Core, string[] Bugs, float Swirl);

    // The art is out of the Dead Revolver pack that ships with the UI, see Assets/uix/dead_revolver/sprites.hor
    private static readonly HiveKind[] Kinds =
    [
        new("Sunny", new Vector2(-400, -165), new Vector4(1.0f, 0.78f, 0.3f, 1.0f), "star", ["star", "star_flat"], 1.0f),
        new("Rosy", new Vector2(130, 165), new Vector4(1.0f, 0.36f, 0.45f, 1.0f), "heart_gem", ["heart", "star_red"], -1.0f),
        new("Bluey", new Vector2(260, -195), new Vector4(0.4f, 0.72f, 1.0f, 1.0f), "heart_gem_blue", ["star_blue", "heart_blue"], 1.0f),
    ];

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Space / A", "hatch a burst of bugs"),
        new("X / B", "destroy every bug"),
        new("1 2 3 / D-pad < ^ >", "switch a hive on or off"),
        new("R / Y", "knock the hives down, build new ones")
    ];

    // Plain old C# objects, no GPU anywhere, so they're fine to make while the scene is constructed. That can be on
    // any thread (the test host happens to do it on the render thread, your game might not), so the one hard rule
    // is nothing that touches GL in a constructor. The atlas only goes near the GPU when a batch first draws out of it
    private readonly SpriteSheetDefinition _sheet = SpriteSheetDefinition.Load("Assets/uix/dead_revolver/", "sprites.hor");
    private readonly TextureAtlas _atlas = new(256, 256);
    private readonly Tally _tally = new();

    private readonly Hive[] _hives = new Hive[Kinds.Length];
    private readonly Label[] _hiveLabels = new Label[Kinds.Length];
    private UICompositor _ui = null!;
    private Label _stats = null!, _tree = null!;

    public override void Initialize()
    {
        // Render thread, with the simulation standing still. Everything added in here is set up (gets its own
        // Initialize) straight after this, in the order it was added, and before anything gets a turn
        var camera = AddEntity(new Camera2D(DesignSize));
        ActiveCamera = camera;

        for (int i = 0; i < Kinds.Length; i++)
            _hives[i] = AddEntity(new Hive(Kinds[i], _atlas, _sheet, _tally));

        // Timers are entities too, so they get their turns (and stop when switched off) like anything else.
        // SubStep keeps whatever time is left over and fires as many times as it's owed, so two waves a second stays
        // two waves a second. FixedStep fires once an update at most and bins the leftover, which is all a readout needs
        AddEntity(new IntervalRunnerSubStep(WAVE_EVERY, () => Hatch(BUGS_PER_WAVE)));
        AddEntity(new IntervalRunnerFixedStep(READOUT_EVERY, RefreshReadout));

        // A component on the scene itself, drawn after the scene's children so the UI sits on top of the hives
        _ui = AddComponent(new UICompositor(camera) { DesignSize = DesignSize });
        BuildPanel();
        RefreshReadout();

        Engine.GL.ClearColor(0.12f, 0.13f, 0.18f, 1.0f);

        // Don't skip this, it's what runs PostInit. Mind that PostInit is called from in here, so it runs before
        // anything you just added has been set up
        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        // Simulation thread, 120 times a second unless the engine's set up otherwise. Keys first, then
        // base.UpdateState hands every component and child its turn. Destroying goes first, so a bug never gets
        // binned in the same tick it hatched in (see Squash for why that matters)
        if (Pressed(Key.X, GamepadInput.B))
        {
            foreach (Hive hive in _hives)
                hive.DestroyBugs();
        }

        if (Pressed(Key.R, GamepadInput.Y))
            RebuildHives();

        if (Pressed(Key.Space, GamepadInput.A))
            Hatch(BUGS_PER_BURST);

        if (Pressed(Key.Number1, GamepadInput.DPadLeft)) Toggle(_hives[0]);
        if (Pressed(Key.Number2, GamepadInput.DPadUp)) Toggle(_hives[1]);
        if (Pressed(Key.Number3, GamepadInput.DPadRight)) Toggle(_hives[2]);

        base.UpdateState(dt);
    }

    /// <summary>
    /// Helper method to hatch some bugs in every hive that's switched on. Simulation thread.
    /// </summary>
    private void Hatch(int perHive)
    {
        foreach (Hive hive in _hives)
        {
            // Enabled only stops the engine giving the hive its turns, it doesn't stop you calling it, so check.
            // It also reads false till the hive has been set up, so a brand new hive sits out its first wave :)
            if (hive.Enabled)
                hive.Spawn(perHive, BUG_LIFETIME);
        }
    }

    /// <summary>
    /// Helper method to switch a hive on or off. Simulation thread, though Enabled is fine to flip from anywhere.
    /// </summary>
    private static void Toggle(Hive hive)
    {
        // Not set up yet (it was only just rebuilt). Setting it up switches it on anyway, so leave it be
        if (!hive.IsInitialized)
            return;

        // Off means no turns at all for the hive and everything in it: not updated (the bugs freeze mid air), not
        // captured and so not drawn. Back on and it carries on exactly where it left off
        hive.Enabled = !hive.Enabled;
    }

    /// <summary>
    /// Helper method to destroy every hive and put a fresh one in its place. Simulation thread.
    /// </summary>
    private void RebuildHives()
    {
        for (int i = 0; i < _hives.Length; i++)
        {
            // Destroy is fine from any thread. The hive's out of the tree and gets no more turns from right now, and
            // it's disposed of on the render thread at the start of the next frame, which is when its glow texture
            // goes (see Hive.DisposeOther). Keep an eye on the texture count in the panel
            _hives[i].Destroy();

            // Made right here on the simulation thread, which is exactly why Hive keeps its GL stuff out of its constructor
            _hives[i] = AddEntity(new Hive(Kinds[i], _atlas, _sheet, _tally));
        }
    }

    /// <summary>
    /// Helper method to get rid of a bug for good. Simulation thread.
    /// </summary>
    private static void Squash(Sprite bug)
    {
        // Gotcha: a SpriteBatch keeps its own list of what it draws, apart from its children, and only picks a sprite
        // up into that list in its Capture, at the end of a tick. Destroy takes the sprite out of the tree and switches
        // it off, but only Remove takes it off the batch's list, so do both or the batch hangs on to dead sprites
        // forever, which is a shit way to leak. Remove can't take back one the batch hasn't picked up yet either,
        // hence the keys destroying before anything gets hatched
        bug.Batch?.Remove(bug);
        bug.Destroy();
    }

    /// <summary>
    /// Helper method for a key, or a button on whichever gamepad was used last, going down this update.
    /// </summary>
    private static bool Pressed(Key key, GamepadInput button) =>
        Engine.Input.Keyboard.WasPressed(key) || Engine.Input.Gamepads.LastUsed?.WasPressed(button) == true;

    /// <summary>
    /// Helper method to put the panel up on the left, and a label under every hive.
    /// </summary>
    private void BuildPanel()
    {
        var module = _ui.CreateModule();

        var panel = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(24, -24),
            Color = PanelColour,
            Padding = new UIEdges(18),
            Spacing = 10
        });

        panel.Add(new Label("Entities and components") { Anchor = Origin.Left, TextScale = 0.36f });
        _stats = panel.Add(new Label { Anchor = Origin.Left, Align = Origin.TopLeft, TextScale = 0.25f });
        panel.Add(new Label("The scene's tree, live:") { Anchor = Origin.Left, TextScale = 0.22f, Color = DimColour });
        _tree = panel.Add(new Label { Anchor = Origin.Left, Align = Origin.TopLeft, TextScale = 0.23f, Color = TreeColour });

        for (int i = 0; i < Kinds.Length; i++)
            _hiveLabels[i] = module.AddComponent(new Label { Position = Kinds[i].Position + new Vector2(0, -175), TextScale = 0.25f });
    }

    /// <summary>
    /// Helper method to rewrite the panel from the tree as it is right now. Children and Components hand back copies
    /// that are swapped whole whenever something's added or removed, so walking them is safe from any thread.
    /// </summary>
    private void RefreshReadout()
    {
        int flying = 0;
        foreach (Hive hive in _hives)
            flying += hive.BugCount;

        (int entities, int components) = Count(this);
        int made = Volatile.Read(ref _tally.GlowsMade), freed = Volatile.Read(ref _tally.GlowsFreed);
        int hatched = Volatile.Read(ref _tally.Hatched);

        _stats.Text =
            $"{entities} entities and {components} components in the scene\n" +
            $"Bugs: {flying} flying, {hatched} hatched, {hatched - flying} gone\n" +
            $"Glow textures on the GPU: {made - freed} (made {made}, freed {freed})";

        var tree = new StringBuilder();
        Describe(this, tree, 0);
        _tree.Text = tree.ToString().TrimEnd();

        for (int i = 0; i < _hives.Length; i++)
        {
            Hive hive = _hives[i];
            bool off = hive.IsInitialized && !hive.Enabled;

            _hiveLabels[i].Text = off ? $"[{i + 1}] {hive.Title} is switched off" : $"[{i + 1}] {hive.Title}: {hive.BugCount} bugs";
            _hiveLabels[i].Color = off ? DimColour : Vector4.One;
        }
    }

    /// <summary>
    /// Helper method to count an entity, everything under it, and the components on all of them.
    /// </summary>
    private static (int Entities, int Components) Count(Entity entity)
    {
        int entities = 1, components = entity.Components.Count;
        foreach (Entity child in entity.Children)
        {
            (int e, int c) = Count(child);
            entities += e;
            components += c;
        }

        return (entities, components);
    }

    /// <summary>
    /// Helper method to write an entity and everything under it out a line each. A run of childless children that read
    /// the same gets folded into one "24 x" line, or the bugs would scroll off the bottom of the screen.
    /// </summary>
    private static void Describe(Entity entity, StringBuilder text, int depth)
    {
        text.Append(' ', depth * 2).Append(Line(entity)).Append('\n');

        IReadOnlyList<Entity> children = entity.Children;
        for (int i = 0; i < children.Count;)
        {
            if (children[i].Children.Count > 0)
            {
                Describe(children[i], text, depth + 1);
                i++;
                continue;
            }

            string line = Line(children[i]);
            int run = 1;
            while (i + run < children.Count && children[i + run].Children.Count == 0 && Line(children[i + run]) == line)
                run++;

            text.Append(' ', (depth + 1) * 2);
            if (run > 1) text.Append(run).Append(" x ");
            text.Append(line).Append('\n');
            i += run;
        }
    }

    /// <summary>
    /// Helper method for an entity's line in the tree: its name, its components and how it's doing.
    /// </summary>
    private static string Line(Entity entity)
    {
        var line = new StringBuilder(entity.Name.Length > 0 ? entity.Name : entity.GetType().Name);

        if (entity.Components.Count > 0)
            line.Append(" [").AppendJoin(", ", entity.Components.Select(component => component.Name)).Append(']');

        if (!entity.IsInitialized)
            line.Append("  (not set up yet)");
        else if (!entity.Enabled)
            line.Append("  (switched off)");

        return line.ToString();
    }

    /// <summary>
    /// Helper class to count what the panel shows. Bumped on both threads, hence the Interlocked and Volatile.
    /// </summary>
    private sealed class Tally
    {
        public int Hatched, GlowsMade, GlowsFreed;
    }

    /// <summary>
    /// A nest of bugs, and an entity of our own from top to bottom: it holds a <see cref="SpriteBatch"/> that parents
    /// (and draws) its bugs, makes a glow texture in Initialize, works out how bright it is in UpdateState, publishes
    /// that in Capture, draws it in Render and frees the texture again in DisposeOther.
    /// </summary>
    private sealed class Hive : GameObject
    {
        private const float CORE_SCALE = 5.0f, BUG_SCALE = 2.0f;
        private const int STARTERS = 5;

        // How the glow texture is painted, in texels, and how big it's drawn, in units of the world
        private const int GLOW_TEXELS = 128;
        private const float GLOW_SIZE = 330.0f;

        private readonly HiveKind _kind;
        private readonly TextureAtlas _atlas;
        private readonly SpriteSheetDefinition _sheet;
        private readonly Tally _tally;

        private readonly SpriteBatch _batch;
        private readonly Sprite _core;

        /// <summary>
        /// How the hive glows as of one tick. Blendable, so a frame drawn between two ticks gets something in between.
        /// </summary>
        private readonly record struct Glow(float Size, float Strength) : IBlendable<Glow>
        {
            public static Glow Blend(in Glow from, in Glow to, float amount) =>
                new(Interpolate.Linear(from.Size, to.Size, amount), Interpolate.Linear(from.Strength, to.Strength, amount));
        }

        // The render thread's: made in Initialize, freed in DisposeOther, and the simulation never touches it
        private Texture _glowTexture = Texture.Invalid;

        // How it glows right now (the simulation's), and as of the end of every tick (what frames are drawn from)
        private readonly Snapshot<Glow> _glow = new();
        private Glow _glowNow = new(GLOW_SIZE, 0.25f);
        private float _pulse;

        public string Title => _kind.Title;

        /// <summary>How many bugs it had as of its last update.</summary>
        public int BugCount { get; private set; }

        public Hive(HiveKind kind, TextureAtlas atlas, SpriteSheetDefinition sheet, Tally tally)
        {
            _kind = kind;
            _atlas = atlas;
            _sheet = sheet;
            _tally = tally;

            Name = $"Hive {kind.Title}";

            // Children can go in from the constructor too. Nothing here touches the GPU, and they're only set up
            // once the hive is, right after it and in this order
            _batch = AddEntity(new SpriteBatch());

            _core = _batch.AddEntity(new Sprite(SizeOf(kind.Core) * CORE_SCALE));
            _core.ConfigureAtlas(atlas, sheet, kind.Core);
            _core.Transform.Position = kind.Position;
            _batch.Add(_core);
            _core.PopIn(0.6f);

            Spawn(STARTERS, BUG_LIFETIME);
        }

        public override void Initialize()
        {
            // Render thread, so this is where GPU stuff gets made. Never in the constructor: R builds hives on the
            // simulation thread, and GL calls off the render thread break in ways that are a pain in the arse to find
            _glowTexture = Texture.Create(GLOW_TEXELS, GLOW_TEXELS, TextureDefinition.RgbaUnsignedByte);
            Engine.GL.TextureSubImage2D<byte>(
                _glowTexture.Handle, 0, 0, 0, GLOW_TEXELS, GLOW_TEXELS, PixelFormat.Rgba, PixelType.UnsignedByte, PaintGlow(_kind.Colour));
            Interlocked.Increment(ref _tally.GlowsMade);

            base.Initialize();
        }

        public override void UpdateState(float dt)
        {
            // This is where the batch and the bugs in it (and their components) get their turns. Leave it out and
            // the whole hive's a statue
            base.UpdateState(dt);

            int bugs = 0;
            foreach (Entity child in _batch.Children)
            {
                if (child.TryGetComponent<Lifetime>(out _)) bugs++;
            }
            BugCount = bugs;

            // Breathes in and out, and glows brighter the busier it is
            _pulse += dt;
            _glowNow = new Glow(GLOW_SIZE * (1.0f + 0.05f * MathF.Sin(_pulse * 2.5f)), 0.25f + 0.55f * MathF.Min(1.0f, bugs / 25.0f));
        }

        public override void Capture()
        {
            // Simulation thread, at the end of every tick. Frames are drawn while the next ticks are already running,
            // so Render can't go reading _glowNow (it could be halfway through changing). It reads what's published
            // here instead, blended between the last two ticks so it's smooth at any frame rate
            _glow.Publish(_glowNow);

            // And this publishes the children and components. Skip it and nothing in the hive gets drawn, and you'll
            // lose a bloody hour working out why
            base.Capture();
        }

        public override void Render(float dt)
        {
            // Render thread. Ours goes first so the glow sits behind the bugs, then base.Render has the batch draw them
            if (_glowTexture.IsValid && TryGetGlow(out Glow glow))
            {
                Vector2 half = new(glow.Size * 0.5f);
                ReadOnlySpan<SpriteItem> quad =
                [
                    SpriteItem.Rectangle(_kind.Position - half, _kind.Position + half, Vector2.Zero, _glowTexture.Size,
                        SpriteItem.PackColor(new Vector4(1.0f, 1.0f, 1.0f, glow.Strength)), 0)
                ];

                // The batch doesn't set any blending itself, it draws with whatever's set, and out of the box that's
                // none (pixel art only needs its see-through bits thrown out). A soft glow wants alpha blending, and
                // whatever GL state you change in a Render you put back how you found it
                RenderState.Saved before = RenderState.Save();
                RenderState.Blend = true;
                RenderState.BlendMode = BlendMode.Alpha;

                // A batch draws whatever quads you hand it, not just its sprites. The 0 in the flags is the texture slot
                _batch.Draw(quad, [new SpriteTexture(_glowTexture)]);

                RenderState.Restore(before);
            }

            base.Render(dt);
        }

        protected override void DisposeOther()
        {
            // Render thread, at the start of the frame after Destroy (or whenever the scene's left). Free what you
            // made in Initialize here. Forget and the scene's Assets sweep it up when the scene goes, but that's a
            // safety net, not a plan: something that builds and bins hives all night leaks a texture every time till then
            if (!_glowTexture.IsValid)
                return;

            _glowTexture.Dispose();
            _glowTexture = Texture.Invalid;
            Interlocked.Increment(ref _tally.GlowsFreed);
        }

        /// <summary>
        /// Hatches bugs in the middle of the hive. Simulation thread, or anywhere before the hive's been set up.
        /// </summary>
        public void Spawn(int count, float lifetime)
        {
            for (int i = 0; i < count; i++)
            {
                string art = _kind.Bugs[Random.Shared.Next(_kind.Bugs.Length)];

                var bug = new Sprite(SizeOf(art) * BUG_SCALE) { Smooth = true };
                bug.ConfigureAtlas(_atlas, _sheet, art);
                bug.Transform.Position = _kind.Position;

                // The components are what make it a bug. Orbit flies it about, Lifetime bins it when its time's up,
                // and the sprite doesn't know or care about either of them
                float angle = Random.Shared.NextSingle() * MathF.Tau;
                bug.AddComponent(new Orbit(_kind.Position, new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 260.0f, _kind.Swirl));
                bug.AddComponent(new Lifetime(lifetime));

                // Added to something that's already set up, it gets set up on the render thread at the start of the
                // next frame and has no turns till then. The simulation parks at the end of the tick while that
                // happens (a rendezvous), which costs a bit every time, hence hatching in waves rather than one a tick
                _batch.AddEntity(bug);

                // The batch is both what updates the bug (its parent) and what draws it, and those are two lists
                _batch.Add(bug);
            }

            Interlocked.Add(ref _tally.Hatched, count);

            // A blink on the core so you can see which hive just hatched. Flash has a tween channel of its own, so
            // it can't cut the PopIn short the way another size tween would
            _core.Flash(Vector4.One, 0.35f, 0.6f);
        }

        /// <summary>
        /// Destroys every bug in the hive, switched on or not. Simulation thread.
        /// </summary>
        public void DestroyBugs()
        {
            // Children is a copy that's swapped whole when something's removed, so binning things while walking it is fine
            foreach (Entity child in _batch.Children)
            {
                if (child is Sprite bug && child.TryGetComponent<Lifetime>(out _))
                    Squash(bug);
            }
        }

        /// <summary>
        /// Helper method to find how the hive glows in the frame that's being drawn. Render thread.
        /// </summary>
        private bool TryGetGlow(out Glow glow)
        {
            RenderFrame frame = RenderFrame.Active;

            // The normal case: draw from what Capture published, blended to the moment this frame shows
            if (frame.IsDecoupled)
                return _glow.TryBlend(frame, out glow);

            // The simulation's standing still (the scene is being set up and warmed up, or there hasn't been a tick
            // yet), so the live state is safe to read
            glow = _glowNow;
            return true;
        }

        /// <summary>
        /// Helper method to look up how big a piece of art is, in pixels.
        /// </summary>
        private Vector2 SizeOf(string art) =>
            _sheet.TryGetSprite(art, null, out SpriteSource source) ? new Vector2(source.Width, source.Height) : new Vector2(16.0f);

        /// <summary>
        /// Helper method to paint the glow: white hot in the middle, the hive's colour further out, fading to nothing
        /// at the edge. Every hive bakes its own colour in, which is why each one has a texture of its own.
        /// </summary>
        private static byte[] PaintGlow(Vector4 colour)
        {
            var pixels = new byte[GLOW_TEXELS * GLOW_TEXELS * 4];
            var middle = new Vector2(GLOW_TEXELS * 0.5f);
            var tint = new Vector3(colour.X, colour.Y, colour.Z);

            for (int y = 0; y < GLOW_TEXELS; y++)
            {
                for (int x = 0; x < GLOW_TEXELS; x++)
                {
                    float fromMiddle = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), middle) / middle.X;
                    float alpha = MathF.Max(0.0f, 1.0f - fromMiddle);
                    Vector3 rgb = Vector3.Lerp(Vector3.One, tint, MathF.Min(1.0f, fromMiddle * 3.0f)) * 255.0f;

                    int at = (y * GLOW_TEXELS + x) * 4;
                    pixels[at] = (byte)rgb.X;
                    pixels[at + 1] = (byte)rgb.Y;
                    pixels[at + 2] = (byte)rgb.Z;
                    pixels[at + 3] = (byte)(alpha * alpha * 255.0f);
                }
            }

            return pixels;
        }
    }

    /// <summary>
    /// Flies whatever it's on round and round a point, settling into an orbit of its own. Movement you integrate
    /// (velocity into position) belongs in UpdatePhysics: that always gets the same dt, so it comes out the same
    /// however busy the machine is.
    /// </summary>
    private sealed class Orbit(Vector2 home, Vector2 kick, float swirl) : GameComponent
    {
        private readonly float _radius = 60.0f + Random.Shared.NextSingle() * 85.0f;
        private readonly float _speed = 90.0f + Random.Shared.NextSingle() * 70.0f;
        private readonly float _phase = Random.Shared.NextSingle() * MathF.Tau;

        private TransformComponent2D _transform = null!;
        private Vector2 _velocity = kick;
        private float _time;

        public override void Initialize()
        {
            // Render thread, once, before the first turn. AddComponent set Parent, so it's there by now (it isn't in
            // the constructor), which makes this the place to grab whatever sibling components you need
            _transform = Parent.GetComponent<TransformComponent2D>()!;
        }

        public override void UpdatePhysics(float dt)
        {
            _time += dt;

            Vector2 offset = _transform.Position - home;
            float distance = MathF.Max(offset.Length(), 0.001f);
            Vector2 outward = offset / distance;
            Vector2 around = new Vector2(-outward.Y, outward.X) * swirl;

            // Round and round at its own speed, pulled in or out towards a radius that breathes a little
            float radius = _radius + MathF.Sin(_time * 1.3f + _phase) * 16.0f;
            Vector2 wanted = around * _speed + outward * (radius - distance) * 2.5f;
            _velocity = Vector2.Lerp(_velocity, wanted, MathF.Min(1.0f, 3.0f * dt));

            _transform.Position += _velocity * dt;

            // Lean into the turn a bit
            _transform.Rotation = Math.Clamp(-_velocity.X * 0.12f, -30.0f, 30.0f);
        }
    }

    /// <summary>
    /// Counts down and gets rid of whatever it's on when the time's up. Plain game logic, so it lives in UpdateState.
    /// </summary>
    private sealed class Lifetime(float seconds) : GameComponent
    {
        private float _left = seconds;

        public override void UpdateState(float dt)
        {
            if (_left <= 0.0f)
                return;

            _left -= dt;
            if (_left > 0.0f)
                return;

            // Time's up: shrink out, then bin it. The tween is ticked by the sprite's own UpdateState, so OnComplete
            // fires on the simulation thread as well. Everything in a wave has the same lifetime, so they all go in
            // the same tick, and that's one rendezvous for the lot rather than one each
            var bug = (Sprite)Parent;
            bug.PopOut(0.3f).OnComplete(() => Squash(bug));
        }
    }
}
