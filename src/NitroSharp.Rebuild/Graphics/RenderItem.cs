using System;
using System.Numerics;
using NitroSharp.Common;
using NitroSharp.Content;
using NitroSharp.Graphics.Core;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Primitives;
using Veldrid;

namespace NitroSharp.Graphics;

internal readonly record struct RenderItemKey(int Priority, int Id) : IComparable<RenderItemKey>
{
    public int CompareTo(RenderItemKey other)
    {
        if (Priority > other.Priority) { return 1; }
        if (Priority < other.Priority) { return -1; }
        if (Id > other.Id) { return 1; }
        return -1;
    }
}

internal abstract partial class RenderItem : Entity, IComparable<RenderItem>
{
    private static int s_lastId;

    private RenderItemKey _key;
    private RgbaFloat _color = RgbaFloat.White;
    private Transform _transform = Transform.Default;
    private OpacityAnimation? _fadeAnimation;
    private MoveAnimation? _moveAnimation;
    private BezierMoveAnimation? _bezierMoveAnimation;
    private ScaleAnimation? _scaleAnimation;
    private RotationAnimation? _rotationAnimation;
    private MaskTransition? _transition;
    private bool _transitionActive;

    protected RenderItem(EntityName name, Entity? parent, int priority) : base(name, parent)
    {
        _key = new RenderItemKey(priority, s_lastId++);
        Priority = priority;
    }

    public int Priority { get; }

    public DesignRect BoundingRect { get; private set; }

    public ref RgbaFloat Color => ref _color;
    public ref Transform Transform => ref _transform;

    public BlendMode BlendMode { get; set; } = BlendMode.Alpha;
    public FilterMode FilterMode { get; set; } = FilterMode.Linear;
    public bool IsHidden { get; private set; }
    public bool HidesWithText { get; private set; }

    public override bool Request(NsEntityAction action)
    {
        switch (action)
        {
            case NsEntityAction.HideWithText:
                HidesWithText = true;
                return true;
            case NsEntityAction.Disable:
                IsHidden = true;
                return true;
            case NsEntityAction.Enable:
                IsHidden = false;
                return true;
            case NsEntityAction.SetAdditiveBlend:
                BlendMode = NitroSharp.Graphics.BlendMode.Additive;
                return true;
            case NsEntityAction.SetMultiplicativeBlend:
                BlendMode = NitroSharp.Graphics.BlendMode.Multiplicative;
                return true;
            default:
                return base.Request(action);
        }
    }

    public abstract DesignSize GetSize(RenderContext ctx);

    public override void Update(GameContext ctx)
    {
        bool finish = ctx.StorySkipping;
        AdvanceAnimation(ref _fadeAnimation, ctx.DeltaTime, finish);
        AdvanceAnimation(ref _moveAnimation, ctx.DeltaTime, finish);
        AdvanceAnimation(ref _bezierMoveAnimation, ctx.DeltaTime, finish);
        AdvanceAnimation(ref _scaleAnimation, ctx.DeltaTime, finish);
        AdvanceAnimation(ref _rotationAnimation, ctx.DeltaTime, finish);
        if (_transitionActive && _transition?.Update(ctx.DeltaTime, finish) is false) _transitionActive = false;

        PerformLayout(ctx, null);
    }

    private void PerformLayout(GameContext ctx, DesignRect? constraintRect)
    {
        // if (Parent is RenderItem parent)
        // {
        //     _color.SetAlpha(parent._color.A);
        // }

        // if (this is ConstraintBox)
        // {
        //     constraintRect = BoundingRect;
        // }
    }

    protected static void AdvanceAnimation<T>(ref T? anim, float dt, bool finish)
        where T : AnimationWithDuration
    {
        if (anim?.Update(dt, finish) is false)
        {
            anim = null;
        }
    }

    protected bool HasActiveAnimations => _fadeAnimation is not null || _moveAnimation is not null
        || _bezierMoveAnimation is not null || _scaleAnimation is not null || _rotationAnimation is not null || _transitionActive;

