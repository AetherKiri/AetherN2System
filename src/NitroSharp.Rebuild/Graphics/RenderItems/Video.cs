using System;
using System.IO;
using System.Numerics;
using NitroSharp.Graphics.Core;
using NitroSharp.Media;
using NitroSharp.NsScript;
using Veldrid;

namespace NitroSharp.Graphics;

internal sealed partial class Video : RenderItem
{
    private readonly GraphicsDevice _device;
    private readonly MediaStream _media;
    private readonly PooledAudioSource _audio;
    private readonly CommandList _upload;
    private readonly Fence _uploadFence;
    private readonly bool _alpha;
    private bool _uploadPending;
    private bool _hasFrame;
    private bool _stopped;
    private bool _disposed;

    public Video(EntityName name, Entity? parent, int priority, Stream input,
        GraphicsDevice device, AudioContext audio, bool loop, bool alpha) : base(name, parent, priority)
    {
        _device = device;
        SaveLoop = loop;
        _alpha = alpha;
        _audio = audio.RentAudioSource();
        try
        {
            _media = new MediaStream(input, device, _audio.Value, audio.Device.AudioParameters);
            _ = _media.VideoResolution;
            _media.ToggleLooping(loop);
            _upload = device.ResourceFactory.CreateCommandList();
            _uploadFence = device.ResourceFactory.CreateFence(false);
        }
        catch
        {
            _media?.Dispose();
            input.Dispose();
            _audio.Dispose();
            throw;
        }
    }

    internal bool IsPlaying => !_disposed && !_stopped && !IsPendingDestruction && _media.IsPlaying;
    internal TimeSpan Elapsed => _media.Elapsed;
    internal TimeSpan Duration => _media.Duration;
    internal int PresentedFrames { get; private set; }

    internal void Start() => _media.Start();

    public override DesignSize GetSize(RenderContext ctx)
    {
        TextureSizeU size = _media.VideoResolution;
        return new DesignSize(size.Width, _alpha ? size.Height / 2 : size.Height);
    }

    public override bool Request(NsEntityAction action)
    {
        switch (action)
        {
            case NsEntityAction.Stop:
                _stopped = true;
                _media.Pause();
                return true;
            case NsEntityAction.Pause:
                _media.Pause();
                return true;
            case NsEntityAction.Play:
            case NsEntityAction.Start:
            case NsEntityAction.Resume:
                if (!_disposed) { _stopped = false; _media.Start(); _media.Resume(); }
                return true;
            default:
                return base.Request(action);
        }
    }

    public override void Update(GameContext ctx)
    {
        base.Update(ctx);
        if (_disposed) return;
        if (_uploadPending)
        {
            if (!_uploadFence.Signaled) return;
            _media.VideoFrames.ReleaseFrame();
            _uploadPending = false;
            _device.ResetFence(_uploadFence);
        }
        if (_stopped || !_media.GetNextFrame(out YCbCrFrame frame)) return;
        _upload.Begin();
        frame.CopyToDeviceMemory(_upload);
        _upload.End();
        _device.SubmitCommands(_upload, _uploadFence);
        // Retain the staging slot until the GPU finishes reading it.
        _uploadPending = true;
        _hasFrame = true;
        PresentedFrames++;
        if (_restorePauseAfterFrame)
        {
            _restorePauseAfterFrame = false;
            _media.Pause();
        }
    }

    public override void Render(GameContext ctx)
    {
        if (_disposed || !_hasFrame || _stopped) return;
        RenderContext render = ctx.RenderContext;
        DesignSize size = GetSize(render);
        var quad = QuadPrimitive.Create(size,
            render.GetTransformMatrix(Transform, size, useScaling: true, aligned: false),
            Vector2.Zero, Vector2.One, Color.ToVector4());
        (Texture luma, Texture chroma) = _media.VideoFrames.GetDeviceTextures();
        render.MainBatch.PushVideo(quad, luma, chroma, _alpha, BlendMode, FilterMode, null);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_uploadPending) _device.WaitForFence(_uploadFence);
        // The previous engine frame may still sample the movie textures.
        _device.WaitForIdle();
        _media.Dispose();
        _audio.Dispose();
        _upload.Dispose();
        _uploadFence.Dispose();
        base.Dispose();
    }
}
