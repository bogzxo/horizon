using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AndroidBuild = Android.OS.Build;

using Horizon.Content;
using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Logging;
using Horizon.Platform;
using Horizon.Platform.Android;

namespace Horizon.Testing.Android;

/// <summary>
/// The checks of <see cref="DeviceChecksScene"/>, each a name, whether it held and what was seen. Run on a pool thread,
/// one after another, never on the game's threads. A check that can't be made here (no audio stream with
/// HORIZON_AUDIO=off) says it was skipped rather than passing.
/// </summary>
internal sealed class DeviceChecks
{
    private readonly Lock gate = new();
    private readonly List<DeviceCheck> results = [];
    private bool finished;
    private bool written;

    /// <summary>Whether every check has been made and the report written.</summary>
    public bool Finished { get { lock (gate) return finished && written; } }

    /// <summary>How many passed, failed and were skipped so far.</summary>
    public (int Passed, int Failed, int Skipped) Tally
    {
        get
        {
            lock (gate)
            {
                int passed = results.Count(result => result.Outcome == Outcome.Passed), failed = results.Count(result => result.Outcome == Outcome.Failed);
                return (passed, failed, results.Count - passed - failed);
            }
        }
    }

    /// <summary>What's on the screen, every result so far.</summary>
    public string Summary
    {
        get
        {
            lock (gate)
            {
                var text = new StringBuilder();
                int passed = results.Count(result => result.Outcome == Outcome.Passed), failed = results.Count(result => result.Outcome == Outcome.Failed);
                text.AppendLine(finished ? $"Device checks done, {passed} passed, {failed} failed, {results.Count - passed - failed} skipped" : "Device checks running...");
                foreach (DeviceCheck result in results) text.AppendLine($"{result.Outcome.ToString().ToUpperInvariant()} {result.Name}");
                return text.ToString();
            }
        }
    }

    public void Run(GameEngine engine, AndroidSession? session)
    {
        Make("platform.session", () => session is null ? Fail("there's no Android session") : Pass($"host {session.Host?.GetType().Name}, features {session.Host?.Features}"));
        if (session is not null)
        {
            Make("display.state", () => Display(engine, session));
            Make("content.index", () => ContentIndex(session));
            Make("content.read", () => ContentRead(session));
            Make("content.hashes", () => ContentHashes(session));
            Make("storage.commit-and-abandon", () => Storage(session));
            Make("storage.nested-cache", () => NestedCache(session));
            Make("audio.callbacks", () => Audio(engine, session));
            Make("surface.lifecycle", () => Surface(session));
        }
        Make("gpu.device", () => Gpu(engine));

        lock (gate) finished = true;
        Write(session);
        lock (gate) written = true;
    }

    // One check, which counts a throw as a failure with its message
    private void Make(string name, Func<(Outcome Outcome, string Detail)> check)
    {
        (Outcome outcome, string detail) result;
        try
        {
            result = check();
        }
        catch (Exception e)
        {
            result = (Outcome.Failed, $"threw {e.GetType().Name}, {e.Message}");
        }

        var made = new DeviceCheck(name, result.outcome, result.detail);
        lock (gate) results.Add(made);

        // So a run of every check through the host names them too
        if (made.Outcome == Outcome.Failed) CheckLog.Failed("Device", $"{made.Name}, {made.Detail}");
        else if (made.Outcome == Outcome.Skipped) CheckLog.Skipped("Device", made.Name, made.Detail);
        LogLevel level = made.Outcome == Outcome.Failed ? LogLevel.Error : LogLevel.Info;
        Log.Write(level, $"[Checks] {made.Outcome.ToString().ToUpperInvariant()} {made.Name}, {made.Detail}");
    }

    private static (Outcome, string) Pass(string detail) => (Outcome.Passed, detail);
    private static (Outcome, string) Fail(string detail) => (Outcome.Failed, detail);
    private static (Outcome, string) Skip(string detail) => (Outcome.Skipped, detail);

