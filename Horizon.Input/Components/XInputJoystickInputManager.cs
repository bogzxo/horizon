using System.Numerics;

using Silk.NET.GLFW;
using Silk.NET.Input;

namespace Horizon.Input.Components
{
    /// <summary>
    /// The XInputJoystickInputManager class is responsible for handling input from a joystick/gamepad.
    /// </summary>
    public class XInputJoystickInputManager : PeripheralInputManager
    {
        /// <summary>
        /// Gets the first connected joystick/gamepad, or null if none is connected.
        /// </summary>
        public static IGamepad? Gamepad =>
            Manager.NativeInputContext.Gamepads.Count > 0 ? GetController() : null;

        private static IGamepad? GetController()
        {
            // FIXME yea....
            return (
                from stick in Manager.NativeInputContext.Gamepads
                where stick.IsConnected
                select stick
            ).FirstOrDefault();
        }

        /// <summary>
        /// Gets the XJoystickBindings representing the button-to-action mappings for the joystick.
        /// </summary>
        public XJoystickBindings Bindings { get; private set; }

        /// <summary>
        /// Gets a value indicating whether a joystick/gamepad is connected.
        /// </summary>
        public bool IsConnected => Gamepad?.IsConnected ?? false;

        private VirtualAction actions;

        private Vector2 primaryAxis,
            secondaryAxis,
            triggers;

        /// <summary>
        /// Initializes the JoystickInputManager by setting the default XJoystickBindings.
        /// </summary>
        public XInputJoystickInputManager()
        {
            Bindings = XJoystickBindings.Default;
        }

        public override void Initialize()
        { }

        public override void SwapBuffers()
        { }

        /// <summary>
        /// Retrieves the current JoystickData containing input information from the joystick/gamepad.
        /// </summary>
        /// <returns>The JoystickData containing the joystick input.</returns>
        public JoystickData GetData()
        {
            return new JoystickData
            {
                Actions = actions,
                PrimaryAxis = primaryAxis,
                SecondaryAxis = secondaryAxis,
                Triggers = triggers
            };
        }

        public XJoystickButton[] JoystickKeys;

        /// <summary>
        /// Updates the JoystickInputManager, processing input from the connected joystick/gamepad.
        /// </summary>
        /// <param name="dt">The time elapsed since the last update.</param>
        public override void AggregateData(float dt)
        {
            List<XJoystickButton> buttonPresses = [];

            foreach (var button in Gamepad.Buttons)
            {
                if (button.Pressed)
                    buttonPresses.Add((XJoystickButton)button.Index);
            }
            JoystickKeys = [.. buttonPresses];

            actions = VirtualAction.None;

            if (Gamepad?.IsConnected != true)
                return;

            
            foreach ((XJoystickButton key, VirtualAction action) in Bindings.ButtonActionPairs)
            {
                if (Gamepad.Buttons[(int)key].Pressed)
                {
                    actions |= action;
                }
                else
                {
                    actions ^= action;
                }
            }
            
            primaryAxis = new Vector2(Gamepad.Thumbsticks[0].X, Gamepad.Thumbsticks[0].Y);
            secondaryAxis = new Vector2(Gamepad.Thumbsticks[1].X, Gamepad.Thumbsticks[1].X);
            triggers = new Vector2(Gamepad.Triggers[0].Position, Gamepad.Triggers[1].Position);
        }
    }
}