    public override bool IsAnimationActive(AnimationKind animationKind)
    {
        return animationKind switch
        {
            AnimationKind.Fade => _fadeAnimation is not null,
            AnimationKind.Move => _moveAnimation is not null,
            AnimationKind.BezierMove => _bezierMoveAnimation is not null,
            AnimationKind.Zoom => _scaleAnimation is not null,
            AnimationKind.Rotate => _rotationAnimation is not null,
            AnimationKind.Transition => _transitionActive,
            _ => false
        };
    }

    public void BeginMaskTransition(AssetRef<Texture> mask, float source, float destination, float feather,
        TimeSpan duration, NsEaseFunction ease)
    {
        _transition?.Dispose();
        _transition = new MaskTransition(mask, source, destination, feather, duration, ease);
        _transitionActive = duration > TimeSpan.Zero;
    }

    protected void PushQuad(RenderContext context, DrawBatch batch, in QuadPrimitive quad, Texture texture,
        Texture alphaMask, Vector2 alphaMaskPosition, ScreenRectU? scissor)
    {
        if (_transition is { } transition && transition.ProgressValue < 1)
        {
            if (transition.ProgressValue <= 0) return;
            if (transition.Mask is not { } mask) return;
            batch.PushTransitionQuad(quad, texture, context.Content.Get(mask), transition.ProgressValue,
                transition.Feather, FilterMode, scissor, blendMode: BlendMode);
        }
        else batch.PushQuad(quad, texture, alphaMask, alphaMaskPosition, BlendMode, FilterMode, scissor);
    }

    public override void Dispose()
    {
        _transition?.Dispose();
        _transition = null;
        base.Dispose();
    }

    public override void Move(RenderContext ctx, in NsCoordinate x, in NsCoordinate y, TimeSpan duration, NsEaseFunction easeFunction)
    {
        _bezierMoveAnimation = null;
        if (duration > TimeSpan.Zero)
        {
            _moveAnimation = new MoveAnimation(this, Transform.Position, Point(ctx, x, y), duration, easeFunction);
        }
        else
        {
            _moveAnimation = null;
            Transform.Position = Point(ctx, x, y);
        }
    }

    public override void BezierMove(in ProcessedBezierCurve curve, TimeSpan duration,
        NsEaseFunction easeFunction)
    {
        _moveAnimation = null;
        _bezierMoveAnimation = null;
        if (curve.Segments.IsDefaultOrEmpty) return;
        if (duration > TimeSpan.Zero)
            _bezierMoveAnimation = new BezierMoveAnimation(this, curve, duration, easeFunction);
        else
        {
            Vector2 destination = curve.Segments[^1].P3;
            Transform.Position = new Vector3(destination, Transform.Position.Z);
        }
    }

    public override void Fade(float dstOpacity, TimeSpan duration, NsEaseFunction easeFunction)
    {
        if (duration > TimeSpan.Zero)
        {
            _fadeAnimation = new OpacityAnimation(this, Color.A, dstOpacity, duration, easeFunction);
        }
        else
        {
            _fadeAnimation = null;
            Color.SetAlpha(dstOpacity);
        }
    }

    public void SetPivot(RenderContext ctx, NsCoordinate x, NsCoordinate y)
    {
        Vector2 current = Transform.Pivot ?? GetSize(ctx).ToVector2() * .5f;
        Vector2 size = GetSize(ctx).ToVector2();
        Transform.Pivot = new Vector2(Coordinate(x, current.X, size.X), Coordinate(y, current.Y, size.Y));

        static float Coordinate(NsCoordinate coordinate, float current, float size) => coordinate switch
        {
            { Kind: NsCoordinateKind.Value, Value: var value } => value.isRelative ? current + value.pos : value.pos,
            { Kind: NsCoordinateKind.Inherit } => current,
            { Kind: NsCoordinateKind.Alignment, Alignment: NsAlignment.Center } => size * .5f,
            { Kind: NsCoordinateKind.Alignment, Alignment: NsAlignment.Right or NsAlignment.Bottom } => size,
            _ => 0
        };
    }

