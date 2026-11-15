using System;
using NitroSharp.Content;
using NitroSharp.NsScript;
using Veldrid;

namespace NitroSharp.Graphics;

internal sealed partial class MaskTransition : AnimationWithDuration, IDisposable
{
    private AssetRef<Texture>? _mask;
    private readonly float _source;
    private readonly float _destination;

    public MaskTransition(AssetRef<Texture>? mask, float source, float destination, float feather,
        TimeSpan duration, NsEaseFunction ease) : base(duration, ease)
    {
        _mask = mask;
        _source = source;
        _destination = destination;
        ProgressValue = source;
        Feather = feather;
        if (duration <= TimeSpan.Zero) Complete();
    }

    public float ProgressValue { get; private set; }
    public float Feather { get; }
    public AssetRef<Texture>? Mask => _mask;

    protected override AdvanceResult Advance()
    {
        ProgressValue = _source + (_destination - _source) * GetFactor(Progress, EaseFunction);
        if (!HasCompleted) return AdvanceResult.KeepGoing;
        Complete();
        return AdvanceResult.Stop;
    }

    private void Complete()
    {
        ProgressValue = _destination;
        if (_destination >= 1) Dispose();
    }

    public void Dispose()
    {
        _mask?.Dispose();
        _mask = null;
    }
}
