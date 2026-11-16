using System.Diagnostics;
using System.Numerics;

using CoreAnimation;

using Foundation;

using GameController;

using Horizon.Input;

using ObjCRuntime;

using InputEvent = Horizon.Input.InputEvent;

namespace Horizon.Platform.iOS;

/// <summary>
/// Controllers on an iPhone, an Xbox or a DualSense pad over Bluetooth or anything else GameController calls an
/// extended gamepad, read with GCController. Its profile already has the standard layout, so the buttons, the sticks
/// (up positive, the same as the engine) and the triggers go straight across.
/// <para>
/// Read by polling on the screen's refresh (a CADisplayLink on the main thread), never through GameController's change
/// handlers. Those are blocks, and handing a managed delegate over as a block is what NativeAOT doesn't do here. A pad
/// that appears in GCController.Controllers is connected, one that's gone from it is disconnected with everything it
/// held let go of first, and a state is only published when something changed. Rumble isn't done yet.
/// </para>
/// </summary>
internal sealed class IosGamepads : IPlatformGamepads
{
    private readonly IosHost host;
    private readonly Dictionary<nint, Pad> pads = [];
    private Ticker? ticker;
    private bool started;
    private volatile bool disposed;

    public event Action<InputEvent>? EventReceived;

    public IosGamepads(IosHost host) => this.host = host;

    /// <summary>How many pads are connected, for a check.</summary>
    public int Count => pads.Count;

    public void Start() => host.Post(StartHere);

    private void StartHere()
    {
        if (started || disposed) return;
        started = true;

        // Pads already paired in Settings show up on their own, this only lets one that's waiting to pair be found too
        GCController.StartWirelessControllerDiscovery(null!);
        ticker = new Ticker(Poll);
        Poll();
    }

    // Main thread, once a screen refresh
    private void Poll()
    {
        if (disposed) return;
        long stamp = Stopwatch.GetTimestamp();

        GCController[] now = GCController.Controllers;
        foreach (GCController controller in now)
        {
            if (controller.ExtendedGamepad is not { } gamepad) continue;
            if (!pads.TryGetValue(controller.Handle, out Pad? pad))
            {
                string name = controller.VendorName ?? controller.ProductCategory ?? "Controller";
                pad = new Pad(controller, new GamepadDevice(name));
                pads.Add(controller.Handle, pad);
                Horizon.Logging.Log.Info($"[iOS] Gamepad '{name}' connected ({controller.ProductCategory}).");
                Publish(InputEventKind.GamepadConnected, pad, stamp);
            }

            GamepadSnapshot state = Read(gamepad);
            if (Same(state, pad.State)) continue;
            pad.State = state;
            Publish(InputEventKind.GamepadState, pad, stamp);
        }

        foreach ((nint handle, Pad pad) in pads.ToArray())
        {
            if (Array.Exists(now, controller => controller.Handle == handle && controller.ExtendedGamepad is not null)) continue;
            pads.Remove(handle);
            Horizon.Logging.Log.Info($"[iOS] Gamepad '{pad.Device.Name}' disconnected.");
            pad.State = default;
            Publish(InputEventKind.GamepadState, pad, stamp);
            Publish(InputEventKind.GamepadDisconnected, pad, stamp);
        }
    }

    private static GamepadSnapshot Read(GCExtendedGamepad gamepad)
    {
        uint buttons = 0;
        void Button(GCControllerButtonInput? input, GamepadInput button)
        {
            if (input?.IsPressed == true) buttons |= GamepadInputs.Bit(button);
        }

        Button(gamepad.ButtonA, GamepadInput.A);
        Button(gamepad.ButtonB, GamepadInput.B);
        Button(gamepad.ButtonX, GamepadInput.X);
        Button(gamepad.ButtonY, GamepadInput.Y);
        Button(gamepad.LeftShoulder, GamepadInput.LeftBumper);
        Button(gamepad.RightShoulder, GamepadInput.RightBumper);
        Button(gamepad.ButtonOptions, GamepadInput.Back);
        Button(gamepad.ButtonMenu, GamepadInput.Start);
        Button(gamepad.ButtonHome, GamepadInput.Home);
        Button(gamepad.LeftThumbstickButton, GamepadInput.LeftStick);
        Button(gamepad.RightThumbstickButton, GamepadInput.RightStick);
        Button(gamepad.DPad.Up, GamepadInput.DPadUp);
        Button(gamepad.DPad.Right, GamepadInput.DPadRight);
        Button(gamepad.DPad.Down, GamepadInput.DPadDown);
        Button(gamepad.DPad.Left, GamepadInput.DPadLeft);

        GamepadSnapshot state = default;
        state.Buttons = buttons;
        state.LeftStick = new Vector2(gamepad.LeftThumbstick.XAxis.Value, gamepad.LeftThumbstick.YAxis.Value);
        state.RightStick = new Vector2(gamepad.RightThumbstick.XAxis.Value, gamepad.RightThumbstick.YAxis.Value);
        state.LeftTrigger = gamepad.LeftTrigger.Value;
        state.RightTrigger = gamepad.RightTrigger.Value;
        return state;
    }

    private static bool Same(GamepadSnapshot a, GamepadSnapshot b) =>
        a.Buttons == b.Buttons && a.LeftStick == b.LeftStick && a.RightStick == b.RightStick &&
        a.LeftTrigger == b.LeftTrigger && a.RightTrigger == b.RightTrigger;

    /// <summary>Every pad let go of, for the app losing focus while buttons were held. Main thread.</summary>
    public void ReleaseAll()
    {
        long stamp = Stopwatch.GetTimestamp();
        foreach (Pad pad in pads.Values)
        {
            pad.State = default;
            Publish(InputEventKind.GamepadState, pad, stamp);
        }
    }

    private void Publish(InputEventKind kind, Pad pad, long stamp)
    {
        EventReceived?.Invoke(new InputEvent { Kind = kind, Gamepad = pad.Device, GamepadState = pad.State, Timestamp = stamp });
        host.HeardInput();
    }

    /// <summary>Stops polling. From any thread, the display link goes on the main thread.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        host.Post(() =>
        {
            ticker?.Stop();
            ticker = null;
            pads.Clear();
            GCController.StopWirelessControllerDiscovery();
        });
    }

    private sealed class Pad(GCController native, GamepadDevice device)
    {
        public GCController Native { get; } = native;
        public GamepadDevice Device { get; } = device;
        public GamepadSnapshot State;
    }

    // A display link calling back through a selector, an exported method rather than a block, so NativeAOT's static
    // registrar has it
    private sealed class Ticker : NSObject
    {
        private readonly Action tick;
        private readonly CADisplayLink link;

        public Ticker(Action tick)
        {
            this.tick = tick;
            link = CADisplayLink.Create(this, new Selector("tick"));

            // As often as the screen goes, 120 on a ProMotion phone (with CADisableMinimumFrameDurationOnPhone in the
            // app's Info.plist), so a press is never a whole 60 Hz frame late
            link.PreferredFrameRateRange = new CAFrameRateRange { Minimum = 60, Maximum = 120, Preferred = 120 };
            link.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
        }

        [Export("tick")]
        public void Tick() => tick();

        public void Stop() => link.Invalidate();
    }
}
