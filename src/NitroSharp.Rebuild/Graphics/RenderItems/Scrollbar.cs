using System;
using System.Numerics;
using NitroSharp.NsScript;

namespace NitroSharp.Graphics;

internal sealed partial class Scrollbar : Sprite
{
    private readonly Vector2 _start;
    private readonly Vector2 _end;
    private readonly bool _vertical;
    private float _grabOffset;

    public Scrollbar(EntityName name, Entity? parent, int priority, in SpriteTexture knob,
        Vector2 start, Vector2 end, NsScrollDirection direction, float initialValue)
        : base(name, parent, priority, knob)
    {
        _start = start;
        _vertical = direction == NsScrollDirection.Vertical;
        _end = _vertical ? new Vector2(start.X, end.Y) : new Vector2(end.X, start.Y);
        SetValue(initialValue);
    }

    public bool IsDragging { get; private set; }
    public float ScrollSpeed { get; set; } = 1;
    public Entity? ScrollTarget { get; set; }
    public bool IsAttached => Parent is { } parent && ReferenceEquals(parent.TryGetChild(Name), this);

    public float Value
    {
        get
        {
            float distance = Axis(_end) - Axis(_start);
            return distance == 0 ? 0 : Math.Clamp((Axis(Position) - Axis(_start)) / distance, 0, 1);
        }
    }

    private Vector2 Position => new(Transform.Position.X, Transform.Position.Y);
    private float Axis(Vector2 point) => _vertical ? point.Y : point.X;

    public void SetValue(float value)
    {
        if (!float.IsFinite(value)) value = 0;
        Vector2 position = Vector2.Lerp(_start, _end, Math.Clamp(value, 0, 1));
        Transform.Position = new Vector3(position, Transform.Position.Z);
    }

    public bool ContainsTrack(Vector2 pointer, Vector2 knobSize)
    {
        // Erase only hides the knob; the entire authored track remains a hit area.
        Vector2 min = Vector2.Min(_start, _end);
        Vector2 max = Vector2.Max(_start, _end) + knobSize;
        return pointer.X >= min.X && pointer.Y >= min.Y && pointer.X < max.X && pointer.Y < max.Y;
    }

    public void BeginDrag(Vector2 pointer, Vector2 knobSize)
    {
        Vector2 pos = Position;
        bool onKnob = pointer.X >= pos.X && pointer.Y >= pos.Y
            && pointer.X < pos.X + knobSize.X && pointer.Y < pos.Y + knobSize.Y;
        _grabOffset = onKnob ? Axis(pointer) - Axis(pos) : Axis(knobSize) / 2;
        IsDragging = true;
        Drag(pointer);
    }

    public void Drag(Vector2 pointer)
    {
        if (!IsDragging) return;
        SetAxis(Axis(pointer) - _grabOffset);
    }

    public void EndDrag() => IsDragging = false;

    public void Step(int direction) => SetAxis(Axis(Position) + direction * Math.Max(0, ScrollSpeed));

    private void SetAxis(float coordinate)
    {
        float distance = Axis(_end) - Axis(_start);
        SetValue(distance == 0 ? 0 : (coordinate - Axis(_start)) / distance);
    }

    internal static Vector2 ToDesignPosition(RenderContext render, Vector2 screenPosition)
    {
        Matrix4x4 matrix = render.GetTransformMatrix(NitroSharp.Graphics.Transform.Default, default,
            useScaling: true, aligned: false);
        if (!Matrix4x4.Invert(matrix, out Matrix4x4 inverse)) return Vector2.Zero;
        Vector3 point = Vector3.Transform(new Vector3(screenPosition, 0), inverse);
        return new Vector2(point.X, point.Y);
    }

    public override void Update(GameContext ctx)
    {
        base.Update(ctx);
        if (!IsDragging) return;
        Drag(ToDesignPosition(ctx.RenderContext, ctx.InputContext.MousePosition));
        if (!ctx.InputContext.PointerHeld) EndDrag();
    }

    public override bool Request(NsEntityAction action)
        => ScrollTarget is BacklogView backlog && backlog.Scroll(action) || base.Request(action);

    public override void Dispose()
    {
        EndDrag();
        base.Dispose();
    }
}
