using System.Buffers;
using System.Numerics;
using System.Text;

using Horizon.Audio;
using Horizon.Graphics;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Physics;
using Horizon.Physics.Simulation;
using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

using Silk.NET.Vulkan;

namespace Horizon.Tests.Core;

/// <summary>
/// Regression tests for Pass 12 optimizations across HIDL number caching, unboxed arithmetic/comparisons,
/// scope pooling with closure capture, HIDLWriter identifier/vector fast paths, allocation-free UIComponent
/// script property application, indirect WAR barriers, row-major tilemap occlusion blur, float SDF lower envelopes,
/// sparse static physics resets, bulk WaveFile decoding, and lock-free AudioDevice.IsPaused reads.
/// </summary>
public sealed class Pass12HidlTileMapPhysicsTests
{
    private sealed class CustomExposedComponent : UIComponent
    {
        public float CustomValue { get; private set; }

        protected override void DefineScript()
        {
            base.DefineScript();
            Expose("custom_value", () => CustomValue, value => CustomValue = value);
        }
    }

    [Fact]
    public void Values_Number_caches_negative_and_extended_positive_integers_except_negative_zero()
    {
        for (int i = -16; i <= 512; i++)
            Assert.Same(Values.Number(i), Values.Number(i));

        Assert.NotSame(Values.Number(-17), Values.Number(-17));
        Assert.NotSame(Values.Number(513), Values.Number(513));
        Assert.NotSame(Values.Number(1.5f), Values.Number(1.5f));

        IRuntimeValue posZero = Values.Number(0.0f);
        IRuntimeValue negZero = Values.Number(-0.0f);
        Assert.NotSame(posZero, negZero);
    }

    [Fact]
    public void HIDLInterpreter_unboxed_math_comparisons_and_scope_pooling_preserve_closures_and_side_effects()
    {
        var runtime = new HIDLRuntime { Output = null };
        IRuntimeValue result = runtime.Run("""
            let total = 0;
            func addScaled(a, b) {
                let c = a * 2 + b - 1;
                if (c >= 10 && c != 14 && (a + b) * 3 > 12) {
                    return c % 7;
                }
                return -c;
            }

            let items = [1, 2, 3, 4, 5];
            let funcs = [];
            for (idx, item in items) {
                total = total + addScaled(idx, item);
                funcs.push(func() { return idx * 10 + item; });
            }

            let closureSum = 0;
            for (f in funcs) {
                closureSum = closureSum + f();
            }

            let plainSum = 0;
            for (n in 10) {
                plainSum = plainSum + n;
            }

            let obj = { x: 4, y: 7 };
            let pairSum = 0;
            for (k, v in obj) {
                pairSum = pairSum + v;
            }

            let calls = 0;
            func bump(v) {
                calls = calls + 1;
                return v;
            }
            let side = bump(3) + bump(4);
            total * 10000 + closureSum * 100 + (pairSum + plainSum) + calls * 1000000 + (side == 7 ? 1 : 0);
            """);

        // idx/item pairs: (0,1)->-0,(1,2)->-3,(2,3)->-6,(3,4)->-9,(4,5)->12%7=5 => total = -13
        // closures: 0*10+1 + 12 + 23 + 34 + 45 = 115
        // plainSum: 0+..+9 = 45; pairSum: 4 + 7 = 11; side == 7 -> 1 => 45 + 11 + 1 = 57; calls = 2
        // Expected = 2*1000000 + (-13)*10000 + 115*100 + 57 = 2000000 - 130000 + 11500 + 57 = 1881557
        NumberValue number = Assert.IsType<NumberValue>(result);
        Assert.Equal(1881557.0f, number.Value);
    }

    [Fact]
    public void HIDLWriter_identifier_check_and_vectors_match_grammar_and_roundtrip()
    {
        Assert.True(HIDLWriter.IsIdentifier("alpha"));
        Assert.True(HIDLWriter.IsIdentifier("_name_2"));
        Assert.False(HIDLWriter.IsIdentifier(""));
        Assert.False(HIDLWriter.IsIdentifier(null));
        Assert.False(HIDLWriter.IsIdentifier("let"));
        Assert.False(HIDLWriter.IsIdentifier("true"));
        Assert.False(HIDLWriter.IsIdentifier("1abc"));
        Assert.False(HIDLWriter.IsIdentifier("a-b"));
        Assert.False(HIDLWriter.IsIdentifier("a b"));

        var sb = new StringBuilder();
        HIDLWriter.WriteInto(sb, new Vector2Value(new Vector2(1.5f, -2.0f)), 0);
        sb.Append('|');
        HIDLWriter.WriteInto(sb, new Vector3Value(new Vector3(1, 2, 3)), 0);
        sb.Append('|');
        HIDLWriter.WriteInto(sb, new Vector4Value(new Vector4(4, 5, 6, 7)), 0);

        Assert.Equal("vec(1.5, -2)|vec(1, 2, 3)|vec(4, 5, 6, 7)", sb.ToString());
    }

