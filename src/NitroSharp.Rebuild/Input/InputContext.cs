using System;
using System.Collections.Generic;
using System.Numerics;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;

namespace NitroSharp.Input;

internal enum InputAction
{
    Enter = 0,
    Advance,
    Back,
    Left,
    Up,
    Right,
    Down,
    Skip,
    TriggerLeft,
    TriggerRight
}

internal sealed class InputContext(GameWindow window) : IDisposable
{
    private static readonly InputAction[] s_allActions = Enum.GetValues<InputAction>();

    private readonly InputMap _inputMap = CreateDefaultMap();
    private readonly float[] _actionValues = new float[s_allActions.Length];
    private readonly bool[] _actionDown = new bool[s_allActions.Length];
    private readonly bool[] _actionTriggered = new bool[s_allActions.Length];
    private readonly HashSet<GamepadButton> _gamepadPresses = [];
    private Vector2 _previousMousePosition;
    private bool _hasMousePosition;
    private bool _choicePressConsumed;

    private InputSnapshot Snapshot { get; set; } = null!;
    private Gamepad Gamepad { get; set; } = Gamepad.Default(window is Sdl3Window);

    private float GetActionValue(InputAction action) => _actionValues[(int)action];
    private bool IsActionDown(InputAction action) => _actionDown[(int)action];
    internal bool IsActionHeld(InputAction action) => IsActionDown(action);
    private bool IsActionTriggered(InputAction action) => _actionTriggered[(int)action];

    internal Vector2 MousePosition => Snapshot.MousePosition;
    internal bool IsMouseButtonDown(MouseButton button) => Snapshot.IsMouseDown(button);
    internal bool WasActionTriggered(InputAction action) => _actionTriggered[(int)action];
    internal bool PointerMoved { get; private set; }
    internal bool PointerHeld => Snapshot.MouseState(MouseButton.Left) || Snapshot.ActiveTouches.Count > 0;
    internal bool ChoicePointerPressed => !_choicePressConsumed
        && (Snapshot.IsMouseDown(MouseButton.Left) || Snapshot.IsTouchDown);

    internal bool ConsumeChoicePress(bool pointer)
    {
        if (_choicePressConsumed || !(pointer ? ChoicePointerPressed : WasActionTriggered(InputAction.Enter)))
            return false;
        _choicePressConsumed = true;
        Consume(InputAction.Enter);
        Consume(InputAction.Advance);
        return true;
    }

    public bool Consume(InputAction action)
    {
        int index = (int)action;
        bool result = _actionTriggered[index];
        _actionTriggered[index] = false;
        return result;
    }

    internal bool ConsumeFrameSequenceInterrupt()
    {
        bool advance = Consume(InputAction.Advance);
        bool rightClick = Snapshot.IsMouseDown(MouseButton.Right);
        if (rightClick) Consume(InputAction.Back);
        return advance || rightClick || Snapshot.KeyState(Key.LeftShift) || Snapshot.KeyState(Key.RightShift);
    }

    public void Update(GameWindow window, SystemVariableLookup systemVariables)
    {
        Snapshot = window.PumpEvents();
        PointerMoved = !_hasMousePosition || Snapshot.MousePosition != _previousMousePosition;
        _previousMousePosition = Snapshot.MousePosition;
        _hasMousePosition = true;
        _choicePressConsumed = false;
        ProcessGamepadEvents();

        foreach (InputAction action in s_allActions)
        {
            PollAction(action);
        }

        SystemVariableLookup sys = systemVariables;
        set(out sys.RightButtonDown, IsActionDown(InputAction.Back));
        set(out sys.X360StartButtonDown, IsActionDown(InputAction.Advance));
        set(out sys.X360AButtonDown, IsActionDown(InputAction.Advance));
        set(out sys.X360BButtonDown, IsActionDown(InputAction.Back));
        set(out sys.X360YButtonDown, Gamepad.ButtonState(GamepadButton.NorthY));
        set(out sys.X360LeftButtonDown, IsActionDown(InputAction.Left));
        set(out sys.X360UpButtonDown, IsActionDown(InputAction.Up));
        set(out sys.X360RightButtonDown, IsActionDown(InputAction.Right));
        set(out sys.X360DownButtonDown, IsActionDown(InputAction.Down));
        set(out sys.X360LbButtonDown, Gamepad.ButtonState(GamepadButton.LeftShoulder));
        set(out sys.X360RbButtonDown, Gamepad.ButtonState(GamepadButton.RightShoulder));
        return;

        static void set(out ConstantValue target, bool value)
        {
            target = ConstantValue.Boolean(value);
        }
    }

    private void PollAction(InputAction action)
    {
        int index = (int)action;
        float value = _inputMap.GetValue(action, Snapshot, Gamepad);
        bool isDown = value > 0.5f;
        _actionTriggered[index] = (!_actionDown[index] && isDown) || HasPressPulse(action);
        _actionDown[index] = isDown;
        _actionValues[index] = value;
    }