    private static (Outcome, string) Display(GameEngine engine, AndroidSession session)
    {
        AndroidDisplayInfo screen = session.Screen;
        System.Numerics.Vector2 viewport = engine.WindowManager.ViewportSize;
        SurfaceSize surface = session.Host?.Surface.PixelSize ?? default;
        string seen = $"viewport {viewport.X}x{viewport.Y}, surface {surface.Width}x{surface.Height}, screen {screen.ScreenSize.X}x{screen.ScreenSize.Y}, density {screen.Density} ({screen.DensityDpi} dpi), {screen.RefreshRate:0.#} Hz, rotation {screen.Rotation}";

        // While the surface is there the engine draws at its size, a mismatch is a resize that never got through
        if (!surface.IsEmpty && (viewport.X != surface.Width || viewport.Y != surface.Height)) return Fail(seen);
        return Pass(seen);
    }

    private static (Outcome, string) ContentIndex(AndroidSession session)
    {
        AndroidContent content = session.Runtime.Content;
        int assets = content.EnumerateFiles("Assets", "*", SearchOption.AllDirectories).Count();
        int shaders = content.EnumerateFiles("shaders", "*", SearchOption.AllDirectories).Count();
        string[] packs = [.. content.EnumerateFiles("shaders/packed", "*.hzshaders")];

        var problems = new List<string>();
        if (assets == 0) problems.Add("no files under Assets");
        if (packs.Length == 0) problems.Add("no shader pack under shaders/packed");
        if (!content.DirectoryExists("Assets/fonts")) problems.Add("no Assets/fonts");
        if (content.DirectoryExists("ASSETS/fonts") || content.DirectoryExists("assets/fonts")) problems.Add("a path in the wrong case was found");

        bool escaped;
        try
        {
            content.ResolvePath("../outside");
            escaped = true;
        }
        catch (Exception)
        {
            escaped = false;
        }
        if (escaped) problems.Add("../outside resolved instead of throwing");

        string seen = $"{assets} files under Assets, {shaders} under shaders, packs {string.Join(", ", packs)}";
        return problems.Count == 0 ? Pass(seen) : Fail($"{string.Join(", ", problems)}. {seen}");
    }

    // The biggest file read in odd sized pieces and then by seeking about, the two must agree byte for byte
    private static (Outcome, string) ContentRead(AndroidSession session)
    {
        AndroidContent content = session.Runtime.Content;
        string? biggest = null;
        long size = -1;
        foreach (string file in content.EnumerateFiles("Assets", "*", SearchOption.AllDirectories))
        {
            using Stream probe = content.OpenRead(file);
            if (probe.Length > size) (biggest, size) = (file, probe.Length);
        }
        if (biggest is null) return Fail("there's nothing under Assets to read");

        byte[] whole;
        using (Stream stream = content.OpenRead(biggest))
        {
            using var copy = new MemoryStream();
            byte[] piece = new byte[7];
            int read;
            while ((read = stream.Read(piece, 0, piece.Length)) > 0) copy.Write(piece, 0, read);
            whole = copy.ToArray();
        }
        if (whole.Length != size) return Fail($"{biggest} read {whole.Length} bytes of {size}");

        using (Stream stream = content.OpenRead(biggest))
        {
            int tail = (int)Math.Min(100, size);
            stream.Seek(-tail, SeekOrigin.End);
            byte[] end = new byte[tail];
            stream.ReadExactly(end);
            if (!end.AsSpan().SequenceEqual(whole.AsSpan((int)size - tail))) return Fail($"{biggest} read differently after seeking to its end");

            stream.Seek(Math.Min(10, size), SeekOrigin.Begin);
            if (stream.Position != Math.Min(10, size)) return Fail("the position after a seek is wrong");

            stream.Seek(size + 5, SeekOrigin.Begin);
            if (stream.Read(new byte[4], 0, 4) != 0) return Fail("reading past the end gave bytes");

            bool threw = false;
            try { stream.Seek(-1, SeekOrigin.Begin); }
            catch (IOException) { threw = true; }
            if (!threw) return Fail("seeking before the start didn't throw");
        }

        return Pass($"{biggest}, {size} bytes in 7 byte pieces and seeks, SHA-256 {Convert.ToHexString(SHA256.HashData(whole))[..16]}");
    }

