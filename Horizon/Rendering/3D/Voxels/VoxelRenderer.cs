using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Voxels;

/// <summary>
/// Draws a <see cref="VoxelTree"/> by path tracing it, all of it, every frame, on the GPU. No meshes, no chunks to
/// load in round the player, no draw distance. The whole world is in the card as the tree it is on the CPU, and
/// rays go through it (shaders/voxels).
/// <code>
/// var renderer = AddEntity(new VoxelRenderer((uint)window.X, (uint)window.Y) { FollowWindow = true, World = tree, Atlas = "content/atlas.png" });
/// // every update
/// renderer.TimeOfDay = 17.5f;
/// renderer.Selected = (hit.X, hit.Y, hit.Z);
/// </code>
/// <para>
/// A frame is nine passes. The eye rays (one a pixel, sun shadow and all, the water with its reflection and what is
/// under it), the bounce light at half size (one ray a pixel, every bounce through last frame's picture), that laid
/// over the frames before it and filtered, everything put together with the haze, the TAA, the bloom, and the
/// picture as bytes into the render target, where the engine's post processing and the UI go on top of it like
/// they go on top of anything. Every pass has a GPU scope of its own, the performance overlay says what each costs.
/// </para>
/// <para>
/// Far away a ray stops at the first node of the tree that is smaller than its pixel and takes its colour, so
/// the far mountains cost a few steps of a ray each and not thousands (<see cref="Detail"/>). The bounce rays go
/// coarser quicker, they're a blur anyway (<see cref="BounceCone"/>). <see cref="RenderScale"/> is the big knob,
/// everything is traced at that share of the screen and blown up at the end.
/// </para>
/// The settings from any thread, they're read once a frame. The world is uploaded a slice a frame, see
/// <see cref="UploadProgress"/>, and every change after that as it happens.
/// </summary>
public class VoxelRenderer : Renderer3D
{
    // How many bytes of the world go up to the card a frame, at most, so a world arriving doesn't stall the frame it does
    private const int UPLOAD_BUDGET = 48 << 20;

    private const int BLOOM_LEVELS = 6;

    /// <summary>The world it draws. Can be swapped for another, which is sent up to the card from scratch.</summary>
    public VoxelTree? World
    {
        get => world;
        set
        {
            if (ReferenceEquals(world, value)) return;
            world = value;
            worldChanged = true;
        }
    }

    /// <summary>The atlas the tiles of the palette are in, a path to a square picture, loaded on the first frame.</summary>
    public string? Atlas { get; set; }

    /* What it costs */

    /// <summary>How much of the screen is traced, every way, 0.5 is a quarter of the rays. Blown up to the screen at the end.</summary>
    public float RenderScale { get; set; } = 1.0f;

    /// <summary>Whether the light bounces. Off is the sun, the sky straight on and nothing else, and about half the cost.</summary>
    public bool BounceLight { get; set; } = true;

    /// <summary>How much of the bounced light there is, 1 is all of it.</summary>
    public float BounceStrength { get; set; } = 1.0f;

    /// <summary>
    /// How many frames the bounce light is laid over at most. More is smoother and slower to follow a change, a
    /// torch put down takes this many frames to light the room fully.
    /// </summary>
    public int BounceHistory { get; set; } = 24;

    /// <summary>How wide the bounce rays' cone is, how quickly they stop going down the tree with distance.</summary>
    public float BounceCone { get; set; } = 0.03f;

    /// <summary>
    /// How much bigger than a pixel a node can be before a ray stops at it, 1 is a pixel. More is quicker and blurrier far
    /// away, less is crisper and slower. The voxels nearby are drawn whatever this is.
    /// </summary>
    public float Detail { get; set; } = 1.5f;

    /// <summary>The most steps an eye ray takes through the tree, a ray that gets this far sees the sky.</summary>
    public int MaxSteps { get; set; } = 400;

    /// <summary>
    /// What is drawn instead of the world, for finding out where the time goes. 0 the world, 1 how many steps every eye
    /// ray took (blue few, red the most there can be), 2 the world without the sun's shadow rays.
    /// </summary>
    public int DebugView { get; set; }

    /// <summary>Whether the faces have the tiles of the atlas on them, or the plain colour of their material.</summary>
    public bool Textures { get; set; } = true;

    /* The look of it */

    /// <summary>The time, 0 to 24, where the sun is. Noon is overhead (nearly), six is sunrise, eighteen sunset.</summary>
    public float TimeOfDay { get; set; } = 10.0f;

    /// <summary>How cloudy it is, 0 a clear sky to 1 overcast.</summary>
    public float Cloudiness { get; set; } = 0.4f;

    /// <summary>How thick the haze is at sea level, and how quickly it thins out going up.</summary>
    public float FogDensity { get; set; } = 0.0007f;

