using System.Numerics;

using Horizon.Engine;
using Horizon.Logging;
using Horizon.Platform.Android;
using Horizon.UI.Components;

namespace Horizon.Testing.Android;

/// <summary>
/// The checks that mean something on a phone, run off the game's threads while a ball goes round so it's plain the
/// frames carry on. What every check said goes on the screen as it comes in, into the log, and once they're all done
/// into checks/report.txt and checks/report.json in the app's external files folder for adb pull. With
/// HORIZON_TESTING_QUIT=on the game quits after, the way --checks does on a desktop.
/// <para>
/// What's here is portable, content, storage, audio, the surface and the GPU as the device has them. The desktop
/// Horizon.Testing --checks (windows, the terminal, the UI self tests) isn't, and isn't pretended to be.
/// </para>
/// </summary>
public sealed class DeviceChecksScene : Scene2D, ISelfCheck
{
    private const string QUIT_VARIABLE = "HORIZON_TESTING_QUIT";

    private readonly DeviceChecks checks = new();
    private Label status = null!;
    private float spin;
    private Task? running;
    private bool reported;

    /// <summary>
    /// Whether HORIZON_TESTING_QUIT=on quits once the report is written. On when started by itself, off in a run of every
    /// check, where the host says when to stop.
    /// </summary>
    public bool QuitWhenDone { get; init; } = true;

    // What the host reads to judge it, a throw out of the checks themselves counts as finished with one more failure
    public bool Finished => checks.Finished || running is { IsFaulted: true };
    public int Count => checks.Tally.Passed + checks.Tally.Failed + (running is { IsFaulted: true } ? 1 : 0);
    public int Failed => checks.Tally.Failed + (running is { IsFaulted: true } ? 1 : 0);
    public int Skipped => checks.Tally.Skipped;

    public DeviceChecksScene()
        : base(lit: false, fancy: false)
    {
        status = UI.CreateModule().AddComponent(new Label { Position = new Vector2(0.0f, -40.0f), Anchor = Horizon.Rendering.Origin.Top, TextScale = 0.25f });
    }

    public override void Initialize()
    {
        base.Initialize();
        Shapes.Describe = shapes =>
        {
            Vector2 at = new(MathF.Cos(spin) * 160.0f, MathF.Sin(spin) * 160.0f);
            shapes.FillCircle(at, 24.0f, new Vector3(0.4f, 0.8f, 1.0f));
            shapes.FillRectangle(new Vector2(-200.0f, -230.0f), new Vector2(200.0f, -214.0f), new Vector3(0.5f, 0.5f, 0.55f));
        };
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        spin += dt * 1.5f;

        // A second in, so the first frames have been drawn and timed and the audio has had its callbacks
        if (running is null && Engine.Runtime > 1.0)
            running = Task.Run(() => checks.Run(Engine, AndroidSession.Current));

        status.Text = checks.Summary;

        if (running is { IsCompleted: true } && !reported)
        {
            reported = true;
            if (running.Exception is { } failed) Log.Error($"[Checks] The checks themselves threw. {failed.GetBaseException()}");
            if (QuitWhenDone && string.Equals(Environment.GetEnvironmentVariable(QUIT_VARIABLE), "on", StringComparison.OrdinalIgnoreCase)) Engine.Exit();
        }
    }
}
