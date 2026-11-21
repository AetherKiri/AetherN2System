using System;
using System.Numerics;
using NitroSharp.Graphics.Core;
using NitroSharp.NsScript;

namespace NitroSharp.Graphics;

// Plain reads the last completed frame. Passive retains that frame while the
// entity's move, opacity and other outer animations continue independently.
internal sealed partial class PlainEffect : RenderItem
{
    private readonly DesignSizeU _size;
    private readonly Func<DesignRectU, SpriteTexture> _capture;
    private readonly Action<SpriteTexture> _release;
    private SpriteTexture? _snapshot;
    private DesignRectU? _sourceRectangle;
    private bool _disposed;
    private uint _blurPasses;
    private bool _blurDirty;
    private SpriteTexture? _blurredSnapshot;

    public PlainEffect(EntityName name, Entity? parent, int priority, DesignSizeU size,
        Func<DesignRectU, SpriteTexture> capture, Action<SpriteTexture>? release = null)
        : base(name, parent, priority)
    {
        _size = size;
        _capture = capture;
        _release = release ?? (texture => texture.Dispose());
    }

    public bool IsPassive { get; private set; }
    internal SpriteTexture? Snapshot => _snapshot;

    public override DesignSize GetSize(RenderContext ctx) => _size.ToFloatSize();

    internal void RefreshSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPassive) return;
        if (_sourceRectangle is null)
        {
            Vector3 position = Transform.Position;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)
                || position.X < 0 || position.Y < 0)
                throw new NotSupportedException("Plain effect capture requires a nonnegative finite source position.");
            _sourceRectangle = new DesignRectU((uint)position.X, (uint)position.Y, _size.Width, _size.Height);
        }
        SpriteTexture next = _capture(_sourceRectangle.Value);
        if (_snapshot is { } previous) _release(previous);
        _snapshot = next;
        _blurDirty = true;
    }

    public override bool Request(NsEntityAction action)
    {
        if (action != NsEntityAction.Passive) return base.Request(action);
        if (!IsPassive)
        {
            RefreshSnapshot();
            IsPassive = true;
        }
        return true;
    }

    public override void Update(GameContext ctx)
    {
        base.Update(ctx);
        RefreshSnapshot();
    }

    public void SetBlur(uint passes)
    {
        _blurPasses = passes;
        _blurDirty = true;
    }

    public override void Render(GameContext ctx)
    {
        if (_snapshot is not { } texture) return;
        if (_blurDirty)
        {
            _blurredSnapshot?.Dispose();
            _blurredSnapshot = _blurPasses > 0 ? SnapshotEffects.Apply(ctx.RenderContext, texture, _blurPasses) : null;
            _blurDirty = false;
        }
        texture = _blurredSnapshot ?? texture;
        RenderContext render = ctx.RenderContext;
        (Vector2 topLeft, Vector2 bottomRight) = texture.GetTexCoords(render);
        var quad = QuadPrimitive.Create(GetSize(render),
            render.GetTransformMatrix(Transform, GetSize(render), useScaling: true, aligned: false),
            topLeft, bottomRight, Color.ToVector4());
        PushQuad(render, render.MainBatch, quad, texture.Resolve(render), render.WhiteTexture,
            Vector2.Zero, null);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_snapshot is { } texture) _release(texture);
        _snapshot = null;
        _blurredSnapshot?.Dispose();
        _blurredSnapshot = null;
        base.Dispose();
    }
}