    public float FogFalloff { get; set; } = 0.012f;

    /// <summary>The height of the sea, where the haze is measured from.</summary>
    public float SeaLevel { get; set; } = 62.0f;

    /// <summary>How choppy the water is, 0 for a mirror.</summary>
    public float Waves { get; set; } = 1.0f;

    /// <summary>How far the foliage moves in the wind, 0 for still.</summary>
    public float Sway { get; set; } = 1.0f;

    /// <summary>How bright the picture is made, on top of what the time of day does to it.</summary>
    public float Exposure { get; set; } = 1.0f;

    public float Bloom { get; set; } = 0.05f;

    public float Vignette { get; set; } = 0.22f;

    public float Saturation { get; set; } = 1.08f;

    /// <summary>Whether the camera is under water, the picture goes murky and green.</summary>
    public bool Underwater { get; set; }

    /// <summary>The voxel the player is pointing at, outlined, null for none.</summary>
    public (int X, int Y, int Z)? Selected { get; set; }

    /* How it is going */

    /// <summary>How much of the world has been sent to the card, 0 to 1. Until it's 1 bits of the world are missing.</summary>
    public float UploadProgress { get; private set; }

    /// <summary>How big the picture that is traced is.</summary>
    public Vector2 TraceSize => new(traceWidth, traceHeight);

    /// <summary>How much of the card the world takes, in bytes.</summary>
    public long GpuBytes => (long)(nodeBuffer?.Size ?? 0) + (long)(voxelBuffer?.Size ?? 0);

    /// <summary>Where the sun is right now, a unit vector towards it.</summary>
    public Vector3 SunDirection => SunAt(TimeOfDay);

    private VoxelTree? world;
    private bool worldChanged = true;

    private GpuBuffer? nodeBuffer, voxelBuffer, materialBuffer, modelBuffer, frameBuffer, exposureBuffer, radianceCache;

    // Entries of the radiance cache and the bytes of one, shaders/voxels/cache.slang, which says the same
    private const int CACHE_ENTRIES = 1 << 20, CACHE_ENTRY_BYTES = 32;
    private int nodesSent, voxelsSent, nodesTotal, voxelsTotal;
    private bool fullUpload;
    private int paletteSent = -1;
    private readonly List<(int Start, int Count)> nodeRanges = [], voxelRanges = [];

    private Texture? atlas;
    private int atlasTiles = 16, atlasTexels = 16;

    private Technique? primary, surface, sunlight, indirect, bounceLight, temporal, atrous, composite, taa, exposure, bloomDown, bloomUp, tonemap;

    // The pictures, made to the size of the trace
    private uint traceWidth, traceHeight, halfWidth, halfHeight, outputWidth, outputHeight;
    private Texture? gPosition, gHit, gNormal, gAlbedo, gDirect, gSun, bouncePosition, bounceHit, raw, filterA, filterB, scene;
    private readonly Texture?[] light = new Texture?[2], moments = new Texture?[2], geometry = new Texture?[2], history = new Texture?[2];
    private readonly Texture?[] bloomDownLevels = new Texture?[BLOOM_LEVELS], bloomUpLevels = new Texture?[BLOOM_LEVELS];
    private int current;

    private Matrix4x4 previousViewProj;
    private Vector3 previousEye;
    private uint frameNumber;
    private bool historyValid;

    public VoxelRenderer(in uint width, in uint height) : base(width, height) { }

    // The picture is written by the last pass, a compute shader, so the render target's colour has to be an image too
    protected override RenderTarget CreateFrameBuffer(in uint width, in uint height) =>
        CreateFrameBuffer(
            new RenderTargetDescription
            {
                Width = width,
                Height = height,
                Attachments = new()
                {
                    { AttachmentPoint.Color0, new TextureDefinition(PixelFormat.Rgba8, Smooth: true, Usage: TextureUsage.Sampled | TextureUsage.Storage) },
                    { AttachmentPoint.DepthStencil, TextureDefinition.DepthStencil },
                }
            });

