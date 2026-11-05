using System.Globalization;
using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.Scaling;

namespace Horizon.UI.Components;

/// <summary>
/// The selectors an options screen wants for the render scale, wired to the window's <see cref="FrameScaler"/> both
/// ways, with a line under them saying what the frame does now (the scale it came to, the upscaler and the scene's
/// and the window's sizes). Picking something here sets <c>WindowManager.Scaling.Settings</c> there and then, it takes
/// at the next frame, and when somebody else sets them (a key, another screen, a check) the selectors show that.
/// <code>
/// options.Add(new RenderScalePicker { OnPicked = settings => File.WriteAllText(path, RenderScaleEnvironment.Format(settings)) });
/// </code>
/// The scale is Auto (720p, the scene's short edge 720 pixels in the window's shape, see <see cref="RenderScalePolicy.Automatic"/>),
/// Native, or a share of the window. The upscaler is CAS, bilinear or nearest, and Custom when the settings have an
/// upscaler of somebody's own in them. The UI is drawn at the window's pixels over the scene, or with the whole frame.
/// None of it changes how big the UI is laid out, only how many pixels the frame is drawn with.
/// </summary>
public sealed class RenderScalePicker : StackPanel
{
    // What the scale selector offers, in the order it steps, Auto and Native first
    private static readonly string[] ScaleOptions = ["Auto 720p", "Native", "90%", "80%", "75%", "70%", "60%", "50%", "40%", "33%", "25%"];
    private static readonly float[] ScaleValues = [0.0f, 1.0f, 0.9f, 0.8f, 0.75f, 0.7f, 0.6f, 0.5f, 0.4f, 0.33f, 0.25f];
    private static readonly string[] SharpnessOptions = ["sharpness 0", "sharpness 0.25", "sharpness 0.5", "sharpness 0.75", "sharpness 1"];
    private static readonly float[] SharpnessValues = [0.0f, 0.25f, 0.5f, 0.75f, 1.0f];
    private static readonly string[] CompositionOptions = ["UI at full size", "Whole frame"];
    private static readonly Upscaler[] BuiltIn = [Upscaler.Cas, Upscaler.Gsr1, Upscaler.Fsr1, Upscaler.Bilinear, Upscaler.Nearest];

    // What the upscaler selector stands for, in its order
    private Upscaler[] upscalers = BuiltIn;

    private readonly Selector scale, upscaler, sharpness, composition;
    private readonly Label readout;
    private int seen = -1;
    private RenderScalePlan? shownPlan;
    private bool showing;

    /// <summary>Called when the player picked something here, with the settings as they are now.</summary>
    public Action<RenderScaleSettings>? OnPicked { get; set; }

    /// <summary>The scale of the text, 0 for the skin's.</summary>
    public float TextScale
    {
        get => scale.TextScale;
        set => scale.TextScale = upscaler.TextScale = sharpness.TextScale = composition.TextScale = readout.TextScale = value;
    }

    public RenderScalePicker()
    {
        Spacing = 6.0f;

        var row = Add(new StackPanel { Anchor = Origin.Left, Direction = UIDirection.Horizontal, Spacing = 10.0f });
        scale = row.Add(new Selector { Size = new Vector2(150.0f, 0.0f), Options = ScaleOptions, Tooltip = "How many of the window's pixels the scene is drawn with, Auto goes by the screen" });
        upscaler = row.Add(new Selector { Size = new Vector2(150.0f, 0.0f), Tooltip = "What blows the scene back up to the window" });
        sharpness = row.Add(new Selector { Size = new Vector2(200.0f, 0.0f), Options = SharpnessOptions, Tooltip = "How hard CAS, GSR 1 or FSR 1 sharpen" });
        composition = row.Add(new Selector { Size = new Vector2(200.0f, 0.0f), Options = CompositionOptions, Tooltip = "Whether the UI is drawn at the window's pixels over the scene, or small with the whole frame" });
        readout = Add(new Label { Anchor = Origin.Left });

        scale.OnChanged = _ => Pick();
        upscaler.OnChanged = _ => Pick();
        sharpness.OnChanged = _ => Pick();
        composition.OnChanged = _ => Pick();

        Show();
    }

    private static FrameScaler? Scaling => GameEngine.Instance?.WindowManager?.Scaling;

