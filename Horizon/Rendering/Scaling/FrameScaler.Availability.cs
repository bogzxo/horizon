using Horizon.Content;
using Horizon.Logging;
using Horizon.Rendering.PostProcessing;

namespace Horizon.Rendering.Scaling;

public enum UpscalerAvailabilityState { Unknown, Available, Unavailable }

/// <summary>Whether this process could load every shader needed by an upscaler.</summary>
public sealed record UpscalerAvailability(UpscalerAvailabilityState State, string? Reason = null);

public sealed partial class FrameScaler
{
    private readonly Lock availabilityLock = new();
    private readonly Dictionary<Upscaler, UpscalerAvailability> availability = [];
    private int availabilityVersion, plannedAvailability = -1;
    private int probeRequested;
    private static readonly UpscalerAvailability unknown = new(UpscalerAvailabilityState.Unknown);

    /// <summary>Reads the last probe result from any thread. Unknown means not tried yet, not unsupported.</summary>
    public UpscalerAvailability GetAvailability(Upscaler upscaler)
    {
        lock (availabilityLock) return availability.GetValueOrDefault(upscaler, unknown);
    }

    /// <summary>Checks all built-in passes next frame, on the render thread. Does not change saved settings.</summary>
    public void RequestAvailabilityProbe() => Interlocked.Exchange(ref probeRequested, 1);

    /// <summary>Raised on the render thread when a probe result changes.</summary>
    public event Action? AvailabilityChanged;

    private void ProbeAll()
    {
        foreach (Upscaler upscaler in new[] { Upscaler.Cas, Upscaler.Bilinear, Upscaler.Nearest, Upscaler.Gsr1, Upscaler.Fsr1 })
            EnsureAvailable(upscaler);
    }

    private bool EnsureAvailable(Upscaler upscaler)
    {
        if (upscaler == Upscaler.Custom) return true;
        UpscalerAvailability known = GetAvailability(upscaler);
        if (known.State != UpscalerAvailabilityState.Unknown)
            return known.State == UpscalerAvailabilityState.Available;

        string? reason = null;
        bool Load(ref PostTechnique? technique, string name)
        {
            try
            {
                using var global = AssetScope.EnterGlobal();
                technique ??= new PostTechnique(name);
                if (technique.IsValid) return true;
                reason = $"shaders/post/{name}.slang is unavailable; see the shader load error";
            }
            catch (Exception e)
            {
                reason = $"shaders/post/{name}.slang could not load: {e.Message}";
            }
            return false;
        }

        bool valid = upscaler switch
        {
            Upscaler.Cas => Load(ref cas, "cas"),
            Upscaler.Bilinear => Load(ref bilinear, "copy"),
            Upscaler.Nearest => Load(ref nearest, "upscale_nearest"),
            Upscaler.Gsr1 => Load(ref gsr1, "gsr1"),
            // Both passes must be valid before drawing either one.
            Upscaler.Fsr1 => Load(ref easu, "fsr1_easu") & Load(ref rcas, "fsr1_rcas"),
            _ => false
        };
        lock (availabilityLock)
            availability[upscaler] = new(valid ? UpscalerAvailabilityState.Available : UpscalerAvailabilityState.Unavailable, reason);
        availabilityVersion++;
        if (!valid) Log.Error($"[RenderScale] {RenderScalePlan.NameOf(upscaler)} unavailable: {reason}. The requested setting is preserved; a working fallback is used.");
        AvailabilityChanged?.Invoke();
        return valid;
    }

    private RenderScalePlan Usable(RenderScaleSettings settings, RenderScalePlan requested, uint width, uint height, float density, uint most) =>
        ResolveAvailablePlan(settings, requested, most, EnsureAvailable);

    /// <summary>Selects a working pass before allocating the scene or redirecting window draws.</summary>
    internal static RenderScalePlan ResolveAvailablePlan(RenderScaleSettings settings, RenderScalePlan requested, uint most, Func<Upscaler, bool> available)
    {
        // Native desktop frames never load shaders or allocate an intermediate target.
        if (!requested.Active || (available(requested.Effective) && (!requested.HasPrepass || available(Upscaler.Bilinear)))) return requested;

        foreach (Upscaler candidate in new[] { Upscaler.Cas, Upscaler.Bilinear })
        {
            if (candidate == requested.Effective) continue;
            RenderScalePlan fallback = RenderScalePlan.For(settings with { Upscaler = candidate }, requested.OutputWidth, requested.OutputHeight, requested.Density, most);
            if (available(fallback.Effective) && (!fallback.HasPrepass || available(Upscaler.Bilinear)))
                return fallback with { Requested = requested.Requested, Fallback = $"{RenderScalePlan.NameOf(requested.Effective)} is unavailable, {RenderScalePlan.NameOf(fallback.Effective)} does it instead" };
        }

        return requested with { SceneWidth = requested.OutputWidth, SceneHeight = requested.OutputHeight, PrepassWidth = 0, PrepassHeight = 0,
            Fallback = "no working upscaler is available, rendering at native resolution" };
    }
}