    protected override void BeforeResolve(float dt)
    {
        base.BeforeResolve(dt);

        VoxelTree? tree = world;
        if (tree is null || Engine.ActiveCamera is not Camera camera) return;

        var device = GraphicsDevice.Current;
        if (!LoadTechniques()) return;

        LoadAtlas();
        Fit();
        Upload(tree);
        if (nodeBuffer is null || voxelBuffer is null || materialBuffer is null || modelBuffer is null) return;

        WriteFrame(tree, camera, dt);

        int previous = current, next = 1 - current;

        // The eye rays on their own, then what they landed on, then the sun on it. Three passes and not one, a pass with
        // all four traces in it needs so many registers that the GPU keeps too few rays going at once
        using (device.BeginGpuScope("voxel eye rays"))
        {
            BindWorld(primary!);
            device.BindStorageImage(0, gPosition);
            device.BindStorageImage(1, gHit);
            device.Dispatch(Groups(traceWidth), Groups(traceHeight));
            device.Barrier(BarrierTargets.ShaderImages);
            Unbind(2);
        }

        using (device.BeginGpuScope("voxel surfaces"))
        {
            BindWorld(surface!);
            gHit!.Bind(0);
            (atlas ?? gAlbedo!).Bind(7);
            device.BindStorageImage(0, gPosition);
            device.BindStorageImage(1, gNormal);
            device.BindStorageImage(2, gAlbedo);
            device.BindStorageImage(3, gDirect);
            device.Dispatch(Groups(traceWidth), Groups(traceHeight));
            device.Barrier(BarrierTargets.ShaderImages);
            Unbind(4);
        }

        using (device.BeginGpuScope("voxel sunlight"))
        {
            BindWorld(sunlight!);
            gPosition!.Bind(0);
            gNormal!.Bind(1);
            gAlbedo!.Bind(2);
            device.BindStorageImage(0, gSun);
            device.Dispatch(Groups(traceWidth), Groups(traceHeight));
            device.Barrier(BarrierTargets.ShaderImages);
            Unbind(1);
        }

        Texture bounce = filterA!;
        if (BounceLight)
        {
            using (device.BeginGpuScope("voxel bounce rays"))
            {
                BindWorld(indirect!);
                gPosition!.Bind(0);
                gNormal!.Bind(1);
                device.BindStorageImage(0, bouncePosition);
                device.BindStorageImage(1, bounceHit);
                device.Dispatch(Groups(halfWidth), Groups(halfHeight));
                device.Barrier(BarrierTargets.ShaderImages);
                Unbind(2);
            }

            using (device.BeginGpuScope("voxel bounce light"))
            {
                BindWorld(bounceLight!);
                bouncePosition!.Bind(0);
                bounceHit!.Bind(1);
                device.BindStorageImage(0, raw);
                device.Dispatch(Groups(halfWidth), Groups(halfHeight));
                device.Barrier(BarrierTargets.ShaderImages);
                Unbind(1);
            }

            using (device.BeginGpuScope("voxel denoise"))
            {
                BindWorld(temporal!);
                raw!.Bind(0);
                gPosition!.Bind(1);
                gNormal!.Bind(2);
                light[previous]!.Bind(3);
                moments[previous]!.Bind(4);
                geometry[previous]!.Bind(5);
                device.BindStorageImage(0, light[next]);
                device.BindStorageImage(1, moments[next]);
                device.BindStorageImage(2, geometry[next]);
                device.Dispatch(Groups(halfWidth), Groups(halfHeight));
                device.Barrier(BarrierTargets.ShaderImages);
                Unbind(3);

                // A wide blur in three small ones, the taps a pixel, two and four apart
                Texture input = light[next]!;
                Texture output = filterA!;
                for (int pass = 0; pass < 3; pass++)
                {
                    BindWorld(atrous!);
                    atrous!.SetUniform("uStep", 1 << pass);
                    input.Bind(0);
                    gPosition.Bind(1);
                    gNormal.Bind(2);
                    moments[next]!.Bind(3);
                    device.BindStorageImage(0, output);
                    device.Dispatch(Groups(halfWidth), Groups(halfHeight));
                    device.Barrier(BarrierTargets.ShaderImages);
                    Unbind(1);

                    bounce = output;
                    input = output;
                    output = ReferenceEquals(output, filterA) ? filterB! : filterA!;
                }
            }
        }
        else
        {
            device.ClearTexture(filterA!, Vector4.Zero);
            bounce = filterA!;
        }

        using (device.BeginGpuScope("voxel compose"))
        {
            BindWorld(composite!);
            gPosition!.Bind(0);
            gNormal!.Bind(1);
            gAlbedo!.Bind(2);
            gDirect!.Bind(3);
            bounce.Bind(4);
            gSun!.Bind(5);
            device.BindStorageImage(0, scene);
            device.Dispatch(Groups(traceWidth), Groups(traceHeight));
            device.Barrier(BarrierTargets.ShaderImages);
            Unbind(1);
        }

        using (device.BeginGpuScope("taa"))
        {
            BindWorld(taa!);
            scene!.Bind(0);
            gPosition.Bind(1);
            history[previous]!.Bind(2);
            device.BindStorageImage(0, history[next]);
            device.Dispatch(Groups(traceWidth), Groups(traceHeight));
            device.Barrier(BarrierTargets.ShaderImages);
            Unbind(1);
        }

        Texture frame = history[next]!;

        // How bright to make it, from how bright it is, the way an eye gets used to the dark
        using (device.BeginGpuScope("exposure"))
        {
            BindWorld(exposure!);
            frame.Bind(0);
            device.Dispatch(1);
            device.Barrier(BarrierTargets.ShaderStorage);
        }

        RunBloom(device, frame);

        using (device.BeginGpuScope("tonemap"))
        {
            BindWorld(tonemap!);
            tonemap!.SetUniform("uOutputSize", new Vector2(outputWidth, outputHeight));
            tonemap.SetUniform("uBloom", MathF.Max(Bloom, 0.0f));
            tonemap.SetUniform("uVignette", Vignette);
            tonemap.SetUniform("uSaturation", Saturation);
            frame.Bind(0);
            (bloomUpLevels[0] ?? frame).Bind(1);
            device.BindStorageImage(0, FrameBuffer.Color);
            device.Dispatch(Groups(outputWidth), Groups(outputHeight));
            device.Barrier(BarrierTargets.ShaderImages);
            Unbind(1);
        }

        current = next;
        frameNumber++;
        historyValid = true;
    }

