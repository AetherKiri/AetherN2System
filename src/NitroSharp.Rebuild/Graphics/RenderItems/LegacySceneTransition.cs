using System;
using System.Numerics;
using NitroSharp.Content;
using NitroSharp.Graphics.Core;
using NitroSharp.NsScript;
using Veldrid;

namespace NitroSharp.Graphics;

internal sealed class LegacySceneTransition : RenderItem
{
    private readonly RenderItem[] _sources;
    private readonly AssetRef<Texture>? _mask;
    private readonly float _duration;
    private readonly bool _dynamic;
    private SpriteTexture? _snapshot;
    private RenderTarget? _incomingTarget;
    private readonly RenderTarget?[] _feedbackTargets = new RenderTarget?[2];
    private SpriteTexture? _incoming;
    private readonly SpriteTexture?[] _feedback = new SpriteTexture?[2];
    private int _feedbackIndex;
    private int _lastStep = -1;
    private float _elapsed;
    private bool _disposed;

    public LegacySceneTransition(EntityName name, Entity parent, int priority, RenderItem[] sources,
        AssetRef<Texture>? mask, TimeSpan duration, bool dynamic = false) : base(name, parent, priority)
    {
        _sources = sources;
        _mask = mask;
        _duration = (float)duration.TotalMilliseconds;
        _dynamic = dynamic;
        foreach (RenderItem source in sources) source.Request(NsEntityAction.Disable);
    }

    internal bool IsCaptured => _snapshot is not null;

    public override bool IsAnimationActive(AnimationKind kind)
        => kind == AnimationKind.Transition ? !_disposed && !IsPendingDestruction : base.IsAnimationActive(kind);

    public override DesignSize GetSize(RenderContext context) => context.DesignResolution.ToFloatSize();

    public override void Update(GameContext context)
    {
        if (_snapshot is null || _disposed || IsPendingDestruction) return;
        _elapsed += Math.Max(0, context.DeltaTime);
        if (_elapsed >= _duration)
        {
            RestoreSources();
            World.DestroyEntity(this);
        }
    }

    public override void Render(GameContext context)
    {
        if (_disposed || IsPendingDestruction) return;
        RenderContext render = context.RenderContext;
        if (_dynamic)
        {
            RenderDynamic(context);
            return;
        }
        _snapshot ??= render.CaptureScene(context, _sources);
        SpriteTexture snapshot = _snapshot.Value;
        (Vector2 topLeft, Vector2 bottomRight) = snapshot.GetTexCoords(render);
        var size = GetSize(render);
        var quad = QuadPrimitive.Create(size, render.GetTransformMatrix(Transform, size, true, false),
            topLeft, bottomRight, Color.ToVector4());
        render.MainBatch.PushTransitionQuad(quad, snapshot.Resolve(render),
            _mask is { } mask ? render.Content.Get(mask) : render.WhiteTexture,
            Math.Clamp(_elapsed / _duration, 0, 1), 0, FilterMode.Point, null, _mask is null ? 1 : 2);
    }

    private void RenderDynamic(GameContext context)
    {
        RenderContext render = context.RenderContext;
        if (_snapshot is null)
        {
            _snapshot = render.CapturePresentedFrame();
            _incomingTarget = render.CreateSceneTarget();
            _incoming = render.SceneTexture(_incomingTarget);
            for (int i = 0; i < 2; i++)
            {
                _feedbackTargets[i] = render.CreateSceneTarget();
                _feedback[i] = render.SceneTexture(_feedbackTargets[i]!);
            }
        }
        int step = LegacyTransitionCoverage.LastStep(_elapsed / _duration);
        if (step > _lastStep)
        {
            render.CaptureScene(context, _sources, _incomingTarget!);
            int next = 1 - _feedbackIndex;
            render.CompositeScene(_lastStep < 0 ? _snapshot.Value : _feedback[_feedbackIndex]!.Value,
                _incoming!.Value, _feedbackTargets[next]!, LegacyTransitionCoverage.Incremental(_lastStep, step));
            _feedbackIndex = next;
            _lastStep = step;
        }
        SpriteTexture snapshot = _feedback[_feedbackIndex]!.Value;
        (Vector2 topLeft, Vector2 bottomRight) = snapshot.GetTexCoords(render);
        var size = GetSize(render);
        var quad = QuadPrimitive.Create(size, render.GetTransformMatrix(Transform, size, true, false),
            topLeft, bottomRight, Color.ToVector4());
        render.MainBatch.PushQuad(quad, snapshot.Resolve(render), render.WhiteTexture, Vector2.Zero,
            BlendMode.Alpha, FilterMode.Point, null);
    }

    private void RestoreSources()
    {
        foreach (RenderItem source in _sources)
            if (!source.IsPendingDestruction) source.Request(NsEntityAction.Enable);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreSources();
        _mask?.Dispose();
        _snapshot?.Dispose();
        _incoming?.Dispose();
        foreach (SpriteTexture? texture in _feedback) texture?.Dispose();
        _snapshot = null;
        base.Dispose();
    }
}