    private bool HasPressPulse(InputAction action)
    {
        bool confirm = Snapshot.IsKeyDown(Key.Return) || Snapshot.IsKeyDown(Key.KeypadEnter)
            || _gamepadPresses.Contains(GamepadButton.SouthA) || Gamepad.IsButtonDown(GamepadButton.SouthA);
        return action switch
        {
            InputAction.Enter => confirm,
            InputAction.Advance => confirm || Snapshot.IsKeyDown(Key.Space)
                || Snapshot.IsMouseDown(MouseButton.Left) || Snapshot.IsTouchDown,
            InputAction.Back => Snapshot.IsMouseDown(MouseButton.Right)
                || Snapshot.IsKeyDown(Key.Backspace) || Snapshot.IsKeyDown(Key.Escape)
                || _gamepadPresses.Contains(GamepadButton.EastB),
            InputAction.Left => Snapshot.IsKeyDown(Key.Left) || _gamepadPresses.Contains(GamepadButton.DPadLeft),
            InputAction.Up => Snapshot.IsKeyDown(Key.Up) || _gamepadPresses.Contains(GamepadButton.DPadUp)
                || Snapshot.WheelDelta.Y > 0,
            InputAction.Right => Snapshot.IsKeyDown(Key.Right) || _gamepadPresses.Contains(GamepadButton.DPadRight),
            InputAction.Down => Snapshot.IsKeyDown(Key.Down) || _gamepadPresses.Contains(GamepadButton.DPadDown)
                || Snapshot.WheelDelta.Y < 0,
            _ => false
        };
    }

    private void ProcessGamepadEvents()
    {
        Gamepad.Refresh();
        _gamepadPresses.Clear();
        foreach (GamepadEvent ev in Snapshot.GamepadEvents)
        {
            switch (ev.Kind)
            {
                case GamepadEventKind.Added:
                {
                    if (Gamepad is NullGamepad)
                    {
                        Gamepad = new Sdl3Gamepad(ev.GamepadId);
                    }
                    break;
                }
                case GamepadEventKind.Removed:
                {
                    if (Gamepad is Sdl3Gamepad gamepad && gamepad.InstanceId == ev.GamepadId)
                    {
                        Gamepad.Dispose();
                        Gamepad = new NullGamepad();
                    }
                    break;
                }
                case GamepadEventKind.ButtonDown:
                case GamepadEventKind.ButtonUp:
                case GamepadEventKind.AxisMotion:
                {
                    if (ev.Kind == GamepadEventKind.ButtonDown && !Gamepad.ButtonState(ev.Button))
                        _gamepadPresses.Add(ev.Button);
                    Gamepad.HandleEvent(ev);
                    break;
                }
            }
        }
    }

    private static InputMap CreateDefaultMap()
    {
        var map = new InputMapBuilder();
        map.Bind(InputAction.Advance, new Binding { MouseButton = MouseButton.Left });
        map.Bind(InputAction.Advance, new Binding { Key = Key.Return });
        map.Bind(InputAction.Advance, new Binding { Key = Key.KeypadEnter });
        map.Bind(InputAction.Advance, new Binding { Key = Key.Space });
        map.Bind(InputAction.Advance, new Binding { GamepadButton = GamepadButton.SouthA });
        map.Bind(InputAction.Advance, new Binding { Touch = true });

        map.Bind(InputAction.Back, new Binding { MouseButton = MouseButton.Right });
        map.Bind(InputAction.Back, new Binding { Key = Key.Backspace });
        map.Bind(InputAction.Back, new Binding { Key = Key.Escape });
        map.Bind(InputAction.Back, new Binding { GamepadButton = GamepadButton.EastB });

        map.Bind(InputAction.Enter, new Binding { Key = Key.Return });
        map.Bind(InputAction.Enter, new Binding { Key = Key.KeypadEnter });
        map.Bind(InputAction.Enter, new Binding { GamepadButton = GamepadButton.SouthA });

        map.Bind(InputAction.Left, new Binding { Key = Key.Left });
        map.Bind(InputAction.Left, new Binding { GamepadButton = GamepadButton.DPadLeft });

        map.Bind(InputAction.Up, new Binding { Key = Key.Up });
        map.Bind(InputAction.Up, new Binding { WheelUp = true });
        map.Bind(InputAction.Up, new Binding { GamepadButton = GamepadButton.DPadUp });

        map.Bind(InputAction.Right, new Binding { Key = Key.Right });
        map.Bind(InputAction.Right, new Binding { GamepadButton = GamepadButton.DPadRight });

        map.Bind(InputAction.Down, new Binding { Key = Key.Down });
        map.Bind(InputAction.Down, new Binding { WheelDown = true });
        map.Bind(InputAction.Down, new Binding { GamepadButton = GamepadButton.DPadDown });

        map.Bind(InputAction.Skip, new Binding { Key = Key.LeftControl });
        map.Bind(InputAction.Skip, new Binding { Key = Key.RightControl });

        map.Bind(InputAction.TriggerLeft, new Binding { Key = Key.Left });
        map.Bind(InputAction.TriggerLeft, new Binding { GamepadAxis = GamepadAxis.LeftTrigger });

        map.Bind(InputAction.TriggerRight, new Binding { Key = Key.Right });
        map.Bind(InputAction.TriggerRight, new Binding { GamepadAxis = GamepadAxis.RightTrigger });

        return map.Build();
    }

    public void Dispose()
    {
        Gamepad.Dispose();
    }
}