    /// <summary>Helper method to blur what's bright down a few halvings and back up again.</summary>
    private void RunBloom(GraphicsDevice device, Texture frame)
    {
        if (Bloom <= 0.0f || bloomDownLevels[0] is null) return;

        using var scope = device.BeginGpuScope("bloom");
        Texture source = frame;
        for (int level = 0; level < BLOOM_LEVELS; level++)
        {
            Texture target = bloomDownLevels[level]!;
            BindWorld(bloomDown!);
            bloomDown!.SetUniform("uTargetSize", new Vector2(target.Width, target.Height));
            bloomDown.SetUniform("uThreshold", 1.0f);
            bloomDown.SetUniform("uFirst", level == 0 ? 1 : 0);
            source.Bind(0);
            device.BindStorageImage(0, target);
            device.Dispatch(Groups(target.Width), Groups(target.Height));
            device.Barrier(BarrierTargets.ShaderImages);
            Unbind(1);
            source = target;
        }

        Texture low = bloomDownLevels[BLOOM_LEVELS - 1]!;
        for (int level = BLOOM_LEVELS - 2; level >= 0; level--)
        {
            Texture target = bloomUpLevels[level]!;
            BindWorld(bloomUp!);
            bloomUp!.SetUniform("uTargetSize", new Vector2(target.Width, target.Height));
            bloomUp.SetUniform("uLowSize", new Vector2(low.Width, low.Height));
            bloomUp.SetUniform("uRadius", 1.0f);
            low.Bind(0);
            bloomDownLevels[level]!.Bind(1);
            device.BindStorageImage(0, target);
            device.Dispatch(Groups(target.Width), Groups(target.Height));
            device.Barrier(BarrierTargets.ShaderImages);
            Unbind(1);
            low = target;
        }
    }

    private static uint Groups(uint size) => (size + 7) / 8;

    private static void Unbind(uint images)
    {
        for (uint i = 0; i < images; i++) GraphicsDevice.Current.BindStorageImage(i, null);
    }

    /// <summary>Helper method to bind a pass and the world's buffers for it.</summary>
    private void BindWorld(Technique technique)
    {
        technique.Bind();
        var device = GraphicsDevice.Current;
        device.BindStorageBuffer(2, nodeBuffer);
        device.BindStorageBuffer(3, voxelBuffer);
        device.BindStorageBuffer(4, materialBuffer);
        device.BindStorageBuffer(5, modelBuffer);
        device.BindStorageBuffer(6, frameBuffer);
        device.BindStorageBuffer(7, exposureBuffer);
        device.BindStorageBuffer(8, radianceCache);
    }

    private bool LoadTechniques()
    {
        if (primary is not null) return primary.IsValid;

        primary = Load("primary");
        surface = Load("surface");
        sunlight = Load("sunlight");
        indirect = Load("indirect");
        bounceLight = Load("bounce_light");
        temporal = Load("temporal");
        atrous = Load("atrous");
        composite = Load("composite");
        taa = Load("taa");
        exposure = Load("exposure");
        bloomDown = Load("bloom_down");
        bloomUp = Load("bloom_up");
        tonemap = Load("tonemap");

        return primary.IsValid && surface.IsValid && sunlight.IsValid && indirect.IsValid && bounceLight.IsValid && temporal.IsValid && atrous.IsValid && composite.IsValid
            && taa.IsValid && exposure.IsValid && bloomDown.IsValid && bloomUp.IsValid && tonemap.IsValid;

        static Technique Load(string name) => new(Shader.Load("shaders/voxels", name));
    }

    private void LoadAtlas()
    {
        if (atlas is not null || Atlas is null || world is null) return;

        atlas = Texture.Load("voxel atlas", Atlas, new TextureDefinition(PixelFormat.Rgba8Srgb, Smooth: false));
        atlasTiles = world.Palette.AtlasTilesPerRow;
        atlasTexels = atlas.IsValid ? (int)atlas.Width / Math.Max(atlasTiles, 1) : 16;
    }