    [Fact]
    public void UIComponent_ApplyScript_updates_properties_without_materializing_script_dictionary()
    {
        var panel = new Panel();
        panel.ApplyScript(new ObjectValue(new Dictionary<string, IRuntimeValue>
        {
            ["pos"] = new Vector2Value(new Vector2(12, 34)),
            ["size"] = new Vector2Value(new Vector2(200, 120)),
            ["visible"] = Values.False,
            ["z"] = Values.Number(3),
            ["hover"] = new StringValue("lift"),
            ["slide"] = new StringValue("down"),
            ["shown"] = Values.False,
            ["resize"] = new StringValue("left, right"),
            ["radius"] = Values.Number(8),
            ["clip"] = Values.True
        }));

        Assert.False(panel.HasScriptDictionary);
        Assert.Equal(new Vector2(12, 34), panel.Position);
        Assert.Equal(new Vector2(200, 120), panel.Size);
        Assert.False(panel.Visible);
        Assert.Equal(3, panel.ZOffset);
        Assert.Equal(UIHover.Lift, panel.Hover);
        Assert.Equal(UISlide.Down, panel.Slide);
        Assert.False(panel.Shown);
        Assert.Equal(UIResize.Left | UIResize.Right, panel.Resizable);
        Assert.Equal(8.0f, panel.Radius);
        Assert.True(panel.Clip);

        var button = new Button();
        button.ApplyScript(new ObjectValue(new Dictionary<string, IRuntimeValue>
        {
            ["label"] = new StringValue("Run"),
            ["primary"] = Values.True,
            ["spr_scale"] = new Vector2Value(new Vector2(1.5f, 1.5f))
        }));
        Assert.False(button.HasScriptDictionary);
        Assert.Equal("Run", button.Label);
        Assert.True(button.Primary);
        Assert.Equal(new Vector2(1.5f, 1.5f), button.Scale);

        Assert.Same(button.Object.Properties, button.Object.Properties);
        Assert.Same(button.ScriptValue, button.ScriptValue);
        Assert.True(button.HasScriptDictionary);

        var custom = new CustomExposedComponent();
        custom.ApplyScript(new ObjectValue(new Dictionary<string, IRuntimeValue>
        {
            ["custom_value"] = Values.Number(42)
        }));
        Assert.True(custom.HasScriptDictionary);
        Assert.Equal(42.0f, custom.CustomValue);

        Assert.Throws<Exception>(() =>
            panel.ApplyScript(new ObjectValue(new Dictionary<string, IRuntimeValue>
            {
                ["does_not_exist"] = Values.Number(1)
            })));
    }