    public override void Rotate(NsNumeric dstRotationX, NsNumeric dstRotationY, NsNumeric dstRotationZ,
        TimeSpan duration, NsEaseFunction easeFunction)
    {
        Vector3 destination = Transform.Rotation;
        dstRotationX.AssignTo(ref destination.X);
        dstRotationY.AssignTo(ref destination.Y);
        dstRotationZ.AssignTo(ref destination.Z);
        _rotationAnimation = null;
        if (duration > TimeSpan.Zero)
            _rotationAnimation = new RotationAnimation(this, Transform.Rotation, destination, duration, easeFunction);
        else
            Transform.Rotation = destination;
    }

    public override void Scale(in Vector3 dstScale, TimeSpan duration, NsEaseFunction easeFunction)
    {
        if (duration > TimeSpan.Zero)
        {
            _scaleAnimation = new ScaleAnimation(this, Transform.Scale, dstScale, duration, easeFunction);
        }
        else
        {
            _scaleAnimation = null;
            Transform.Scale = dstScale;
        }
    }

    public Vector3 Point(RenderContext ctx, NsCoordinate x, NsCoordinate y)
    {
        Vector3 pos = Transform.Position;
        DesignSizeU designResolution = ctx.DesignResolution;
        DesignSize parentBounds = Parent is RenderItem parentVisual
            ? parentVisual.GetSize(ctx)
            : designResolution.ToFloatSize();
        Vector3 origin = Parent switch
        {
            ConstraintBox { IsContainer: false } => Vector3.Zero,
            RenderItem parent => parent.Transform.Position,
            _ => Vector3.Zero
        };
        pos.X = x switch
        {
            { Kind: NsCoordinateKind.Value, Value: var val }
                => val.isRelative ? pos.X + val.pos : origin.X + val.pos,
            { Kind: NsCoordinateKind.Inherit } => origin.X,
            { Kind: NsCoordinateKind.Alignment, Alignment: var align } => align switch
                {
                    NsAlignment.Left => origin.X,
                    NsAlignment.Center => designResolution.Width / 2.0f,
                    NsAlignment.Right => origin.X + parentBounds.Width,
                    _ => throw ThrowHelper.ArgumentInvalid(nameof(x))
                },
            _ => 0.0f
        };
        pos.Y = y switch
        {
            { Kind: NsCoordinateKind.Value, Value: var val }
                => val.isRelative ? pos.Y + val.pos : origin.Y + val.pos,
            { Kind: NsCoordinateKind.Inherit } => origin.Y,
            { Kind: NsCoordinateKind.Alignment, Alignment: var align } => align switch
                {
                    NsAlignment.Top => origin.Y,
                    NsAlignment.Center => designResolution.Height / 2.0f,
                    NsAlignment.Bottom => origin.Y + parentBounds.Height,
                    _ => throw ThrowHelper.ArgumentInvalid(nameof(y))
                },
            _ => 0.0f
        };
        var anchorPoint = new Vector2(x.AnchorPoint, y.AnchorPoint);
        var size = GetSize(ctx).ToVector2();
        // N2: actual size of a dialogue box is ignored when computing its final position.
        if (this is DialogueBox)
        {
            size = ctx.DesignResolution.ToVector2();
        }

        pos -= new Vector3(anchorPoint * size, 0);
        return pos;
    }

    public int CompareTo(RenderItem? other) => _key.CompareTo(other.NotNull()._key);
}

internal static class RenderItemExt
{
    public static T WithPosition<T>(
        this T renderItem,
        RenderContext ctx,
        NsCoordinate x,
        NsCoordinate y)
        where T : RenderItem
    {
        renderItem.Transform.Position = renderItem.Point(ctx, x, y);
        return renderItem;
    }
}