    // Every file's hash, written into the report so a script can compare them with the desktop copies
    private readonly SortedDictionary<string, string> hashes = new(StringComparer.Ordinal);

    private (Outcome, string) ContentHashes(AndroidSession session)
    {
        AndroidContent content = session.Runtime.Content;
        long bytes = 0;
        foreach (string root in content.Roots)
        {
            if (!content.DirectoryExists(root)) continue;
            foreach (string file in content.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                using Stream stream = content.OpenRead(file);
                bytes += stream.Length;
                hashes[file] = Convert.ToHexString(SHA256.HashData(stream));
            }
        }

        return hashes.Count > 0 ? Pass($"{hashes.Count} files, {bytes} bytes, hashes in report.json") : Fail("no files were hashed");
    }

    private static (Outcome, string) Storage(AndroidSession session)
    {
        AndroidStorage storage = session.Runtime.Storage;
        string folder = storage.State("device-checks");
        string path = Path.Combine(folder, "save.txt");

        PlatformStorage.WriteAllText(path, "first");
        using (IStorageWrite write = storage.BeginWrite(path))
        {
            write.Stream.Write("second"u8);
            write.Commit();
        }
        if (PlatformStorage.ReadAllText(path) != "second") return Fail("a committed save didn't read back");

        using (IStorageWrite write = storage.BeginWrite(path))
        {
            write.Stream.Write("abandoned"u8);
        }
        if (PlatformStorage.ReadAllText(path) != "second") return Fail("a save that was never committed replaced the one before");

        string[] leftovers = Directory.GetFiles(folder).Where(file => !string.Equals(file, path, StringComparison.Ordinal)).ToArray();
        if (leftovers.Length > 0) return Fail($"files left beside the save, {string.Join(", ", leftovers.Select(Path.GetFileName))}");

        var refused = new List<string>();
        foreach (string outside in (string[])[Path.Combine(session.Runtime.Paths.Files, "..", "outside.txt"), "/system/build.prop"])
        {
            try
            {
                storage.BeginWrite(outside).Dispose();
            }
            catch (ArgumentException)
            {
                refused.Add(outside);
            }
        }
        if (refused.Count != 2) return Fail("a path outside the app's folders was allowed");
        if (storage.Exists("/system/build.prop")) return Fail("Exists said yes to a path outside the app's folders");

        storage.Delete(path);
        return Pass($"commit and abandon in {folder}, outside paths refused, roots {storage.Roots.Files} and {storage.Roots.Cache}");
    }

    private static (Outcome, string) NestedCache(AndroidSession session)
    {
        AndroidStorage storage = session.Runtime.Storage;
        string nested = storage.Cache(Path.Combine("Horizon", "Horizon.Testing"));
        if (!nested.StartsWith(storage.Roots.Cache + "/", StringComparison.Ordinal) || !Directory.Exists(nested)) return Fail($"Cache(Horizon/Horizon.Testing) gave {nested}");

        foreach (string bad in (string[])["../x", "/x", "a/../../x", ""])
        {
            try
            {
                storage.Cache(bad);
                return Fail($"Cache('{bad}') was allowed");
            }
            catch (ArgumentException)
            {
            }
        }

        string? shaders = ShaderCompiler.CacheDirectory();
        return Pass($"{nested}, climbing names refused, the shader cache is {(shaders ?? "off")}");
    }

    private static (Outcome, string) Audio(GameEngine engine, AndroidSession session)
    {
        if (session.Audio is not { } output) return Skip("no AAudio output, HORIZON_AUDIO=off or none could be opened");
        if (!output.HasStream) return Fail($"no stream, last error {output.LastError}, {output.ReopenProblem}");

        long before = output.Callbacks;
        Thread.Sleep(1000);
        long during = output.Callbacks;
        if (during <= before) return Fail($"no callbacks in a second ({before})");

        engine.Audio.Pause();
        Thread.Sleep(300);
        long paused = output.Callbacks;
        Thread.Sleep(500);
        long stillPaused = output.Callbacks;
        engine.Audio.Resume();
        Thread.Sleep(500);
        long resumed = output.Callbacks;

        string seen = $"{output.Format.SampleRate} Hz, {output.Format.Channels} channels, {output.Format.FramesPerBuffer} a burst, {during - before} callbacks a second, " +
            $"paused {paused} then {stillPaused}, resumed {resumed}, {output.FramesRequested} frames, {output.Faults} faults, {output.Disconnects} disconnects, {output.Reopens} reopens";
        if (stillPaused != paused) return Fail("callbacks went on while paused. " + seen);
        if (resumed <= stillPaused) return Fail("no callbacks after resuming. " + seen);
        if (output.Faults > 0) return Fail("the render threw in a callback. " + seen);
        return Pass(seen);
    }