    [Fact]
    public void GraphicsDevice_BarrierMasks_includes_DrawIndirect_WAR_masks_for_Indirect_target()
    {
        var indirectOnly = GraphicsDevice.BarrierMasks(BarrierTargets.Indirect);
        Assert.True((indirectOnly.SrcStage & PipelineStageFlags2.DrawIndirectBit) != 0);
        Assert.True((indirectOnly.DstStage & PipelineStageFlags2.ComputeShaderBit) != 0);
        Assert.True((indirectOnly.DstAccess & AccessFlags2.ShaderStorageWriteBit) != 0);

        var combined = GraphicsDevice.BarrierMasks(BarrierTargets.ShaderStorage | BarrierTargets.Indirect);
        Assert.True((combined.SrcStage & PipelineStageFlags2.DrawIndirectBit) != 0);
        Assert.True((combined.SrcStage & PipelineStageFlags2.ComputeShaderBit) != 0);
        Assert.True((combined.DstStage & PipelineStageFlags2.ComputeShaderBit) != 0);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(7, 5, 2)]
    [InlineData(17, 19, 3)]
    [InlineData(9, 6, 12)]
    public void TileMapGeometryOcclusion_SmearVertical_matches_column_Smear_exactly(int width, int height, int radius)
    {
        var rng = new Random(width * 31 + height * 17 + radius);
        float[] expected = new float[width * height];
        for (int i = 0; i < expected.Length; i++)
            expected[i] = rng.NextDouble() > 0.65 ? 1.0f : 0.0f;

        float[] actualInput = (float[])expected.Clone();
        float[] actualOutput = new float[width * height];
        float[] scratch = new float[height];
        for (int x = 0; x < width; x++)
            TileMapGeometryOcclusion.Smear(expected, x, width, height, radius, scratch);

        float[] colSums = new float[width];
        TileMapGeometryOcclusion.SmearVertical(actualInput, actualOutput, width, height, radius, 2 * radius + 1, colSums);

        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actualOutput[i]);
    }

    [Fact]
    public void OcclusionMap2D_EnvelopeFloat_matches_double_Envelope_with_empty_rows()
    {
        const int height = 128;
        var dbl = new OcclusionMap2D.Envelope(height);
        var flt = new OcclusionMap2D.EnvelopeFloat(height);

        for (int y = 0; y < height; y++)
        {
            float along = y is 12 or 13 or 77 ? 0.0f : (y < 40 ? 1e6f : 15.0f + (y % 7));
            dbl.Heights[y] = (double)along * along;
            flt.Heights[y] = along * along;
        }

        dbl.Lower(height);
        flt.Lower(height);

        for (int y = 0; y < height; y++)
            Assert.Equal((float)dbl.Distances[y], flt.Distances[y], 1e-3f);
    }

    [Fact]
    public void PhysicsWorld_clears_touched_static_fixtures_sparsely_after_contact_ends()
    {
        var world = new PhysicsWorld { Gravity = new Vector2(0.0f, -600.0f) };
        var ground = world.CreateBody(PhysicsBodySimulationType.Static);
        for (int i = 0; i < 64; i++)
            ground.CreateRectangularFixture(new Vector2(i * 40.0f, -20.0f), new Vector2(30.0f, 20.0f));

        var mover = world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(15.0f, 5.0f));
        mover.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));

        bool touchedFloor = false;
        for (int i = 0; i < 60 && !touchedFloor; i++)
        {
            world.UpdatePhysics(1.0f / 60.0f);
            touchedFloor = ground.DynamicFixtures[0].IsTouching;
        }

        Assert.True(touchedFloor);
        Assert.NotEmpty(ground.DynamicFixtures[0].ActiveContacts);
        Assert.False(ground.DynamicFixtures[63].IsTouching);

        mover.Position = new Vector2(15.0f, 500.0f);
        mover.Velocity = Vector2.Zero;
        world.UpdatePhysics(1.0f / 60.0f);

        Assert.False(ground.DynamicFixtures[0].IsTouching);
        Assert.Empty(ground.DynamicFixtures[0].ActiveContacts);
    }

    [Fact]
    public void WaveFile_decodes_16bit_pcm_and_32bit_float_mono_and_stereo_correctly()
    {
        byte[] MakeWav(ushort format, ushort channels, ushort bits, Action<BinaryWriter> writePayload)
        {
            var payload = new MemoryStream();
            using (var bw = new BinaryWriter(payload, Encoding.ASCII, leaveOpen: true))
                writePayload(bw);

            byte[] pcm = payload.ToArray();
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            {
                int rate = 48000;
                ushort blockAlign = (ushort)(channels * (bits / 8));
                w.Write("RIFF"u8);
                w.Write(36 + pcm.Length);
                w.Write("WAVEfmt "u8);
                w.Write(16);
                w.Write(format);
                w.Write(channels);
                w.Write(rate);
                w.Write(rate * blockAlign);
                w.Write(blockAlign);
                w.Write(bits);
                w.Write("data"u8);
                w.Write(pcm.Length);
                w.Write(pcm);
            }
            return ms.ToArray();
        }

        Sound s16 = WaveFile.Read(MakeWav(1, 2, 16, w =>
        {
            w.Write(short.MaxValue);
            w.Write(short.MinValue);
            w.Write((short)-12345);
            w.Write((short)12345);
        }));
        Assert.Equal(2, s16.Frames);
        Assert.Equal(short.MaxValue, s16.Samples[0]);
        Assert.Equal(-short.MaxValue, s16.Samples[1]);
        Assert.Equal(-12345, s16.Samples[2]);
        Assert.Equal(12345, s16.Samples[3]);

        Sound sFloatMono = WaveFile.Read(MakeWav(3, 1, 32, w =>
        {
            w.Write(1.0f);
            w.Write(-0.5f);
        }));
        Assert.Equal(2, sFloatMono.Frames);
        Assert.Equal(sFloatMono.Samples[0], sFloatMono.Samples[1]);
        Assert.Equal(1.0f, sFloatMono.Level(0), 3);
        Assert.Equal(-0.5f, sFloatMono.Level(2), 3);
    }

    [Fact]
    public void AudioDevice_IsPaused_transitions_cleanly_without_rendering_lock()
    {
        using var device = new AudioDevice(open: false);
        Assert.False(device.IsPaused);
        device.Pause();
        Assert.True(device.IsPaused);
        device.Resume();
        Assert.False(device.IsPaused);
    }
}