    /* The pictures */

    private static readonly TextureDefinition Data32 = new(PixelFormat.Rgba32F, Smooth: false, Usage: TextureUsage.Sampled | TextureUsage.Storage);
    private static readonly TextureDefinition Data16 = new(PixelFormat.Rgba16F, Smooth: false, Usage: TextureUsage.Sampled | TextureUsage.Storage);
    private static readonly TextureDefinition Smooth16 = new(PixelFormat.Rgba16F, Smooth: true, Usage: TextureUsage.Sampled | TextureUsage.Storage);

    /// <summary>Helper method to make the pictures the size the trace is now, if they aren't already.</summary>
    private void Fit()
    {
        uint outW = Math.Max(1, FrameBuffer.Width), outH = Math.Max(1, FrameBuffer.Height);
        float scale = Math.Clamp(RenderScale, 0.25f, 1.0f);
        uint w = Math.Max(8, (uint)MathF.Round(outW * scale)), h = Math.Max(8, (uint)MathF.Round(outH * scale));
        if (w == traceWidth && h == traceHeight && outW == outputWidth && outH == outputHeight && gPosition is not null) return;

        DisposePictures();
        traceWidth = w;
        traceHeight = h;
        halfWidth = (w + 1) / 2;
        halfHeight = (h + 1) / 2;
        outputWidth = outW;
        outputHeight = outH;

        gPosition = Make("voxel position", w, h, Data32);
        gHit = Make("voxel hit", w, h, new TextureDefinition(PixelFormat.Rgba32Uint, Smooth: false, Usage: TextureUsage.Sampled | TextureUsage.Storage));
        gSun = Make("voxel sun", w, h, Data16);
        gNormal = Make("voxel normal", w, h, Data16);
        gAlbedo = Make("voxel albedo", w, h, Data16);
        gDirect = Make("voxel direct", w, h, Data16);
        scene = Make("voxel scene", w, h, Smooth16);
        raw = Make("bounce raw", halfWidth, halfHeight, Data16);
        bouncePosition = Make("bounce position", halfWidth, halfHeight, Data32);
        bounceHit = Make("bounce hit", halfWidth, halfHeight, new TextureDefinition(PixelFormat.Rgba32Uint, Smooth: false, Usage: TextureUsage.Sampled | TextureUsage.Storage));
        filterA = Make("bounce filter a", halfWidth, halfHeight, Data16);
        filterB = Make("bounce filter b", halfWidth, halfHeight, Data16);

        for (int i = 0; i < 2; i++)
        {
            light[i] = Make($"bounce light {i}", halfWidth, halfHeight, Data16);
            moments[i] = Make($"bounce moments {i}", halfWidth, halfHeight, Data16);
            geometry[i] = Make($"bounce geometry {i}", halfWidth, halfHeight, Data16);
            history[i] = Make($"taa {i}", w, h, Smooth16);
        }

        uint bw = w, bh = h;
        for (int level = 0; level < BLOOM_LEVELS; level++)
        {
            bw = Math.Max(1, bw / 2);
            bh = Math.Max(1, bh / 2);
            bloomDownLevels[level] = Make($"bloom down {level}", bw, bh, Smooth16);
            bloomUpLevels[level] = level < BLOOM_LEVELS - 1 ? Make($"bloom up {level}", bw, bh, Smooth16) : null;
        }

        // Nothing that was in the old pictures means anything now
        historyValid = false;
        foreach (Texture? texture in light) GraphicsDevice.Current.ClearTexture(texture!, Vector4.Zero);
        foreach (Texture? texture in geometry) GraphicsDevice.Current.ClearTexture(texture!, new Vector4(0, 0, 0, -1));
        foreach (Texture? texture in moments) GraphicsDevice.Current.ClearTexture(texture!, Vector4.Zero);
        foreach (Texture? texture in history) GraphicsDevice.Current.ClearTexture(texture!, Vector4.Zero);

        static Texture Make(string name, uint width, uint height, TextureDefinition definition)
        {
            Texture texture = Texture.Create(width, height, definition);
            texture.Name = name;
            return texture;
        }
    }

    private void DisposePictures()
    {
        foreach (Texture? texture in new[] { gPosition, gHit, gNormal, gAlbedo, gDirect, gSun, bouncePosition, bounceHit, raw, filterA, filterB, scene }) texture?.Dispose();
        for (int i = 0; i < 2; i++)
        {
            light[i]?.Dispose();
            moments[i]?.Dispose();
            geometry[i]?.Dispose();
            history[i]?.Dispose();
        }

        for (int i = 0; i < BLOOM_LEVELS; i++)
        {
            bloomDownLevels[i]?.Dispose();
            bloomUpLevels[i]?.Dispose();
        }
    }

