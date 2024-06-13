using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

using CodeKneading.Player;
using CodeKneading.Voxel;

using Horizon.Core;
using Horizon.Core.Collections;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Engine.Debugging.Debuggers;
using Horizon.HIDL.Runtime;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering;

using ImGuiNET;

using ImPlotNET;

using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.SDL;

using static CodeKneading.Rendering.WorldBufferManager;

namespace CodeKneading.Rendering;

internal class FrustumCullingTechnique : Technique
{
    public FrustumCullingTechnique()
    {
        if (GameEngine.Instance.ObjectManager.Shaders.TryCreate(ShaderDescription.FromPath("content/shaders", "tilemesh_frustum"), out var result))
        {
            SetShader(result.Asset);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }
}

internal class OcclusionTechnique : Technique
{
    public OcclusionTechnique()
    {
        if (GameEngine.Instance.ObjectManager.Shaders.TryCreate(ShaderDescription.FromPath("content/shaders", "occlusion"), out var result))
        {
            SetShader(result.Asset);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }

    protected override void SetUniforms()
    {
        BindBuffer(3, MainScene.CameraData);
    }
}

internal class UpdateCullingTechnique : Technique
{
    public UpdateCullingTechnique()
    {
        if (GameEngine.Instance.ObjectManager.Shaders.TryCreate(ShaderDescription.FromPath("content/shaders", "tilemesh_cull"), out var result))
        {
            SetShader(result.Asset);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }
}
internal class WorldRenderer : IGameComponent
{
    internal readonly WorldBufferManager BufferManager;
    internal FrameBufferObject FrameBufferFar, FrameBufferNear;
    private readonly VoxelWorld world;
    private readonly int ZERO = 0;
    private Horizon.OpenGL.Assets.Texture atlas;
    private readonly LinearBuffer<double> averageDrawFrameTimes = new(512);
    private readonly LinearBuffer<double> averageShadowFrameTimes = new(512);
    private readonly LinearBuffer<double> averageTimes = new(512);
    private QueryObject cullQuery, drawQuery, shadowQuery;
    private UpdateCullingTechnique cullShader;
    private float cullTimer = 0.0f, fps = 0.0f;
    private FrustumCullingTechnique frustumTechnique;
    private bool hasBeenLogged, collectMetrics, calcCull, usePreCulling = false;
    private OcclusionTechnique occlusionTechnique;
    private ShadowTechnique shadow;
    private RenderTarget skyboxTarget, postProcessingTarget;
    private SkyManager skyManager;
    private WorldTechnique technique;
    private float time = 0;
    private BufferObject visibilityBuffer, chunkIndexBuffer;
    public WorldRenderer(in VoxelWorld world)
    {
        Name = "WorldRenderer";
        Enabled = true;
        this.world = world;
        BufferManager = new();
    }

    public bool Enabled { get; set; }
    public string Name { get; set; }
    public Entity Parent { get; set; }

    public void Initialize()
    {
        skyManager = (Parent as VoxelWorld)!.Sky;

        BufferManager.Initialize();

        technique = new WorldTechnique();
        shadow = new ShadowTechnique();
        frustumTechnique = new FrustumCullingTechnique();
        occlusionTechnique = new OcclusionTechnique();
        cullShader = new UpdateCullingTechnique();

        if (GameEngine.Instance.ObjectManager.Queries.TryCreate(
            new QueryObjectDescription
            {
                Target = QueryTarget.TimeElapsed
            },
            out var cullResult)
            )
        {
            cullQuery = cullResult.Asset;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Failed to create QueryObject!");
        }

        if (GameEngine.Instance.ObjectManager.Queries.TryCreate(
          new QueryObjectDescription
          {
              Target = QueryTarget.TimeElapsed
          },
          out var drawResult)
          )
        {
            drawQuery = drawResult.Asset;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Failed to create QueryObject!");
        }

        if (GameEngine.Instance.ObjectManager.Queries.TryCreate(
         new QueryObjectDescription
         {
             Target = QueryTarget.TimeElapsed
         },
         out var shadowResult)
         )
        {
            shadowQuery = shadowResult.Asset;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Failed to create QueryObject!");
        }

        GameEngine.Instance.GL.Enable(EnableCap.Multisample);

        if (GameEngine.Instance.ObjectManager.Textures.TryCreate(new TextureDescription
        {
            Paths = ["content/textures/atlas_albedo.png"],
            Definition = TextureDefinition.RgbaUnsignedByte with
            {
                Parameters = [
                    new () { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Nearest},
                    new () { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Nearest },
                    new () { Name = TextureParameterName.TextureBaseLevel, Value = 0 },
                    new () { Name = TextureParameterName.TextureMaxLevel, Value = 0 },
                    ]
            }
        }, out var atlasResult))
        {
            atlas = atlasResult.Asset;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, atlasResult.Message);
        }

