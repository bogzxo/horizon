using System.Reflection;

using Horizon.Logging;

namespace Horizon.Graphics.Vulkan;

/// <summary>
/// What the Vulkan loader is told before anything of Vulkan is asked, by this process's environment and nobody else's.
/// <para>
/// The loader puts every implicit layer installed on the machine into every Vulkan program, the overlays and recorders
/// of whatever's installed (Overwolf, Steam, an FPS counter), each a DLL loaded and hooked into making the instance and
/// the device, and a good part of what starting took. They're left out (VK_LOADER_LAYERS_DISABLE), all but the GPU
/// makers' own (VK_LOADER_LAYERS_ALLOW), unless
/// HORIZON_VULKAN_LAYERS=all says otherwise, or RenderDoc started the game to capture it, or somebody set the variable
/// themselves. Layers asked for by name (validation) are a different thing and aren't touched.
/// </para>
/// <para>
/// It also loads every driver installed and asks each for its cards, which a laptop with two GPUs pays for twice. The
/// vendor of the card picked last time is kept, and only that vendor's driver is loaded next time (VK_LOADER_DRIVERS_SELECT).
/// If it finds nothing it can use (the card was swapped) it's forgotten and Vulkan is started again with every driver.
/// HORIZON_VULKAN_DRIVER=all turns that off.
/// </para>
/// Loaders older than 1.3.234 don't know the variables and do as they always did.
/// </summary>
internal static class VulkanLoader
{
    private const string LAYERS_DISABLE = "VK_LOADER_LAYERS_DISABLE", LAYERS_ALLOW = "VK_LOADER_LAYERS_ALLOW", DRIVERS_SELECT = "VK_LOADER_DRIVERS_SELECT";

    // The GPU makers' own layers, which are in the driver that's loaded anyway and do what the card needs (NVIDIA's
    // windowed presenting and G-SYNC, which GPU a laptop with two draws on), let back in past the filter
    private const string VENDOR_LAYERS = "*_NV_*,*_AMD_*,*_INTEL_*,*_MESA_*";
    private const string LAYERS_SETTING = "HORIZON_VULKAN_LAYERS", DRIVER_SETTING = "HORIZON_VULKAN_DRIVER";
    private const string RENDERDOC = "ENABLE_VULKAN_RENDERDOC_CAPTURE";
    private const string DRIVER_FILE = "vulkan_driver.txt";

    private static readonly Lock gate = new();
    private static bool configured;

    // The driver this process is asking the loader for, null for every one
    private static string? selected;

    /// <summary>Tells the loader the above, once, before the first thing of Vulkan or the window. Any thread.</summary>
    public static void Configure()
    {
        lock (gate)
        {
            if (configured)
                return;
            configured = true;

            if (Environment.GetEnvironmentVariable(LAYERS_DISABLE) is null && !Asked(LAYERS_SETTING) && Environment.GetEnvironmentVariable(RENDERDOC) is null)
            {
                Environment.SetEnvironmentVariable(LAYERS_DISABLE, "~implicit~");
                if (Environment.GetEnvironmentVariable(LAYERS_ALLOW) is null)
                    Environment.SetEnvironmentVariable(LAYERS_ALLOW, VENDOR_LAYERS);
                Log.Info($"[Vulkan] Implicit layers (overlays and the like) are left out, {LAYERS_SETTING}=all keeps them.");
            }

            if (Environment.GetEnvironmentVariable(DRIVERS_SELECT) is null && !Asked(DRIVER_SETTING) && Remembered() is { } driver)
            {
                selected = driver;
                Environment.SetEnvironmentVariable(DRIVERS_SELECT, driver);
            }
        }
    }

    /// <summary>
    /// Asks the loader for every driver again after the one remembered found nothing that would do. True when there was
    /// one to forget, and the instance is worth making again.
    /// </summary>
    public static bool ForgetDriver()
    {
        lock (gate)
        {
            if (selected is null)
                return false;

            Log.Warning($"[Vulkan] The driver used last time ({selected}) found no card that would do, asking every driver instead.");
            selected = null;
            Environment.SetEnvironmentVariable(DRIVERS_SELECT, null);
            TryDelete();
            return true;
        }
    }

    /// <summary>Keeps the vendor of the card that was picked, for the next start to load only its driver.</summary>
    public static void Remember(uint vendor)
    {
        // The start of the name of each vendor's driver manifest (nv-vk64.json, amd-vulkan64.json, igvk64.json)
        string? driver = vendor switch
        {
            0x10DE => "*nv-vk*",
            0x1002 => "*amd*",
            0x8086 => "*igvk*",
            _ => null
        };

        string? file = FilePath();
        if (file is null)
            return;

        try
        {
            if (driver is null)
                TryDelete();
            else if (!File.Exists(file) || File.ReadAllText(file).Trim() != driver)
                File.WriteAllText(file, driver);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not kept, every driver is loaded next time as well, which is all that happens
        }
    }

    private static bool Asked(string setting) => string.Equals(Environment.GetEnvironmentVariable(setting), "all", StringComparison.OrdinalIgnoreCase);

    private static string? Remembered()
    {
        try
        {
            return FilePath() is { } file && File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } driver ? driver : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete()
    {
        try
        {
            if (FilePath() is { } file && File.Exists(file))
                File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Next to the game's compiled shaders, by the game rather than the process (which is dotnet for one started through it)
    private static string? FilePath()
    {
        try
        {
            string game = Assembly.GetEntryAssembly()?.GetName().Name ?? "Horizon";
            return Path.Combine(Horizon.Core.Platform.PlatformPaths.Cache(Path.Combine("Horizon", game)), DRIVER_FILE);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