    /* The world, to the card */

    /// <summary>
    /// Helper method to send the card what it hasn't got of the world. A new world (or one that was compacted) goes
    /// up from the start a slice a frame, after that only what changed, as it changes.
    /// </summary>
    private void Upload(VoxelTree tree)
    {
        lock (tree.SyncRoot)
        {
            nodeRanges.Clear();
            voxelRanges.Clear();
            bool everything = tree.TakeChanges(nodeRanges, voxelRanges) || worldChanged;
            worldChanged = false;

            uint[] nodes = tree.Nodes;
            byte[] voxels = tree.Voxels;
            int nodeCount = tree.NodeCount;
            int voxelBytes = (tree.VoxelBytes + 3) & ~3;

            // The buffers as big as the arrays are, which have room to grow already
            long nodeCapacity = Math.Max((long)nodes.Length * 4, 64);
            long voxelCapacity = Math.Max(((long)voxels.Length + 3) & ~3L, 64);
            var device = GraphicsDevice.Current;

            if (nodeBuffer is null || everything)
            {
                // A new world, from the start, into buffers that are only made again if they're too small for it
                if (nodeBuffer is null || (long)nodeBuffer.Size < nodeCapacity || (long)voxelBuffer!.Size < voxelCapacity)
                {
                    nodeBuffer?.Dispose();
                    voxelBuffer?.Dispose();
                    nodeBuffer = MakeWorldBuffer("voxel nodes", nodeCapacity);
                    voxelBuffer = MakeWorldBuffer("voxels", voxelCapacity);
                }
                else
                {
                    device.FillBuffer(nodeBuffer, 0);
                    device.FillBuffer(voxelBuffer, 0);
                }

                fullUpload = true;
                nodesSent = 0;
                voxelsSent = 0;
                nodeRanges.Clear();
                voxelRanges.Clear();

                // The light a new world had bounced about is nothing to do with this one
                if (radianceCache is not null) device.FillBuffer(radianceCache, 0);
            }
            else
            {
                // A world that grew past its buffers (one being made while it's played in) gets bigger ones, what the old
                // ones had copied over on the card, nothing sent again
                if ((long)nodeBuffer.Size < (long)nodeCount * 16) nodeBuffer = Grow(nodeBuffer, "voxel nodes", nodeCapacity);
                if ((long)voxelBuffer!.Size < voxelBytes) voxelBuffer = Grow(voxelBuffer, "voxels", voxelCapacity);
            }

            if (paletteSent != tree.Palette.Count + tree.Palette.Models.Count * 1000 || materialBuffer is null)
            {
                materialBuffer ??= GpuBuffer.Create(BufferDescription.StorageBuffer(VoxelPalette.CAPACITY * 16));
                materialBuffer.Name = "voxel materials";
                materialBuffer.Update<uint>(tree.Palette.Pack());

                int models = Math.Max(tree.Palette.Models.Count, 1);
                var packed = new uint[models * VoxelModel.PACKED_UINTS];
                for (int i = 0; i < tree.Palette.Models.Count; i++)
                    tree.Palette.Models[i].Pack(packed.AsSpan(i * VoxelModel.PACKED_UINTS, VoxelModel.PACKED_UINTS));
                modelBuffer?.Dispose();
                modelBuffer = GpuBuffer.Create(BufferDescription.StorageBuffer((nuint)(packed.Length * 4)));
                modelBuffer.Name = "voxel models";
                modelBuffer.Update<uint>(packed);
                paletteSent = tree.Palette.Count + tree.Palette.Models.Count * 1000;
            }

            if (fullUpload)
            {
                // What changed in the part that's up already goes up now, the rest comes with the slices
                foreach (var (start, count) in nodeRanges)
                {
                    int end = Math.Min(start + count, nodesSent);
                    if (end > start) nodeBuffer!.Update<uint>(nodes.AsSpan(start * 4, (end - start) * 4), start * 16);
                }

                foreach (var (start, count) in voxelRanges)
                {
                    int from = start & ~3, to = Math.Min((start + count + 3) & ~3, voxelsSent);
                    if (to > from) voxelBuffer!.Update<byte>(voxels.AsSpan(from, to - from), from);
                }

                // From the start, as much as the budget lets
                int budget = UPLOAD_BUDGET;
                nodesTotal = nodeCount;
                voxelsTotal = voxelBytes;

                int nodeBytes = Math.Min((nodesTotal - nodesSent) * 16, budget);
                if (nodeBytes > 0)
                {
                    int count = nodeBytes / 16;
                    nodeBuffer!.Update<uint>(nodes.AsSpan(nodesSent * 4, count * 4), nodesSent * 16);
                    nodesSent += count;
                    budget -= nodeBytes;
                }

                int bytes = Math.Min(voxelsTotal - voxelsSent, budget) & ~3;
                if (bytes > 0)
                {
                    voxelBuffer!.Update<byte>(voxels.AsSpan(voxelsSent, bytes), voxelsSent);
                    voxelsSent += bytes;
                }

                if (nodesSent >= nodesTotal && voxelsSent >= voxelsTotal) fullUpload = false;
                long total = (long)nodesTotal * 16 + voxelsTotal;
                UploadProgress = total == 0 ? 1.0f : (float)(((long)nodesSent * 16 + voxelsSent) / (double)total);
                return;
            }

            UploadProgress = 1.0f;

            // Just what changed
            foreach (var (start, count) in nodeRanges)
                nodeBuffer!.Update<uint>(nodes.AsSpan(start * 4, count * 4), start * 16);

            foreach (var (start, count) in voxelRanges)
            {
                int from = start & ~3;
                int to = Math.Min((start + count + 3) & ~3, voxels.Length & ~3);
                if (to > from) voxelBuffer!.Update<byte>(voxels.AsSpan(from, to - from), from);
            }
        }
    }

