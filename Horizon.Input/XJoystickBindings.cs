namespace Horizon.Input;

/// <summary>
/// The XJoystickBindings struct represents a serializable and customizable way to hotswap multiple binding profiles for joysticks.
/// </summary>
public struct XJoystickBindings
{
    /// <summary>
    /// Gets or sets the dictionary of XJoystickButton and VirtualAction pairs representing button-to-action mappings.
    /// </summary>
    public Dictionary<XJoystickButton, VirtualAction> ButtonActionPairs { get; set; }

    /// <summary>
    /// Gets the default XJoystickBindings with some pre-defined button-to-action mappings.
    /// </summary>
    public static XJoystickBindings Default { get; } =
        new XJoystickBindings
        {
            ButtonActionPairs = new Dictionary<XJoystickButton, VirtualAction>
            {
                { XJoystickButton.Y, VirtualAction.Back },
                { XJoystickButton.X, VirtualAction.Interact },
                { XJoystickButton.Start, VirtualAction.Pause }
            }
        };
}
/// <summary>
/// The XJoystickBindings struct represents a serializable and customizable way to hotswap multiple binding profiles for joysticks.
/// </summary>
public struct DJoystickBindings
{
    /// <summary>
    /// Gets or sets the dictionary of XJoystickButton and VirtualAction pairs representing button-to-action mappings.
    /// </summary>
    public Dictionary<DJoystickButton, VirtualAction> ButtonActionPairs { get; set; }

    /// <summary>
    /// Gets the default XJoystickBindings with some pre-defined button-to-action mappings.
    /// </summary>
    public static DJoystickBindings Default { get; } =
        new DJoystickBindings
        {
            ButtonActionPairs = new Dictionary<DJoystickButton, VirtualAction>
            {
                    { DJoystickButton.Circle, VirtualAction.Back },
                    { DJoystickButton.X, VirtualAction.Interact },
                    { DJoystickButton.Menu, VirtualAction.Pause },
                    { DJoystickButton.Create, VirtualAction.None },
                    { DJoystickButton.Square, VirtualAction.None },
                    { DJoystickButton.R1, VirtualAction.None },
                    { DJoystickButton.R2, VirtualAction.None },
                    { DJoystickButton.RB, VirtualAction.None },
                    { DJoystickButton.L1, VirtualAction.None },
                    { DJoystickButton.L2, VirtualAction.None },
                    { DJoystickButton.LB, VirtualAction.None },
                    { DJoystickButton.DPadUp, VirtualAction.None },
                    { DJoystickButton.DPadDown, VirtualAction.None },
                    { DJoystickButton.DPadLeft, VirtualAction.None },
                    { DJoystickButton.DPadRight, VirtualAction.None },
                    { DJoystickButton.LeftStickClick, VirtualAction.None },
                    { DJoystickButton.RightStickClick, VirtualAction.None },
                    { DJoystickButton.Touchpad, VirtualAction.None },
                    { DJoystickButton.Sony, VirtualAction.None },
                    { DJoystickButton.Triangle, VirtualAction.None },
            }
        };
}