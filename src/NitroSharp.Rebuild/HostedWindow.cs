using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using NitroSharp.Input;
using Veldrid;

namespace NitroSharp;

// A host-owned surface: no OS window, SDL initialization, or global event pump.
internal sealed class HostedWindow(ScreenSizeU size) : InputSnapshot, GameWindow
{
    private readonly List<KeyEvent> _keys = [];
    private readonly List<MouseButtonEvent> _buttons = [];
    public SwapchainSource SwapchainSource => throw new NotSupportedException("Offscreen surface");
    public ScreenSizeU Size => size;
    public bool Exists { get; private set; } = true;
    public event Action? CloseRequested;
    public event Action? Resized;
    public override ReadOnlySpan<KeyEvent> KeyEvents => CollectionsMarshal.AsSpan(_keys);
    public override ReadOnlySpan<MouseButtonEvent> MouseEvents =>
        CollectionsMarshal.AsSpan(_buttons);
    public override ReadOnlySpan<GamepadEvent> GamepadEvents => [];
    public override ReadOnlySpan<TouchEvent> TouchEvents => [];

    public void Event(uint type, int code, float x, float y)
    {
        switch (type)
        {
            case 1:
            case 2:
                if (code < 0 || code >= (int)Key.NumKeys)
                    throw new ArgumentOutOfRangeException(nameof(code));
                _keys.Add(new KeyEvent(0, 0, type == 1, false, (Key)code, 0, 0));
                break;
            case 3:
                MousePosition = new Vector2(x, y);
                break;
            case 4:
            case 5:
                if (code is < 1 or > 3)
                    throw new ArgumentOutOfRangeException(nameof(code));
                MousePosition = new Vector2(x, y);
                _buttons.Add(
                    new MouseButtonEvent(0, 0, (MouseButton)(1 << (code - 1)), type == 4, 1)
                );
                break;
            case 6:
                WheelDelta += new Vector2(x, y);
                break;
            default:
                throw new NotSupportedException($"Input event {type} is unsupported.");
        }
    }

    public InputSnapshot PumpEvents()
    {
        Refresh();
        _keys.Clear();
        _buttons.Clear();
        return this;
    }

    public void EndFrame() => WheelDelta = Vector2.Zero;

    public void SetMousePosition(Vector2 pos) => MousePosition = pos;

    public void SetCursor(SystemCursor cursor) { }

    public void Dispose()
    {
        Exists = false;
    }
}