    /// <summary>Helper method to make a buffer for the world, empty (all zeros is all empty nodes, a ray goes straight through).</summary>
    private static GpuBuffer MakeWorldBuffer(string name, long bytes)
    {
        GpuBuffer buffer = GpuBuffer.Create(BufferDescription.StorageBuffer((nuint)bytes));
        buffer.Name = name;
        GraphicsDevice.Current.FillBuffer(buffer, 0);
        return buffer;
    }

    /// <summary>Helper method to swap a buffer of the world for a bigger one with the same in it, copied on the card.</summary>
    private static GpuBuffer Grow(GpuBuffer old, string name, long bytes)
    {
        GpuBuffer bigger = MakeWorldBuffer(name, Math.Max(bytes, (long)old.Size * 3 / 2));
        GraphicsDevice.Current.CopyBuffer(old, bigger, old.Size);
        old.Dispose();
        return bigger;
    }

    /* The frame */

    [StructLayout(LayoutKind.Sequential)]
    private struct Int4(int x, int y, int z, int w)
    {
        public int X = x, Y = y, Z = z, W = w;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameData
    {
        public Matrix4x4 InvViewProj, ViewProj, PrevViewProj;
        public Vector4 CameraPos, PrevCameraPos, SunDir, SunColor, MoonDir, SkyParams, World, Resolution, Jitter, Lod, Water;
        public Int4 Selected;
        public Vector4 Misc, Atlas, History;
    }

    private FrameData data;