        CreateShadowMaps();
        CreateAndUploadCullBuffer();

        skyboxTarget = new RenderTarget(1600, 900, new SkyBoxTechnique());
        skyboxTarget.Initialize();

        var p = new PostProcessingTechnique();
        postProcessingTarget = new RenderTarget(1600, 900, p);
        postProcessingTarget.Initialize();
        p.SetRenderTarget(postProcessingTarget);
        p.SetSkyBox(skyboxTarget.FrameBuffer);

        GameEngine.Instance.GL.ActiveTexture(TextureUnit.Texture0);
    }

    public unsafe void Render(float dt, object? obj = null)
    {
        BufferManager.UploadMesh();
        if (!BufferManager.ShouldDraw) return;

        //lock (BufferManager.Buffer)
        {
            cullTimer += dt;
            if (cullTimer > 0.1f)
            {
                fps = MathF.Round(1.0f / dt, 3);
                cullQuery.Begin();
                OcclusionCullChunks();
                cullQuery.End();

                //cullTimer = 0;
                calcCull = true;
            }
            time += dt;

            //shadowQuery.Begin();
            DrawShadowPass();
            //shadowQuery.End();

            //drawQuery.Begin();
            DrawRenderPass();
            //drawQuery.End();

            if (ImGui.Begin("World Renderer"))
            {
                ImGui.Text($"FPS: {fps}\nAllocated Chunklets: {BufferManager.ChunkletManager.Count}\nHeap info: \nVertex: {(int)(BufferManager.ChunkletManager.VertexHeap.UsedBytes / 1024.0 / 1024.0)}MB ({BufferManager.ChunkletManager.VertexHeap.Health}%%)\nElement: {(int)(BufferManager.ChunkletManager.ElementHeap.UsedBytes / 1024.0 / 1024.0)}MB\nCull: {(int)(BufferManager.ChunkletManager.CullIndirectHeap.UsedBytes / 1024.0 / 1024.0)}MB\nDraw: {(int)(BufferManager.ChunkletManager.DrawIndirectHeap.UsedBytes / 1024.0 / 1024.0)}MB\nData: {(int)(BufferManager.ChunkletManager.DrawDataHeap.UsedBytes / 1024.0 / 1024.0)}MB\nDrawCommands: {BufferManager.RealDrawCount}\n");

                ImGui.Checkbox("Prefrustum culling", ref usePreCulling);
                ImGui.Checkbox("Collect Metrics", ref collectMetrics);
                if (collectMetrics)
                {
                    double cAvg = averageTimes.Buffer.Average();
                    double rAvg = averageDrawFrameTimes.Buffer.Average();
                    double sAvg = averageShadowFrameTimes.Buffer.Average();

                    ImGui.Text($"({averageTimes.Length}/{averageTimes.Capacity}samples)");
                    ImGui.SameLine();
                    if (ImGui.Button("Clear"))
                    {
                        averageTimes.Clear();
                        averageDrawFrameTimes.Clear();
                        averageShadowFrameTimes.Clear();
                    }
                    ImGui.Separator();

                    ImGui.Text($"culling time: {Math.Round(cAvg, 5)}ms/{Math.Round(1.0 / cAvg * 1000)}FPS");
                    ImGui.Text($"render  time: {Math.Round(rAvg, 5)}ms/{Math.Round(1.0 / rAvg * 1000)}FPS");
                    ImGui.Text($"shadow  time: {Math.Round(sAvg, 5)}ms/{Math.Round(1.0 / sAvg * 1000)}FPS");

                    PerformanceProfilerDebugger.PlotValues("Frame Times", averageDrawFrameTimes);
                }
                ImGui.End();
            }
        }

        calcCull = false;
    }

    public void UpdatePhysics(float dt)
    {
    }

    public void UpdateState(float dt)
    {
    }

    private static string genData(in double[] buffer, in string unit = "ms")
    {
        double average = buffer.Average();
        double sum = buffer.Sum(d => Math.Pow(d - average, 2));
        double deviation = Math.Sqrt(sum / (buffer.Length - 1));

        return $"{buffer.Min()}{unit}\t{buffer.Max()}{unit}\t{average}{unit}\t{deviation}{unit}";
    }

