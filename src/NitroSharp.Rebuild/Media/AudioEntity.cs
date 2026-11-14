using System;
using System.IO;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;

namespace NitroSharp.Media;

/// <summary>Script-visible audio with a reusable source and reopenable media.</summary>
internal sealed class AudioEntity : Entity
{
    private readonly Func<Stream> _openStream;
    private readonly AudioContext _audioContext;
    private readonly PooledAudioSource _pooledSource;
    private readonly float _volumeMultiplier;
    private readonly NsAudioKind _kind;
    private NsScriptVM? _vm;
    private float _systemGain = 1;
    private float _appliedVolume = float.NaN;
    private MediaStream? _stream;
    private bool _playbackStarted;
    private bool _hasPlayed;
    private bool _disposed;
    private bool _looping;
    private LoopRegion? _loopRegion;
    private float _volume = 1;
    private float _fadeStart;
    private float _fadeTarget;
    private float _fadeDuration;
    private float _fadeElapsed;

    public AudioEntity(
        EntityName name,
        Entity? parent,
        Func<Stream> openStream,
        AudioContext audioContext,
        NsAudioKind kind,
        NsScriptVM? vm = null)
        : base(name, parent)
    {
        _openStream = openStream;
        _audioContext = audioContext;
        _kind = kind;
        _vm = vm;
        _volumeMultiplier = kind switch
        {
            NsAudioKind.BackgroundMusic => 0.7f,
            NsAudioKind.Voice => 0.9f,
            _ => 1.0f
        };
        _pooledSource = audioContext.RentAudioSource();
        try
        {
            _stream = OpenMedia();
            Duration = _stream.Duration;
            if (vm is not null) _systemGain = SystemAudioSettings.GetGain(vm, kind);
            ApplyVolume();
        }
        catch
        {
            _pooledSource.Dispose();
            throw;
        }
    }

    public TimeSpan Duration { get; }
    public TimeSpan Elapsed => _playbackStarted ? _stream?.Elapsed ?? TimeSpan.Zero : TimeSpan.Zero;
    public bool IsPlaying => _playbackStarted && _stream is { IsPlaying: true };
    public bool IsIdle => (_hasPlayed && !IsPlaying) || (_volume == 0 && _fadeDuration == 0);
    public float Volume => _volume;

    private MediaStream OpenMedia()
    {
        Stream input = _openStream();
        try
        {
            var media = new MediaStream(input, null, _pooledSource.Value,
                _audioContext.Device.AudioParameters);
            if (_loopRegion is { } region)
                media.SetLoopRegion(region);
            media.ToggleLooping(_looping);
            return media;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    public override bool Request(NsEntityAction action)
    {
        if (_disposed)
            return false;
        switch (action)
        {
            case NsEntityAction.Start:
            case NsEntityAction.Play:
                Play();
                return true;
            case NsEntityAction.Stop:
                Stop();
                return true;
            case NsEntityAction.Pause:
                if (_playbackStarted)
                    _stream?.Pause();
                return true;
            case NsEntityAction.Resume:
                if (_playbackStarted)
                    _stream?.Resume();
                return true;
            default:
                return base.Request(action);
        }
    }

    private void Play()
    {
        if (_vm is not null) UpdateSystemGain(_vm);
        if (_playbackStarted && _stream is { IsPlaying: false })
            Stop();
        _stream ??= OpenMedia();
        if (_playbackStarted)
            _stream.Resume();
        else
        {
            _playbackStarted = true;
            _hasPlayed = true;
            _stream.Start();
        }
    }

    private void Stop()
    {
        // Join all decoder workers before reusing this source for another play.
        _stream?.Dispose();
        _stream = null;
        _playbackStarted = false;
    }

    public void SetLooping(bool enabled)
    {
        _looping = enabled;
        _stream?.ToggleLooping(enabled);
    }

    public void SetLoopRegion(TimeSpan start, TimeSpan end)
    {
        _loopRegion = new LoopRegion(start, end);
        _stream?.SetLoopRegion(_loopRegion);
        SetLooping(true);
    }

    public void SetVolume(float volume, TimeSpan duration)
    {
        _fadeStart = _volume;
        _fadeTarget = Math.Max(0, volume);
        _fadeDuration = (float)Math.Max(0, duration.TotalMilliseconds);
        _fadeElapsed = 0;
        if (_fadeDuration == 0)
        {
            _volume = _fadeTarget;
            ApplyVolume();
        }
    }

    public override void Update(GameContext ctx)
    {
        UpdateSystemGain(ctx.VM);
        AdvanceVolume(ctx.DeltaTime);
    }

    internal void UpdateSystemGain(NsScriptVM vm)
    {
        _vm = vm;
        _systemGain = SystemAudioSettings.GetGain(vm, _kind);
        ApplyVolume();
    }

    internal void AdvanceVolume(float milliseconds)
    {
        if (_fadeDuration == 0)
            return;
        _fadeElapsed = Math.Min(_fadeDuration, _fadeElapsed + Math.Max(0, milliseconds));
        _volume = _fadeStart + (_fadeTarget - _fadeStart) * (_fadeElapsed / _fadeDuration);
        if (_fadeElapsed >= _fadeDuration)
            _fadeDuration = 0;
        ApplyVolume();
    }

    private void ApplyVolume()
    {
        float volume = _volume * _volumeMultiplier * _systemGain;
        if (volume == _appliedVolume) return;
        _pooledSource.Value.Volume = volume;
        _appliedVolume = volume;
    }

    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _pooledSource.Dispose();
    }
}