    /// <summary>Helper method to put what was picked here in force.</summary>
    private void Pick()
    {
        if (showing || Scaling is not { } scaling)
            return;

        RenderScaleSettings now = scaling.Settings;
        int at = Math.Clamp(scale.Index, 0, ScaleValues.Length - 1);
        now = at switch
        {
            0 => now with { Policy = RenderScalePolicy.Automatic },
            1 => now with { Policy = RenderScalePolicy.Native, Scale = 1.0f },
            _ => now with { Policy = RenderScalePolicy.Manual, Scale = ScaleValues[at] }
        };

        now = now with
        {
            Upscaler = upscalers[Math.Clamp(upscaler.Index, 0, upscalers.Length - 1)],
            Sharpness = SharpnessValues[Math.Clamp(sharpness.Index, 0, SharpnessValues.Length - 1)],
            Composition = composition.Index == 1 ? RenderComposition.WholeFrame : RenderComposition.NativeOverlay
        };

        scaling.Settings = now;
        Show();
        OnPicked?.Invoke(now);
    }

    /// <summary>Helper method to have the selectors say what the settings are.</summary>
    private void Show()
    {
        if (Scaling is not { } scaling)
            return;

        showing = true;
        seen = scaling.Version;
        RenderScaleSettings now = scaling.Settings.Sanitized();

        // Auto says the short edge it draws at, 720p unless the program asked for another
        string[] scales = (string[])ScaleOptions.Clone();
        scales[0] = $"Auto {now.ShortEdge}p";
        scale.Options = scales;

        scale.Index = now.Policy switch
        {
            RenderScalePolicy.Automatic => 0,
            RenderScalePolicy.Density => 0,
            RenderScalePolicy.Native => 1,
            _ => Nearest(ScaleValues, now.Scale, from: 2)
        };

        // The custom one is offered only when the settings have one to run
        upscalers = now.Custom is null ? BuiltIn : [.. BuiltIn, Upscaler.Custom];
        upscaler.Options = [.. upscalers.Select(RenderScalePlan.NameOf)];
        upscaler.Index = Math.Max(0, Array.IndexOf(upscalers, now.Upscaler));
        sharpness.Index = Nearest(SharpnessValues, now.Sharpness, from: 0);
        composition.Index = now.Composition == RenderComposition.WholeFrame ? 1 : 0;

        showing = false;
    }

    // The option nearest a value, a scale set in code (0.62) shows as the step it is closest to
    private static int Nearest(float[] values, float value, int from)
    {
        int best = from;
        for (int i = from; i < values.Length; i++)
        {
            if (MathF.Abs(values[i] - value) < MathF.Abs(values[best] - value))
                best = i;
        }

        return best;
    }

    /// <summary>What a plan comes to, in a line, the way the readout says it.</summary>
    public static string Describe(RenderScalePlan plan, uint shortEdge = RenderScaleSettings.DEFAULT_SHORT_EDGE)
    {
        string scale = plan.EffectiveScale.ToString("0%", CultureInfo.InvariantCulture);
        string policy = plan.Policy switch
        {
            RenderScalePolicy.Automatic => $"Auto {shortEdge}p ({scale})",
            RenderScalePolicy.Density => $"By density {scale}",
            RenderScalePolicy.Manual => scale,
            _ => "Native"
        };

        if (!plan.Active)
            return $"{policy}, drawn at {plan.OutputWidth} x {plan.OutputHeight} as it is";

        string ui = plan.Composition == RenderComposition.WholeFrame ? "the whole frame" : "the scene, UI at full size";
        string fallback = plan.Effective != plan.Requested ? $" ({plan.Requested} can't, see the log)" : "";
        return $"{policy} of {ui}, {plan.SceneWidth} x {plan.SceneHeight} to {plan.OutputWidth} x {plan.OutputHeight} with {plan.Effective}{(plan.HasPrepass ? $" after a stretch to {plan.PrepassWidth} x {plan.PrepassHeight}" : "")}{fallback}";
    }

    protected override void Update(float dt)
    {
        base.Update(dt);

        if (Scaling is not { } scaling)
            return;

        // Somebody else changed it
        if (seen != scaling.Version)
            Show();

        RenderScalePlan plan = scaling.Plan;
        if (!ReferenceEquals(plan, shownPlan))
        {
            shownPlan = plan;
            readout.Text = Describe(plan, scaling.Settings.Sanitized().ShortEdge);
        }
    }
}