    private unsafe void CreateAndUploadCullBuffer()
    {
        if (GameEngine.Instance.ObjectManager.Buffers.TryCreate(
            BufferObjectDescription.ShaderStorageBuffer,
            out var result))
        {
            visibilityBuffer = result.Asset;
            visibilityBuffer.NamedBufferData(VoxelWorld.LOADED_DISTANCE * VoxelWorld.HEIGHT * VoxelWorld.LOADED_DISTANCE * sizeof(uint) * 6);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }

    private void CreateShadowMaps()
    {
        if (GameEngine.Instance.ObjectManager.FrameBuffers.TryCreate(new FrameBufferObjectDescription
        {
            Width = 4096,
            Height = 4096,
            Attachments = new() {
                { FramebufferAttachment.DepthAttachment, FrameBufferAttachmentDefinition.TextureDepth with {
                    TextureDefinition = TextureDefinition.DepthComponent with {
                        Parameters = [
                            new () { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToEdge },
                            new () { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToEdge },
                            new () { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Linear },
                            new () { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Linear },

                            new () { Name = TextureParameterName.TextureCompareMode, Value = (int)GLEnum.CompareRefToTexture },
                            new () { Name = TextureParameterName.TextureCompareFunc, Value = (int)GLEnum.Lequal },

                        ]
                    }
                } },
            },
        }, out var farResult))
        {
            FrameBufferFar = farResult.Asset;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, farResult.Message);
        }

        if (GameEngine.Instance.ObjectManager.FrameBuffers.TryCreate(new FrameBufferObjectDescription
        {
            Width = 4096,
            Height = 4096,
            Attachments = new() {
                 { FramebufferAttachment.DepthAttachment, FrameBufferAttachmentDefinition.TextureDepth with {
                    TextureDefinition = TextureDefinition.DepthComponent with {
                        Parameters = [
                            new () { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToEdge },
                            new () { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToEdge },
                            new () { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Linear },
                            new () { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Linear },

                            new () { Name = TextureParameterName.TextureCompareMode, Value = (int)GLEnum.CompareRefToTexture },
                            new () { Name = TextureParameterName.TextureCompareFunc, Value = (int)GLEnum.Lequal },

                        ]
                    }
                } },
            },
        }, out var nearResult))
        {
            FrameBufferNear = nearResult.Asset;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, nearResult.Message);
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void CullChunks(Vector3 direction, bool cullFaces = true)
    {
        if (!calcCull) return;

        cullShader.Bind();
        cullShader.SetUniform("uCullFaces", cullFaces);
        cullShader.SetUniform("uCamDir", in direction);
        cullShader.SetUniform("uTileChunkCount", (uint)BufferManager.MeshDrawCount);

        cullShader.BindBuffer(0, BufferManager.Buffer[VertexArrayBufferAttachmentType.IndirectBuffer]);
        cullShader.BindBuffer(1, visibilityBuffer);
        cullShader.BindBuffer(2, BufferManager.FinalIndirectBuffer);
        cullShader.BindBuffer(3, BufferManager.ChunkPositionsIndexBuffer);

        GameEngine.Instance.GL.DispatchCompute((uint)VoxelWorld.Chunks.Length / 32, 1, 1);
        GameEngine.Instance.GL.MemoryBarrier(MemoryBarrierMask.AllBarrierBits);
        BufferManager.RealDrawCount = BufferManager.FinalIndirectBuffer.GetSubData<uint>(0, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void DrawRenderPass()
    {
        postProcessingTarget.BindForRendering();
        GameEngine.Instance.GL.Clear(ClearBufferMask.DepthBufferBit | ClearBufferMask.ColorBufferBit);

        // frustum and backface culling compute shader
        CullChunks(GameEngine.Instance.ActiveCamera.Direction);

        BufferManager.Buffer.Bind();
        technique.Bind();

        FrameBufferFar.BindAttachment(FramebufferAttachment.DepthAttachment, 0);
        technique.SetUniform("uTexSunDepthFar", 0);

        FrameBufferNear.BindAttachment(FramebufferAttachment.DepthAttachment, 1);
        technique.SetUniform("uTexSunDepthNear", 1);

        atlas.Bind(2);
        technique.SetUniform("uTexAlbedo", 2);

        technique.SetUniform("uSunViewProjNear", skyManager.SunViewNear * SkyManager.SunProjNear);
        technique.SetUniform("uSunViewProjFar", skyManager.SunViewFar * SkyManager.SunProjFar);
        technique.SetUniform("uSunDir", skyManager.SunDirection);

        technique.BindBuffer(0, BufferManager.ChunkPositionsBuffer);
        technique.BindBuffer(1, BufferManager.ChunkPositionsIndexBuffer);
        technique.SetUniform("uUseIndexMap", true);

        BufferManager.FinalIndirectBuffer.Bind();
        BufferManager.Render();

        technique.Unbind();
        BufferManager.Buffer.Unbind();

        skyboxTarget.BindForRendering();
        GameEngine.Instance.GL.Clear(ClearBufferMask.DepthBufferBit | ClearBufferMask.ColorBufferBit);

        skyboxTarget.Render(0, null);

        GameEngine.Instance.GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GameEngine.Instance.GL.Viewport(0, 0, (uint)GameEngine.Instance.WindowManager.WindowSize.X, (uint)GameEngine.Instance.WindowManager.WindowSize.Y);

        GameEngine.Instance.GL.Clear(ClearBufferMask.DepthBufferBit | ClearBufferMask.ColorBufferBit);

        postProcessingTarget.Render(0, null);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void DrawShadowPass()
    {
        DrawShadowPassBuffer(FrameBufferFar, SkyManager.SunProjFar, skyManager.SunViewFar, -skyManager.SunDirection);
        DrawShadowPassBuffer(FrameBufferNear, SkyManager.SunProjNear, skyManager.SunViewNear, -skyManager.SunDirection);

        GameEngine.Instance.GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void DrawShadowPassBuffer(in FrameBufferObject fbo, Matrix4x4 sunProj, Matrix4x4 sunView, Vector3 dir)
    {
        fbo.Bind();
        fbo.Viewport();

        GameEngine.Instance.GL.Clear(Silk.NET.OpenGL.ClearBufferMask.DepthBufferBit);

        BufferManager.Buffer.Bind();
        shadow.SetSun(in sunProj, in sunView);
        shadow.Bind();

        technique.BindBuffer(0, BufferManager.ChunkPositionsBuffer);
        technique.BindBuffer(1, BufferManager.ChunkPositionsIndexBuffer);

        BufferManager.Buffer[VertexArrayBufferAttachmentType.IndirectBuffer].Bind();
        BufferManager.RenderAll();

        shadow.Unbind();
        BufferManager.Buffer.Unbind();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FrustumCullChunks(Matrix4x4 view, Matrix4x4 proj)
    {
        frustumTechnique.Bind();
        frustumTechnique.SetUniform("uProj", proj);
        frustumTechnique.SetUniform("uView", view);
        frustumTechnique.SetUniform("uTileChunkCount", BufferManager.MeshDrawCount);

        frustumTechnique.BindBuffer("b_indirectBlock", BufferManager.CullIndirectBuffer);

        GameEngine.Instance.GL.DispatchCompute((uint)VoxelWorld.Chunks.Length / 32, 1, 1);
        GameEngine.Instance.GL.MemoryBarrier(MemoryBarrierMask.AllBarrierBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void OcclusionCullChunks()
    {
        GameEngine.Instance.GL.Clear(ClearBufferMask.DepthBufferBit);
        GameEngine.Instance.GL.ClearNamedBufferData(visibilityBuffer.Handle, SizedInternalFormat.R8i, Silk.NET.OpenGL.PixelFormat.RedInteger, Silk.NET.OpenGL.PixelType.Int, in ZERO);

        if (usePreCulling)
            FrustumCullChunks(GameEngine.Instance.ActiveCamera!.View, GameEngine.Instance.ActiveCamera!.Projection);

        occlusionTechnique.Bind();
        occlusionTechnique.BindBuffer(0, BufferManager.ChunkPositionsBuffer);
        occlusionTechnique.BindBuffer(2, visibilityBuffer);

        BufferManager.Buffer.Bind();
        BufferManager.CullIndirectBuffer.Bind();
        BufferManager.RenderAll();

        occlusionTechnique.Unbind();
    }

    private void TryCollectMetrics()
    {
        if (!collectMetrics) return;

        if (!averageTimes.IsFull)
        {
            averageTimes.Append((cullQuery.GetParameter()) / 1000000.0);
            averageShadowFrameTimes.Append(shadowQuery.GetParameter() / 1000000.0);
            averageDrawFrameTimes.Append(drawQuery.GetParameter() / 1000000.0);
        }
        else if (!hasBeenLogged)
        {
            hasBeenLogged = true;
            Console.WriteLine("Logged!");
            using var stream = new StreamWriter(File.Open("metrics.txt", FileMode.Append, FileAccess.Write));

            const string title = "combined no pre cull";
            stream.WriteLine($"Culling pass ({title}):\t\t{genData(averageTimes.Buffer)}");
            stream.WriteLine($"Drawing pass ({title}):\t\t{genData(averageDrawFrameTimes.Buffer)}");
            stream.WriteLine($"Shadows pass ({title}):\t\t{genData(averageShadowFrameTimes.Buffer)}");
            stream.WriteLine();
            stream.Close();
        }
    }
}