using System;
using System.Collections.Generic;
using System.Numerics;

namespace NitroSharp.Input;

public abstract class InputSnapshot
{
    private readonly HashSet<Key> _keyboardState = [];
    private readonly List<Key> _newKeys = [];

    private readonly bool[] _mouseState = new bool[13];
    private readonly bool[] _newMouseButtons = new bool[13];

    private readonly Dictionary<ulong, TouchPoint> _activeTouches = [];

    public Vector2 MousePosition { get; protected set; }
    public Vector2 WheelDelta { get; protected set; }
    public IReadOnlyCollection<TouchPoint> ActiveTouches => _activeTouches.Values;
    public bool IsTouchDown { get; private set; }

    public abstract ReadOnlySpan<KeyEvent> KeyEvents { get; }
    public abstract ReadOnlySpan<MouseButtonEvent> MouseEvents { get; }
    public abstract ReadOnlySpan<GamepadEvent> GamepadEvents { get; }
    public abstract ReadOnlySpan<TouchEvent> TouchEvents { get; }

    public bool KeyState(Key key)
        => _keyboardState.Contains(key);

    public bool IsKeyDown(Key key)
        => _newKeys.Contains(key);

    public bool MouseState(MouseButton button)
        => _mouseState[(int)button];

    public bool IsMouseDown(MouseButton button)
        => _newMouseButtons[(int)button];

    public void Refresh()
    {
        _newKeys.Clear();
        _newMouseButtons.AsSpan().Clear();
        IsTouchDown = false;

        foreach (KeyEvent evt in KeyEvents)
        {
            if (evt.Down)
            {
                if (_keyboardState.Add(evt.Physical))
                {
                    _newKeys.Add(evt.Physical);
                }
            }
            else
            {
                _keyboardState.Remove(evt.Physical);
            }
        }

        foreach (MouseButtonEvent evt in MouseEvents)
        {
            int index = (int)evt.MouseButton;
            // Preserve a press even when its release arrives in the same pump.
            _newMouseButtons[index] |= !_mouseState[index] && evt.Down;
            _mouseState[index] = evt.Down;
        }

        foreach (TouchEvent evt in TouchEvents)
        {
            switch (evt.Kind)
            {
                case TouchEventKind.Down:
                    IsTouchDown = true;
                    goto case TouchEventKind.Motion;
                case TouchEventKind.Motion:
                    _activeTouches[evt.FingerId] = new TouchPoint(evt.Position, evt.Pressure);
                    MousePosition = evt.Position;
                    break;
                case TouchEventKind.Up:
                    _activeTouches.Remove(evt.FingerId);
                    break;
            }
        }
    }
}