    // What happened to the surface so far. Backgrounding and rotating are done by hand or by adb while this runs, the
    // check only reads what's been noted and says whether it holds together
    private static (Outcome, string) Surface(AndroidSession session)
    {
        IReadOnlyList<string> lines = session.Surfaces.Lines;
        AndroidSurface? surface = session.Host?.Surface;
        if (surface is null) return Fail("no host surface");

        string seen = $"generation {surface.Generation}, available {surface.IsAvailable}, {surface.PixelSize.Width}x{surface.PixelSize.Height}. " + string.Join(" | ", lines);
        if (surface.Generation < 1 || !surface.IsAvailable) return Fail(seen);
        return Pass(seen);
    }

    private static (Outcome, string) Gpu(GameEngine engine)
    {
        if (!GraphicsDevice.IsAvailable) return Fail("there's no graphics device");
        GraphicsDevice device = GraphicsDevice.Current;
        string seen = $"{device.Description}, surface {device.SurfaceState}, frame {device.FrameNumber}, GPU {device.GpuFrameMilliseconds:0.00} ms, " +
            $"{device.Statistics.MemoryInUse / (1024.0 * 1024.0):0.0} MB in use in {device.Statistics.MemoryBlocks} blocks, max texture {device.MaxTextureSize}";
        if (device.SurfaceState != SurfaceState.Attached) return Fail("the surface isn't attached. " + seen);
        if (device.FrameNumber < 10) return Fail("hardly any frames were drawn. " + seen);
        return Pass(seen);
    }

    // The report, text for reading and JSON for a script, in the external files folder
    private void Write(AndroidSession? session)
    {
        string folder = Path.Combine(session?.Runtime.Paths.External ?? Path.GetTempPath(), "checks");
        try
        {
            Directory.CreateDirectory(folder);
            DeviceCheck[] made;
            lock (gate) made = [.. results];

            File.WriteAllText(Path.Combine(folder, "report.txt"), string.Join('\n', made.Select(result => $"{result.Outcome.ToString().ToUpperInvariant()} {result.Name}, {result.Detail}")) + "\n");

            using var stream = File.Create(Path.Combine(folder, "report.json"));
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            json.WriteString("device", $"{AndroidBuild.Manufacturer} {AndroidBuild.Model}");
            json.WriteNumber("api", (int)AndroidBuild.VERSION.SdkInt);
            json.WriteString("gpu", AndroidSession.DescribeGraphics());
            json.WriteStartArray("checks");
            foreach (DeviceCheck result in made)
            {
                json.WriteStartObject();
                json.WriteString("name", result.Name);
                json.WriteString("outcome", result.Outcome.ToString().ToLowerInvariant());
                json.WriteString("detail", result.Detail);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartObject("hashes");
            foreach ((string file, string hash) in hashes) json.WriteString(file, hash);
            json.WriteEndObject();
            json.WriteEndObject();

            int failed = made.Count(result => result.Outcome == Outcome.Failed);
            int passed = made.Count(result => result.Outcome == Outcome.Passed);
            int skipped = made.Length - passed - failed;
            Log.Write(failed == 0 ? LogLevel.Success : LogLevel.Error, $"[Checks] {passed} passed, {failed} failed, {skipped} skipped, report in {folder}.");
        }
        catch (Exception e)
        {
            Log.Error($"[Checks] The report couldn't be written to {folder}. {e.Message}");
        }
    }

    private enum Outcome { Passed, Failed, Skipped }

    private sealed record DeviceCheck(string Name, Outcome Outcome, string Detail);
}
