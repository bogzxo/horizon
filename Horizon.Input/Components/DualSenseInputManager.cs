using DualSenseAPI;
using System.Numerics;

namespace Horizon.Input.Components
{
    /// <summary>
    /// The DualSenseInputManager class is responsible for handling input from a joystick/gamepad.
    /// </summary>
    public class DualSenseInputManager : PeripheralInputManager, IDisposable
    {
        /// <summary>
        /// Gets the first connected joystick/gamepad, or null if none is connected.
        /// </summary>
        public DualSense? Controller { get; private set; }

        public DJoystickBindings Bindings { get; private set; }

        public bool IsConnected { get; private set; } = false;
        public DualSenseAPI.State.DualSenseOutputState OutputState { get; set; } =
            new DualSenseAPI.State.DualSenseOutputState();


        private DualSense? AttachController()
        {

            var controller = DualSense.EnumerateControllers().FirstOrDefault();
            if (controller == null)
                return null;

            controller.Acquire();
            controller.JoystickDeadZone = 0.2f;
            controller.BeginPolling(12);
            controller.OnStatePolled += ControllerStatePolled;

            IsConnected = true;
            return controller;
        }

        private void ControllerStatePolled(DualSense sender)
        {
            lock (_updateLock)
            {
                var state = sender.InputState;

                if (state.CircleButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.Circle];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.Circle];

                if (state.SquareButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.Square];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.Square];

                if (state.TriangleButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.Triangle];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.Triangle];

                if (state.CrossButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.X];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.X];

                if (state.DPadUpButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.DPadUp];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.DPadUp];

                if (state.DPadDownButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.DPadDown];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.DPadDown];

                if (state.DPadLeftButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.DPadLeft];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.DPadLeft];

                if (state.DPadRightButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.DPadRight];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.DPadRight];

                if (state.L1Button) actions |= Bindings.ButtonActionPairs[DJoystickButton.L1];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.L1];

                if (state.L2Button) actions |= Bindings.ButtonActionPairs[DJoystickButton.L2];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.L2];

                if (state.R1Button) actions |= Bindings.ButtonActionPairs[DJoystickButton.R1];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.R1];

                if (state.R2Button) actions |= Bindings.ButtonActionPairs[DJoystickButton.R2];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.R2];

                if (state.MenuButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.Menu];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.Menu];

                if (state.LogoButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.Sony];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.Sony];

                if (state.CreateButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.Create];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.Create];

                if (state.L3Button) actions |= Bindings.ButtonActionPairs[DJoystickButton.LeftStickClick];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.LeftStickClick];

                if (state.R3Button) actions |= Bindings.ButtonActionPairs[DJoystickButton.RightStickClick];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.RightStickClick];

                if (state.TouchpadButton) actions |= Bindings.ButtonActionPairs[DJoystickButton.Touchpad];
                else actions ^= Bindings.ButtonActionPairs[DJoystickButton.Touchpad];


                primaryAxis = new Vector2(state.LeftAnalogStick.X, state.LeftAnalogStick.Y);
                secondaryAxis = new Vector2(state.RightAnalogStick.X, state.RightAnalogStick.Y);
                triggers = new Vector2(state.L2, state.R2);

                sender.OutputState = OutputState;
            }
        }

        private readonly object _updateLock = new();


        private VirtualAction actions;

        private Vector2 primaryAxis,
            secondaryAxis,
            triggers;

        /// <summary>
        /// Initializes the JoystickInputManager by setting the default XJoystickBindings.
        /// </summary>
        public override void Initialize()
        {
            Bindings = DJoystickBindings.Default;

            Controller = AttachController();
        }

        /// <summary>
        /// Retrieves the current JoystickData containing input information from the joystick/gamepad.
        /// </summary>
        /// <returns>The JoystickData containing the joystick input.</returns>
        public JoystickData GetData()
        {
            lock (_updateLock)
            {
                return new JoystickData
                {
                    Actions = actions,
                    PrimaryAxis = primaryAxis,
                    SecondaryAxis = secondaryAxis,
                    Triggers = triggers
                };
            }
        }

        public override void SwapBuffers()
        {
            
        }

        public override void AggregateData(float dt)
        {
            
        }

        public void Dispose()
        {
            OutputState.LeftRumble = OutputState.RightRumble = 0.0f;
            OutputState.L2Effect = OutputState.R2Effect = DualSenseAPI.TriggerEffect.Default;

            Controller?.ReadWriteOnce();
            Controller?.EndPolling();
            Controller?.Release();
        }
    }
}