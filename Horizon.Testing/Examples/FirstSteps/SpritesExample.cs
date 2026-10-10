using System.Numerics;

using Horizon.Core.Components;
using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.Input;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.FirstSteps;

/// <summary>
/// Sprites, the bread and butter of any 2D game. A lil blob hops back and forth along the ground, a big one in the
/// middle shows off a new trick every couple of seconds, and a row of small ones each show one thing a sprite can do.
/// The art is two pictures in Assets/examples/sprites, a sheet of blob and a sheet of scenery with a file next to
/// it that says what is where.
/// <para>
/// What to look at: <see cref="SpriteBatch"/> (draws a pile of sprites in a call or two), <see cref="Sprite"/> and its
/// <see cref="Sprite.Transform"/> (a <see cref="TransformComponent2D"/>: position, size, origin, rotation),
/// <see cref="SpriteSheet"/> with named animations on a <see cref="SpriteSheetAnimationManager"/>
/// (<see cref="Sprite.AddAnimation"/>, <see cref="Sprite.SetAnimation"/>, and <see cref="SpriteSheetAnimationManager.SetFrame"/>
/// with <see cref="SpriteSheetAnimationManager.AnimateFrames"/> off for frames you pick yourself), <see cref="Sprite.Flipped"/>,
/// <see cref="Sprite.Tint"/>, <see cref="Sprite.Smooth"/>, the ready made <see cref="SpriteTweens"/>, and sprites out of a
/// <see cref="TextureAtlas"/> described by a <see cref="SpriteSheetDefinition"/>. It's all drawn through a
/// <see cref="Renderer2D"/>, and the batches are drawn in the order they were added to it.
/// </para>
/// <para>
/// In your own game:
/// <code>
/// // Initialize, render thread: the sheet is a texture, so it can't be made any earlier than this
/// var sheet = SpriteSheet.FromTexture(texture, new Vector2(16));     // 16 by 16 pixels a frame
/// var batch = AddEntity(new SpriteBatch());
///
/// var blob = batch.AddEntity(new Sprite(new Vector2(96)));           // AddEntity so it gets updated...
/// blob.ConfigureSpriteSheet(sheet, "idle");
/// blob.AddAnimation("idle", new Vector2(0, 0), 4, 0.15f);            // first frame at column 0, row 0, 4 frames long
/// blob.AddAnimation("hop", new Vector2(0, 1), 6);
/// batch.Add(blob);                                                   // ...and Add so it gets drawn
///
/// // UpdateState, simulation thread
/// blob.SetAnimation("hop");
/// blob.Flipped = true;
/// blob.Punch();
/// </code>
/// </para>
/// </summary>
public class SpritesExample : Scene, ITestControls
{
    // The camera and the UI both see exactly this much of the world, so a spot in the UI is the same spot in the world
    private static readonly Vector2 DesignSize = new(1600, 900);

    private static readonly Vector4 Sky = new(0.29f, 0.45f, 0.66f, 1.0f);
    private static readonly Vector4 PanelColour = new(0.1f, 0.12f, 0.17f, 0.88f);
    private static readonly Vector4 CaptionColour = new(1.0f, 1.0f, 1.0f, 0.9f);
    private static readonly Vector4 MarkerColour = new(1.0f, 0.2f, 0.25f, 1.0f);

    // One frame of blob is this many pixels square, and the sheet is a grid of them
    internal const int CELL = 16;

    // Where the top of the grass is. Everything that stands on the ground stands here
    private const float GROUND = -300.0f;

    // How the little blob gets about: how long one hop takes, how far and how high it goes, and how long it has a
    // breather at either end before turning round
    private const float HOP_TIME = 0.5f;
    private const float HOP_DISTANCE = 110.0f;
    private const float HOP_HEIGHT = 100.0f;
    private const float REST_TIME = 1.2f;
    private const float HOP_LIMIT = 680.0f;

    // The big blob does its next trick this often, in seconds
    private const float TRICK_EVERY = 2.4f;

