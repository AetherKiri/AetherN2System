using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NitroSharp.Graphics.Core;
using NitroSharp.NsScript;

namespace NitroSharp.Graphics;

internal sealed class FrameSequence : RenderItem
{
    private readonly SpriteTexture[] _frames;
    private readonly List<SpriteTexture> _owned = [];
    private readonly SpriteTexture? _finalFrame;
    private readonly double _interval;
    private readonly bool _loop;
    private readonly Action<FrameSequence>? _completed;
    private readonly Action<SpriteTexture> _release;
    private readonly Func<bool>? _ready;
    private SpriteTexture _current;
    private double _elapsed;
    private bool _disposed;
    private bool _initialized;
    private bool _stopped;
    private bool _removeRequested;
    private bool _prepared;

    public FrameSequence(EntityName name, Entity? parent, int priority, ReadOnlySpan<string> frames,
        int ticksPerFrame, string finalFrame, bool removeOnCompletion, bool loop,
        Func<string, SpriteTexture?> load, Action<FrameSequence>? completed = null,
        Action<SpriteTexture>? release = null, Func<bool>? ready = null) : base(name, parent, priority)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerFrame);
        _frames = new SpriteTexture[frames.Length];
        _interval = 33.0 * ticksPerFrame;
        _loop = loop;
        _completed = completed;
        _release = release ?? (texture => texture.Dispose());
        _ready = ready;
        RemoveOnCompletion = removeOnCompletion;
        var loaded = new Dictionary<string, SpriteTexture?>(StringComparer.Ordinal);
        try
        {
            for (int i = 0; i < frames.Length; i++)
            {
                SpriteTexture? texture = loadFrame(frames[i]);
                if (i == 0 && texture is null)
                    throw new FileNotFoundException("The first frame of an animation must exist.", frames[i]);
                _frames[i] = texture ?? _frames[i - 1];
            }
            _current = _frames[0];
            _finalFrame = string.IsNullOrEmpty(finalFrame) ? null : loadFrame(finalFrame);
        }
        catch
        {
            foreach (SpriteTexture texture in _owned) _release(texture);
            _owned.Clear();
            throw;
        }

        SpriteTexture? loadFrame(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (loaded.TryGetValue(path, out SpriteTexture? existing)) return existing;
            SpriteTexture? texture = load(path);
            loaded.Add(path, texture);
            if (texture is { } owned) _owned.Add(owned);
            return texture;
        }
    }

    public bool RemoveOnCompletion { get; }
    public bool IsComplete { get; private set; }
    internal int FrameIndex { get; private set; }
    internal SpriteTexture CurrentTexture => _current;

    public override bool IsAnimationActive(AnimationKind animationKind)
        => animationKind == AnimationKind.FrameSequence
            ? !IsComplete && !_disposed && !IsPendingDestruction
            : base.IsAnimationActive(animationKind);

    public override DesignSize GetSize(RenderContext ctx) => _current.GetSize(ctx);

    public override bool Request(NsEntityAction action)
    {
        if (action == NsEntityAction.Stop)
        {
            if (!_disposed && !IsComplete && !IsPendingDestruction)
            {
                _stopped = true;
                Finish();
            }
            return true;
        }
        if (action is NsEntityAction.Start or NsEntityAction.Resume && _stopped
            && !_disposed && !IsPendingDestruction && !_removeRequested)
        {
            IsComplete = false;
            _stopped = false;
            _initialized = false;
            _current = _frames[FrameIndex];
            return true;
        }
        return base.Request(action);
    }

    internal void Advance(double milliseconds)
    {
        if (_disposed || IsComplete || IsPendingDestruction) return;
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        _elapsed += milliseconds;
        double steps = Math.Floor(_elapsed / _interval);
        if (steps < 1) return;
        _elapsed %= _interval;
        if (_loop)
        {
            FrameIndex = (FrameIndex + (int)(steps % _frames.Length)) % _frames.Length;
            _current = _frames[FrameIndex];
            return;
        }
        int remaining = Math.Max(1, _frames.Length - 1 - FrameIndex);
        if (steps < remaining)
        {
            FrameIndex += (int)steps;
            _current = _frames[FrameIndex];
            return;
        }
        FrameIndex = _frames.Length - 1;
        _current = _frames[FrameIndex];
        Finish();
    }

    private void Finish()
    {
        _current = _finalFrame ?? _current;
        _elapsed = 0;
        IsComplete = true;
        _removeRequested = RemoveOnCompletion;
        _completed?.Invoke(this);
    }

    public override void Update(GameContext ctx)
    {
        if (_disposed || IsPendingDestruction) return;
        base.Update(ctx);
        AdvanceFrame(ctx.DeltaTime);
    }

    internal void AdvanceFrame(double milliseconds)
    {
        if (!_prepared)
        {
            if (_ready is not null && !_ready()) return;
            _prepared = true;
        }
        // The creating script may have run earlier in this same engine frame.
        // Its elapsed delta must not advance the newly displayed START image.
        if (_initialized) Advance(milliseconds);
        else _initialized = true;
    }

    public override void Render(GameContext ctx)
    {
        if (_disposed || _removeRequested) return;
        RenderContext render = ctx.RenderContext;
        DesignSize size = GetSize(render);
        (Vector2 topLeft, Vector2 bottomRight) = _current.GetTexCoords(render);
        var quad = QuadPrimitive.Create(size,
            render.GetTransformMatrix(Transform, size, useScaling: true, aligned: false),
            topLeft, bottomRight, Color.ToVector4());
        PushQuad(render, render.MainBatch, quad, _current.Resolve(render), render.WhiteTexture, Vector2.Zero, null);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (SpriteTexture texture in _owned) _release(texture);
        _owned.Clear();
        base.Dispose();
    }
}
