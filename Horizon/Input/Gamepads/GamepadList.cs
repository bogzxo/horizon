using System.Collections;

namespace Horizon.Input;

/// <summary>
/// The gamepads of a <see cref="GamepadInputManager"/> by slot, to look at and not to change.
/// Menus walk these on every update, so walking them with a foreach makes no garbage (which a plain read only list would).
/// </summary>
public readonly struct GamepadList : IReadOnlyList<Gamepad>
{
    private readonly List<Gamepad> _gamepads;

    internal GamepadList(List<Gamepad> gamepads)
    {
        _gamepads = gamepads;
    }

    public Gamepad this[int slot] => _gamepads[slot];

    public int Count => _gamepads.Count;

    public List<Gamepad>.Enumerator GetEnumerator() => _gamepads.GetEnumerator();

    IEnumerator<Gamepad> IEnumerable<Gamepad>.GetEnumerator() => _gamepads.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _gamepads.GetEnumerator();
}
