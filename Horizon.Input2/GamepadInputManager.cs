using System;
using System.Collections.Generic;
using Horizon.Core;
using Horizon.Engine;
using Silk.NET.Input;

namespace Horizon.Input2;

/// <summary>
/// Manages Gamepad inputs using Silk.NET's IGamepad.
/// Supports multiple gamepads, connection state tracking, as well as event-driven callbacks.
/// </summary>
public class GamepadInputManager : GameObject
{
    private IInputContext _inputContext;
    
    // Direct access to managed gamepads
    public IReadOnlyList<IGamepad> Gamepads => _gamepads;
    private readonly List<IGamepad> _gamepads = new();

    // Event driven callbacks
    public event Action<IGamepad> OnGamepadConnected;
    public event Action<IGamepad> OnGamepadDisconnected;

    public event Action<IGamepad, Button> OnButtonDown;
    public event Action<IGamepad, Button> OnButtonUp;
    public event Action<IGamepad, Thumbstick> OnThumbstickMoved;
    public event Action<IGamepad, Trigger> OnTriggerMoved;

    public GamepadInputManager()
    {
        Name = "Gamepad Input Manager";
    }

    public override void Initialize()
    {
        base.Initialize();
        
        var windowManager = Parent?.GetComponent<WindowManager>() ?? GameEngine.Instance?.WindowManager;
        if (windowManager == null)
            throw new Exception("GamepadInputManager requires a WindowManager to function.");

        _inputContext = windowManager.Input;
        
        if (_inputContext == null)
            throw new Exception("InputContext is not initialized on WindowManager.");

        // Subscribe to connection events
        _inputContext.ConnectionChanged += OnConnectionChanged;

        // Add already connected gamepads
        foreach (var gamepad in _inputContext.Gamepads)
        {
            if (gamepad.IsConnected)
            {
                AddGamepad(gamepad);
            }
        }
    }

    private void OnConnectionChanged(IInputDevice device, bool isConnected)
    {
        if (device is IGamepad gamepad)
        {
            if (isConnected)
            {
                AddGamepad(gamepad);
            }
            else
            {
                RemoveGamepad(gamepad);
            }
        }
    }

    private void AddGamepad(IGamepad gamepad)
    {
        if (!_gamepads.Contains(gamepad))
        {
            _gamepads.Add(gamepad);
            
            gamepad.ButtonDown += Gamepad_ButtonDown;
            gamepad.ButtonUp += Gamepad_ButtonUp;
            gamepad.ThumbstickMoved += Gamepad_ThumbstickMoved;
            gamepad.TriggerMoved += Gamepad_TriggerMoved;

            OnGamepadConnected?.Invoke(gamepad);
        }
    }

    private void RemoveGamepad(IGamepad gamepad)
    {
        if (_gamepads.Contains(gamepad))
        {
            _gamepads.Remove(gamepad);
            
            gamepad.ButtonDown -= Gamepad_ButtonDown;
            gamepad.ButtonUp -= Gamepad_ButtonUp;
            gamepad.ThumbstickMoved -= Gamepad_ThumbstickMoved;
            gamepad.TriggerMoved -= Gamepad_TriggerMoved;

            OnGamepadDisconnected?.Invoke(gamepad);
        }
    }

    private void Gamepad_ButtonDown(IGamepad gamepad, Button button) => OnButtonDown?.Invoke(gamepad, button);
    private void Gamepad_ButtonUp(IGamepad gamepad, Button button) => OnButtonUp?.Invoke(gamepad, button);
    private void Gamepad_ThumbstickMoved(IGamepad gamepad, Thumbstick thumbstick) => OnThumbstickMoved?.Invoke(gamepad, thumbstick);
    private void Gamepad_TriggerMoved(IGamepad gamepad, Trigger trigger) => OnTriggerMoved?.Invoke(gamepad, trigger);

    protected override void DisposeOther()
    {
        if (_inputContext != null)
        {
            _inputContext.ConnectionChanged -= OnConnectionChanged;
        }
        
        foreach (var gamepad in _gamepads)
        {
            gamepad.ButtonDown -= Gamepad_ButtonDown;
            gamepad.ButtonUp -= Gamepad_ButtonUp;
            gamepad.ThumbstickMoved -= Gamepad_ThumbstickMoved;
            gamepad.TriggerMoved -= Gamepad_TriggerMoved;
        }
        _gamepads.Clear();
        
        base.DisposeOther();
    }
}
