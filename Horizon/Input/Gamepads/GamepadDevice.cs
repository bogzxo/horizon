namespace Horizon.Input;

/// <summary>One connection to a physical gamepad. Hosts reuse this object until that connection ends.</summary>
public sealed class GamepadDevice
{
    private readonly Action<float, float>? vibrate;

    public string Name { get; }

    /// <param name="vibrate">Runs on the caller's thread. A platform host must dispatch to its device thread if needed.</param>
    public GamepadDevice(string name, Action<float, float>? vibrate = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        this.vibrate = vibrate;
    }

    internal void Vibrate(float left, float right) =>
        vibrate?.Invoke(Math.Clamp(left, 0.0f, 1.0f), Math.Clamp(right, 0.0f, 1.0f));
}