    // Where the row of small blobs sits
    private const float ROW = 120.0f;

    // The blob's animations: which row of the sheet each is on, how many frames and how long each frame is up for
    private static readonly (string Name, int Row, uint Frames, float FrameTime)[] Animations =
    [
        ("idle", 0, 4, 0.18f),
        ("hop", 1, 6, 0.1f),
        ("blink", 2, 8, 0.12f)
    ];

    // The tricks the big blob cycles through on its own, in order
    private static readonly string[] Tricks =
        ["Punch()", "Flash()", "Shake()", "PopIn()", "TweenTint()", "TweenRotation()", "Flipped", "SetAnimation()"];

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Space / A", "burst of tweens"),
        new("F / Y", "flip the big blob"),
        new("N / X", "next animation")
    ];

    private readonly UICompositor _ui;
    // Where the art is, from the folder the game runs in. blob.png is a grid of frames, props.png has props.hor to explain it
    private const string ART_DIRECTORY = "Assets/examples/sprites";

    private readonly SpriteSheetDefinition _props;
    private readonly TextureAtlas _atlas = new(128, 128);

    private Sprite _hero = null!, _big = null!;
    private readonly List<Sprite> _clouds = [], _coins = [];
    private Label _bigCaption = null!;

    private float _heroX = -HOP_LIMIT, _hopTime, _restLeft;
    private int _heroDirection = 1;

    private float _trickTimer;
    private int _trick, _animation;
    private bool _bigFacingLeft;
    private string _lastTrick = "nothing yet";

    public SpritesExample()
    {
        var camera = AddEntity(new Camera2D(DesignSize));
        ActiveCamera = camera;

        // UI components own nothing on the GPU (the compositor makes its bits in its own Initialize), so the whole UI
        // can be built right here
        _ui = AddComponent(new UICompositor(camera) { DesignSize = DesignSize });

        // Reading what's in a sheet is just a file, no GPU anywhere, so it's fine in the constructor too. Only
        // turning the pictures into textures has to wait for Initialize
        _props = SpriteSheetDefinition.Load(ART_DIRECTORY, "props.hor");

        BuildCaptions();
    }

    public override void Initialize()
    {
        // Render thread, with the simulation parked: the one spot where it's safe to make GPU stuff. Everything made
        // in here (and while the scene draws) is noted in Scene.Assets and freed when the scene is left, so there's
        // no cleaning up to do by hand
        var renderer = AddEntity(new Renderer2D((uint)DesignSize.X, (uint)DesignSize.Y) { ClearColor = Sky });

        // A batch is drawn in one go, and the batches in the order they went into the renderer. That's your layers
        // sorted: the scenery at the back, the stage, then whatever has to be in front of the lot.
        // ZOffset on a transform won't help you here, it only means anything with the depth test on and a
        // Renderer2D turns that off. Within one batch don't count on any order, if it matters give it another batch
        var scenery = renderer.AddEntity(new SpriteBatch());
        var stage = renderer.AddEntity(new SpriteBatch());
        var front = renderer.AddEntity(new SpriteBatch());

        if (!Engine.ObjectManager.Textures.TryCreate(
                new TextureDescription
                {
                    Paths = [Path.Combine(ART_DIRECTORY, "blob.png")],

                    // Nearest, or your crisp pixel art comes out as a blurry pile of shit
                    Definition = TextureDefinition.RgbaUnsignedByteNearest
                },
                out var texture))
        {
            throw new Exception($"Couldn't make the blob texture: {texture.Message}");
        }

        // A sprite sheet is a texture cut up into a grid of frames, CELL pixels square
        var sheet = SpriteSheet.FromTexture(texture.Asset, new Vector2(CELL));

        BuildScenery(scenery, stage);

        // The big one, standing on the ground in the middle. Origin.Bottom means Position is where its feet are,
        // so it squashes and wobbles about its feet instead of its middle
        _big = CreateBlob(stage, sheet, new Vector2(0, GROUND), 12);
        _big.Transform.Origin = Origin.Bottom;

        BuildSpecimens(stage, front, sheet);

        // The hopper goes in the front batch, so it walks past in front of the big one rather than behind it
        _hero = CreateBlob(front, sheet, new Vector2(_heroX, GROUND), 6);
        _hero.Transform.Origin = Origin.Bottom;

        // The hop is picked frame by frame below, so the squash always lines up with it landing.
        // AnimateFrames is for the whole manager, every animation of the sprite stops moving along on its own
        _hero.AnimationManager.AnimateFrames = false;
        _hero.SetAnimation("hop");

        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        // Simulation thread. Children first: the sprites move their tweens and animations along in their own updates
        base.UpdateState(dt);

        UpdateHero(dt);
        UpdateScenery(dt);

        _trickTimer += dt;
        if (_trickTimer >= TRICK_EVERY)
        {
            _trickTimer = 0.0f;
            DoTrick(_trick);
            _trick = (_trick + 1) % Tricks.Length;
        }

        if (Pressed(Key.Space, GamepadInput.A))
        {
            Burst();
            _trickTimer = 0.0f;
        }

        if (Pressed(Key.F, GamepadInput.Y))
            DoTrick(Array.IndexOf(Tricks, "Flipped"));

        if (Pressed(Key.N, GamepadInput.X))
            DoTrick(Array.IndexOf(Tricks, "SetAnimation()"));
    }

    /// <summary>
    /// Helper method to move the little blob along: hop after hop to one end, a rest, turn round, and back again.
    /// Its frames are picked by hand from how far through the hop it is. Simulation thread.
    /// </summary>
    private void UpdateHero(float dt)
    {
        if (_restLeft > 0.0f)
        {
            _restLeft -= dt;
            if (_restLeft > 0.0f)
                return;

            // Turn round. The art faces right, so flipped is facing left. Flipping is a jump, not something the
            // batch slides between from one tick to the next (it'd squash it flat halfway), so it just happens
            _heroDirection = -_heroDirection;
            _hero.Flipped = _heroDirection < 0;

            // Back to picking frames ourselves
            _hero.AnimationManager.AnimateFrames = false;
            _hero.SetAnimation("hop");
        }

        _hopTime += dt;
        if (_hopTime >= HOP_TIME)
        {
            _hopTime -= HOP_TIME;
            _heroX += _heroDirection * HOP_DISTANCE;

            if (MathF.Abs(_heroX + _heroDirection * HOP_DISTANCE) > HOP_LIMIT)
            {
                // Out of room, have a breather. The idle animation plays itself, so the manager gets to drive again
                _restLeft = REST_TIME;
                _hopTime = 0.0f;
                _hero.AnimationManager.AnimateFrames = true;
                _hero.SetAnimation("idle");
                _hero.Transform.Position = new Vector2(_heroX, GROUND);
                return;
            }
        }

        // Crouch for the first sixth of the hop, land in the last, and it's in the air for everything in between
        float through = _hopTime / HOP_TIME;
        float air = Math.Clamp((through - 1.0f / 6.0f) / (4.0f / 6.0f), 0.0f, 1.0f);

        _hero.AnimationManager.SetFrame("hop", (uint)Math.Min(5, through * 6.0f));
        _hero.Transform.Position = new Vector2(
            _heroX + _heroDirection * HOP_DISTANCE * air,
            GROUND + HOP_HEIGHT * 4.0f * air * (1.0f - air));
    }

    /// <summary>
    /// Helper method to drift the clouds along and bob the coins. Simulation thread.
    /// </summary>
    private void UpdateScenery(float dt)
    {
        for (int i = 0; i < _clouds.Count; i++)
        {
            var transform = _clouds[i].Transform;
            Vector2 position = transform.Position + new Vector2(18.0f + 9.0f * i, 0.0f) * dt;

            if (position.X > DesignSize.X / 2.0f + 120.0f)
            {
                // Back round to the left. Snap() says it was put there, not moved there, so the frames between this
                // tick and the last don't draw it zipping across the whole sky. Any teleport wants one of these
                position.X -= DesignSize.X + 240.0f;
                transform.Snap();
            }

            transform.Position = position;
        }

        for (int i = 0; i < _coins.Count; i++)
        {
            var transform = _coins[i].Transform;
            transform.Position = transform.Position with { Y = 330.0f + 10.0f * MathF.Sin((float)Time * 2.5f + i * 0.8f) };
        }
    }

    /// <summary>
    /// Helper method to have the big blob do one of its tricks. Simulation thread.
    /// </summary>
    private void DoTrick(int trick)
    {
        // Every one of these starts from wherever the blob is right now. Punch() settles back to the size it has
        // now, Shake() to where it is now and so on. Start one while the last is still wobbling and it settles
        // somewhere stupid and stays there, looking like arse. CompleteAll() puts it back at rest first, every time
        _big.Tweens.CompleteAll();

        switch (trick)
        {
            case 0:
                _big.Punch(0.25f, 0.35f);
                break;

            case 1:
                // Unlike a tint (which only ever darkens) a flash paints over the sprite, so white actually shows
                _big.Flash(Vector4.One, 0.5f);
                break;

            case 2:
                _big.Shake(14.0f, 0.4f);
                break;

            case 3:
                _big.PopIn(0.45f);
                break;

            case 4:
                // There and back: twice, PingPong, ends where it started
                _big.TweenTint(new Vector4(1.0f, 0.45f, 0.5f, 1.0f), 0.35f).SetLoops(2, LoopMode.PingPong);
                break;

            case 5:
                // A lean and back, twice. It turns about its origin, which is its feet
                _big.TweenRotation(12.0f, 0.15f).SetEasing(Easing.OutQuad).SetLoops(4, LoopMode.PingPong);
                break;

            case 6:
                // Flipped just turns the width negative. A size tween that was still going would turn it straight
                // back, which is another reason the CompleteAll() up there earns its keep
                _bigFacingLeft = !_bigFacingLeft;
                _big.Flipped = _bigFacingLeft;
                break;

            case 7:
                // Every animation keeps its own place, so switching to one carries on wherever it was left.
                // SetFrame(name, 0) starts it from the top
                _animation = (_animation + 1) % Animations.Length;
                string name = Animations[_animation].Name;
                _big.SetAnimation(name);
                _big.AnimationManager.SetFrame(name, 0);
                break;
        }

        _lastTrick = Tricks[trick];
        UpdateBigCaption();
    }

    /// <summary>
    /// Helper method to fire off everything at once. Every SpriteTweens tween is on a channel of its own (position,
    /// size, rotation, tint, flash), so these all run together instead of killing each other. Simulation thread.
    /// </summary>
    private void Burst()
    {
        _big.Tweens.CompleteAll();
        _big.Punch(0.35f, 0.45f);
        _big.Flash(new Vector4(1.0f, 0.95f, 0.6f, 1.0f), 0.6f);
        _big.Shake(18.0f, 0.5f);
        _big.TweenTint(new Vector4(0.6f, 0.8f, 1.0f, 1.0f), 0.3f).SetLoops(2, LoopMode.PingPong);
        _big.TweenRotation(-10.0f, 0.12f).SetLoops(4, LoopMode.PingPong);

        // And the coins pop back in one after another, each with a bit more delay than the last
        for (int i = 0; i < _coins.Count; i++)
        {
            _coins[i].Tweens.CompleteAll();
            _coins[i].PopIn(0.4f, 0.06f * i);
        }

        _lastTrick = "the bloody lot";
        UpdateBigCaption();
    }

    /// <summary>
    /// Helper method to make a blob off the sheet, with all of its animations, and put it in a batch.
    /// Render thread, the sheet has to exist.
    /// </summary>
    /// <param name="scale">How many units of the world one pixel of the art takes up. Whole numbers keep it crisp.</param>
    private static Sprite CreateBlob(SpriteBatch batch, SpriteSheet sheet, Vector2 position, int scale)
    {
        // AddEntity is what gets the sprite updated (its tweens and animations move along in there), Add is what
        // gets it drawn. You want both, and forgetting either is a classic
        var blob = batch.AddEntity(new Sprite(new Vector2(CELL * scale)));
        blob.Transform.Position = position;

        // ConfigureSpriteSheet first: it's what gives the sprite its animation manager, AddAnimation without one
        // falls over. The name is the animation it starts on
        blob.ConfigureSpriteSheet(sheet, "idle");

        // The position of an animation is the column and row of its first frame, in cells of the sheet, not
        // pixels. The rest of its frames carry on to the right
        foreach (var (name, row, frames, frameTime) in Animations)
            blob.AddAnimation(name, new Vector2(0, row), frames, frameTime);

        batch.Add(blob);
        return blob;
    }

    /// <summary>
    /// Helper method to lay the ground and put clouds in the sky (the scenery batch), and a row of coins up there in
    /// front of them (the stage batch). All of it out of the atlas. Render thread.
    /// </summary>
    private void BuildScenery(SpriteBatch scenery, SpriteBatch stage)
    {
        // Clouds and ground share a batch, which is fine because they never overlap. Within one batch the order
        // things are drawn in isn't yours to pick
        for (int i = 0; i < 4; i++)
        {
            var size = new Vector2(32, 16) * (4 + i % 2);
            _clouds.Add(CreateProp(scenery, "cloud", new Vector2(-620.0f + i * 430.0f, 210.0f + (i % 2) * 140.0f), size));
        }

        // Ground: a row of grass on top, dirt down to the bottom of the screen. 16 pixel tiles drawn four times over
        for (float x = -DesignSize.X / 2.0f; x < DesignSize.X / 2.0f; x += 64.0f)
        {
            CreateProp(scenery, "grass", new Vector2(x + 32.0f, GROUND - 32.0f), new Vector2(64));
            CreateProp(scenery, "dirt", new Vector2(x + 32.0f, GROUND - 96.0f), new Vector2(64));
            CreateProp(scenery, "dirt", new Vector2(x + 32.0f, GROUND - 160.0f), new Vector2(64));
        }

        // Coins go on the stage so they're always in front of the clouds drifting past. The definition says they
        // have four frames, and a sprite out of an atlas plays its own frames (Animated) with no animation manager
        for (int i = 0; i < 6; i++)
            _coins.Add(CreateProp(stage, "coin", new Vector2(-40.0f + i * 100.0f, 330.0f), new Vector2(48)));
    }

    /// <summary>
    /// Helper method to make a sprite out of the atlas and put it in a batch. Doesn't need the GL context: the atlas
    /// has nothing on the GPU until the batch first draws it.
    /// </summary>
    private Sprite CreateProp(SpriteBatch batch, string art, Vector2 position, Vector2 size)
    {
        // Atlas sprites ask for their art by name and it's stitched into the atlas the next time the batch draws.
        // Only what's asked for goes in, so a definition can name a thousand sprites and you pay for the ones you use
        var sprite = batch.AddEntity(new Sprite(size));
        sprite.ConfigureAtlas(_atlas, _props, art);
        sprite.Transform.Position = position;
        batch.Add(sprite);
        return sprite;
    }

    /// <summary>
    /// Helper method to put up the row of small blobs, each showing off one thing. Render thread.
    /// </summary>
    private void BuildSpecimens(SpriteBatch batch, SpriteBatch front, SpriteSheet sheet)
    {
        // Tint multiplies the colour of every pixel, alpha included. The middle one breathes its alpha in and out
        // forever (-1 loops), on the sprite's own tweens, which only move while the sprite is being updated
        Vector4[] tints = [new(1.0f, 0.5f, 0.5f, 1.0f), new(1.0f, 0.9f, 0.4f, 1.0f), new(0.5f, 0.7f, 1.0f, 1.0f)];
        for (int i = 0; i < tints.Length; i++)
        {
            var tinted = CreateBlob(batch, sheet, new Vector2(-660.0f + i * 100.0f, ROW), 5);
            tinted.Tint = tints[i];

            if (i == 1)
                tinted.TweenOpacity(0.2f, 1.2f).SetEasing(Easing.InOutSine).SetLoops(-1, LoopMode.PingPong);
        }

        // Spinning round its middle (Origin.Center is the default). Smooth blends the art's pixels where they meet,
        // which keeps pixel art from going jaggy when it's turned or drawn at an odd size
        var spinner = CreateBlob(batch, sheet, new Vector2(-300.0f, ROW), 6);
        spinner.Smooth = true;
        spinner.TweenRotation(360.0f, 3.0f).SetLoops(-1);

        // The same squash on two origins. A centred sprite squashes towards its middle and its feet leave the
        // ground, one with Origin.Bottom keeps them planted. The red dots are where each one's Position is
        for (int i = 0; i < 2; i++)
        {
            bool bottom = i == 1;
            var squasher = CreateBlob(batch, sheet, new Vector2(-20.0f + i * 160.0f, bottom ? ROW - 48.0f : ROW), 6);
            squasher.Smooth = true;
            if (bottom) squasher.Transform.Origin = Origin.Bottom;

            squasher.TweenSize(new Vector2(124, 62), 0.6f).SetEasing(Easing.InOutSine).SetLoops(-1, LoopMode.PingPong);

            CreateProp(front, "dot", squasher.Transform.Position, new Vector2(12)).Tint = MarkerColour;
        }
    }

    /// <summary>
    /// Helper method for a key, or a button on whichever gamepad was used last, going down this update.
    /// </summary>
    private static bool Pressed(Key key, GamepadInput button) =>
        Engine.Input.Keyboard.WasPressed(key) || Engine.Input.Gamepads.LastUsed?.WasPressed(button) == true;

    /// <summary>
    /// Helper method to say what the big blob is up to. Simulation thread, which is where the UI is updated too.
    /// </summary>
    private void UpdateBigCaption()
    {
        // Kept in a field rather than read off Flipped: mid PopIn() the size is zero, which reads as not flipped
        string facing = _bigFacingLeft ? "left" : "right";
        _bigCaption.Text = $"playing \"{Animations[_animation].Name}\", facing {facing}\nlast trick: {_lastTrick}";
    }

    /// <summary>
    /// Helper method to put up the panel in the corner and a caption for everything on stage.
    /// </summary>
    private void BuildCaptions()
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
        panel.Add(new Label("Sprites and animation") { Anchor = Origin.Left, TextScale = 0.36f });
        panel.Add(new Label(
            "Every blob comes off one SpriteSheet, the scenery\n" +
            "out of a TextureAtlas. Three SpriteBatches in a\n" +
            "Renderer2D: scenery, stage, then the front.")
        {
            Anchor = Origin.Left,
            Align = Origin.TopLeft,
            TextScale = 0.22f,
            Color = CaptionColour
        });

        Label Caption(string text, Vector2 position) =>
            module.AddComponent(new Label(text) { Position = position, TextScale = 0.2f, Color = CaptionColour });

        Caption("Tint, alpha and all", new Vector2(-560, ROW - 82));
        Caption("Rotation, Smooth on", new Vector2(-300, ROW - 90));
        Caption("Origin: Center | Bottom", new Vector2(60, ROW - 82));
        Caption("Out of a TextureAtlas, frames and all", new Vector2(210, 280));
        Caption("Hop frames picked by hand, in the front batch", new Vector2(-470, GROUND + 210));

        _bigCaption = Caption(string.Empty, new Vector2(0, GROUND + 290));
        _bigCaption.Text = "playing \"idle\", facing right\nlast trick: nothing yet";
    }
}
