using System;
using System.Collections.Generic;
using NitroSharp.Input;
using Xunit;

namespace NitroSharp.Tests;

public sealed class InputSnapshotTests
{
    [Fact]
    public void SamePumpPressAndReleaseStillProducesPressEdges()
    {
        var snapshot = new TestSnapshot();
        snapshot.Keys.Add(new KeyEvent(0, 0, true, false, Key.Return, (VKey)0, 0));
        snapshot.Keys.Add(new KeyEvent(0, 0, false, false, Key.Return, (VKey)0, 0));
        snapshot.MouseButtons.Add(new MouseButtonEvent(0, 0, MouseButton.Left, true, 1));
        snapshot.MouseButtons.Add(new MouseButtonEvent(0, 0, MouseButton.Left, false, 1));

        snapshot.Refresh();

        Assert.True(snapshot.IsKeyDown(Key.Return));
        Assert.False(snapshot.KeyState(Key.Return));
        Assert.True(snapshot.IsMouseDown(MouseButton.Left));
        Assert.False(snapshot.MouseState(MouseButton.Left));
    }

    private sealed class TestSnapshot : InputSnapshot
    {
        public readonly List<KeyEvent> Keys = [];
        public readonly List<MouseButtonEvent> MouseButtons = [];
        public override ReadOnlySpan<KeyEvent> KeyEvents => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Keys);
        public override ReadOnlySpan<MouseButtonEvent> MouseEvents => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(MouseButtons);
        public override ReadOnlySpan<GamepadEvent> GamepadEvents => [];
        public override ReadOnlySpan<TouchEvent> TouchEvents => [];
    }
}