    /// <summary>Helper method to work out the frame's block and send it up.</summary>
    private void WriteFrame(VoxelTree tree, Camera camera, float dt)
    {
        Matrix4x4 viewProj = camera.ViewProj;
        Matrix4x4.Invert(viewProj, out Matrix4x4 inverse);
        Matrix4x4.Invert(camera.View, out Matrix4x4 cameraWorld);
        Vector3 eye = cameraWorld.Translation;

        // A jump of more than a few blocks between two frames is a cut, nothing on the last frame is where it was
        bool cut = !historyValid || Vector3.Distance(eye, previousEye) > 24.0f;
        if (cut)
        {
            previousViewProj = viewProj;
            previousEye = eye;
        }

        Vector3 sun = SunAt(TimeOfDay);
        Vector3 moon = -sun;
        float day = SmoothStep(-0.12f, 0.12f, sun.Y);

        // The light of a frame is the sun's while it is up and the moon's while it isn't, one or the other
        Vector3 lightDirection, lightColor;
        if (sun.Y > -0.02f)
        {
            float warm = SmoothStep(0.0f, 0.45f, sun.Y);
            Vector3 tint = Vector3.Lerp(new Vector3(1.0f, 0.42f, 0.16f), new Vector3(1.0f, 0.95f, 0.88f), warm);
            lightDirection = sun;
            lightColor = tint * 3.0f * SmoothStep(-0.02f, 0.12f, sun.Y) * (1.0f - 0.55f * Cloudiness);
        }
        else
        {
            lightDirection = moon;
            lightColor = new Vector3(0.32f, 0.4f, 0.62f) * 0.32f * SmoothStep(-0.02f, 0.2f, moon.Y);
        }

        // Projection's Y scale is 1 / tan(half the field of view), which is how big a pixel is
        float tanHalf = MathF.Abs(camera.Projection.M22) > 1e-5f ? 1.0f / MathF.Abs(camera.Projection.M22) : 0.577f;
        float pixelAngle = 2.0f * tanHalf / traceHeight;

        Vector2 jitter = Halton(frameNumber % 8 + 1) - new Vector2(0.5f);

        data.InvViewProj = inverse;
        data.ViewProj = viewProj;
        data.PrevViewProj = previousViewProj;
        data.CameraPos = new Vector4(eye, (float)GameEngine.Instance.TotalTime);
        data.PrevCameraPos = new Vector4(previousEye, frameNumber);
        data.SunDir = new Vector4(lightDirection, sun.Y);
        data.SunColor = new Vector4(lightColor, Math.Clamp(Cloudiness, 0.0f, 1.0f));
        data.MoonDir = new Vector4(moon, 1.0f);
        data.SkyParams = new Vector4(FogDensity, FogFalloff, Exposure, day);
        data.World = new Vector4(tree.SizeX, tree.SizeY, tree.SizeZ, tree.Depth);
        data.Resolution = new Vector4(traceWidth, traceHeight, halfWidth, halfHeight);
        data.Jitter = new Vector4(jitter, outputWidth, outputHeight);
        data.Lod = new Vector4(pixelAngle, MathF.Max(Detail, 0.25f), MathF.Max(BounceCone, 0.001f), Math.Clamp(MaxSteps, 32, 4096));
        data.Water = new Vector4(SeaLevel, Waves * 0.6f, 1.0f, 0.0f);
        data.Selected = Selected is { } s ? new Int4(s.X, s.Y, s.Z, 1) : new Int4(0, 0, 0, 0);
        data.Misc = new Vector4(Underwater ? 1.0f : 0.0f, Sway, BounceStrength, cut ? 1.0f : 0.0f);
        data.Atlas = new Vector4(atlasTiles, 1.0f / atlasTiles, Textures && atlas is { IsValid: true } ? 1.0f : 0.0f, atlasTexels);
        data.History = new Vector4(Math.Max(BounceHistory, 1), 0.1f, DebugView, Math.Clamp(dt, 0.0f, 0.25f));

        frameBuffer ??= GpuBuffer.Create(BufferDescription.StorageBuffer((nuint)Marshal.SizeOf<FrameData>()));
        if (radianceCache is null)
        {
            // Sixteen bytes an entry, empty to begin with (no entry's check is ever 0)
            radianceCache = GpuBuffer.Create(BufferDescription.StorageBuffer(CACHE_ENTRIES * CACHE_ENTRY_BYTES));
            radianceCache.Name = "radiance cache";
            GraphicsDevice.Current.FillBuffer(radianceCache, 0);
        }

        if (exposureBuffer is null)
        {
            // Nothing in it means nothing yet, the first frame takes whatever it measures
            exposureBuffer = GpuBuffer.Create(BufferDescription.StorageBuffer(16));
            exposureBuffer.Update<float>([0.0f, 0.0f, 0.0f, 0.0f]);
        }

        frameBuffer.Update(MemoryMarshal.CreateReadOnlySpan(ref data, 1));

        previousViewProj = viewProj;
        previousEye = eye;

        if (TraceFrames)
        {
            Vector3 forward = -new Vector3(cameraWorld.M31, cameraWorld.M32, cameraWorld.M33);
            Horizon.Logging.Log.Info($"[VoxelTrace] {frameNumber} t={GameEngine.Instance.TotalTime:0.00000} dt={dt * 1000:0.000} yaw={MathF.Atan2(forward.X, -forward.Z) * 180 / MathF.PI:0.0000} eye={eye.X:0.000},{eye.Y:0.000},{eye.Z:0.000}");
        }
    }

    // Temporary, a line in the log for every frame
    private static readonly bool TraceFrames = Environment.GetEnvironmentVariable("HORIZON_VOXEL_TRACE") == "1";

    /// <summary>Where the sun is at a time of day, towards it. Up the east at six, nearly overhead at noon, down the west at eighteen.</summary>
    public static Vector3 SunAt(float hours)
    {
        float angle = (hours - 6.0f) / 24.0f * MathF.Tau;
        return Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle) * 0.92f, 0.28f + MathF.Sin(angle) * 0.12f));
    }

    private static float SmoothStep(float from, float to, float x)
    {
        float t = Math.Clamp((x - from) / (to - from), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static Vector2 Halton(uint index) => new(Radical(index, 2), Radical(index, 3));

    private static float Radical(uint index, uint radix)
    {
        float result = 0.0f, fraction = 1.0f;
        while (index > 0)
        {
            fraction /= radix;
            result += fraction * (index % radix);
            index /= radix;
        }

        return result;
    }

    protected override void DisposeOther()
    {
        DisposePictures();
        nodeBuffer?.Dispose();
        voxelBuffer?.Dispose();
        materialBuffer?.Dispose();
        modelBuffer?.Dispose();
        frameBuffer?.Dispose();
        exposureBuffer?.Dispose();
        radianceCache?.Dispose();
        base.DisposeOther();
    }
}
