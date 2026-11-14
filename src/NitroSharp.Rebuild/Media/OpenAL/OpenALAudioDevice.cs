using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static OpenAL.AL10;
using static OpenAL.ALC10;

namespace NitroSharp.Media;

internal sealed class OpenALAudioDevice : AudioDevice
{
    internal static readonly object Sync = new();
    private readonly nint _device;
    private readonly nint _context;
    private readonly List<OpenALAudioSource> _sources = [];

    public OpenALAudioDevice(in AudioParameters parameters)
        : base(parameters)
    {
        lock (Sync)
        {
            _device = alcOpenDevice(null!);
            if (_device == 0)
                throw new InvalidOperationException(
                    "OpenAL could not open the audio output device."
                );
            _context = alcCreateContext(_device, [0]);
            if (_context == 0)
            {
                alcCloseDevice(_device);
                throw new InvalidOperationException("OpenAL could not create an audio context.");
            }
            Activate();
        }
    }

    internal void Activate() => alcMakeContextCurrent(_context);

    public override AudioBackend Backend => AudioBackend.OpenAL;

    public override AudioSource CreateAudioSource(int bufferSize = 16384, int bufferCount = 16)
    {
        lock (Sync)
        {
            Activate();
            var source = new OpenALAudioSource(this, bufferSize, bufferCount);
            _sources.Add(source);
            return source;
        }
    }

    public void PauseAll(bool pause)
    {
        foreach (var source in _sources)
            source.SetHostPause(pause);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var source in _sources)
            await source.DisposeAsync();
        _sources.Clear();
        lock (Sync)
        {
            alcMakeContextCurrent(0);
            alcDestroyContext(_context);
            alcCloseDevice(_device);
        }
    }
}

internal sealed class OpenALAudioSource : AudioSource
{
    private readonly OpenALAudioDevice _device;
    private uint _source;
    private readonly uint[] _buffers;
    private readonly int _bufferSize;
    private readonly Dictionary<uint, int> _queuedSizes = [];
    private PipeReader? _reader;
    private CancellationTokenSource? _cancel;
    private Task? _task;
    private bool _paused,
        _hostPaused;
    private long _bytesPlayed;
    private float _volume = 1;
    private short[] _current = [];
    private Exception? _failure;

    public OpenALAudioSource(OpenALAudioDevice device, int size, int count)
    {
        _device = device;
        _bufferSize = Math.Max(4, size / 4 * 4);
        _buffers = new uint[Math.Max(2, count)];
        alGenSources(1, out _source);
        alGenBuffers(_buffers.Length, _buffers);
    }

    private void Activate() => _device.Activate();

    public override bool IsPlaying
    {
        get
        {
            lock (OpenALAudioDevice.Sync)
            {
                Activate();
                if (_failure is not null)
                    throw new InvalidOperationException("OpenAL streaming failed.", _failure);
                alGetSourcei(_source, AL_SOURCE_STATE, out int state);
                return state is AL_PLAYING or AL_PAUSED || _task is { IsCompleted: false };
            }
        }
    }
    public override double SecondsElapsed
    {
        get
        {
            lock (OpenALAudioDevice.Sync)
            {
                Activate();
                alGetSourcef(_source, 0x1024, out float seconds);
                return _bytesPlayed
                        / (double)(
                            _device.AudioParameters.SampleRate
                            * _device.AudioParameters.ChannelCount
                            * 2
                        )
                    + seconds;
            }
        }
    }
    public override float Volume
    {
        get => _volume;
        set
        {
            lock (OpenALAudioDevice.Sync)
            {
                Activate();
                _volume = Math.Max(0, value);
                alSourcef(_source, AL_GAIN, _volume);
            }
        }
    }

    public override ReadOnlySpan<short> GetCurrentBuffer() => _current;

    public override void Play(PipeReader data)
    {
        Stop();
        _failure = null;
        _reader = data;
        _cancel = new();
        _bytesPlayed = 0;
        _paused = false;
        _task = Task.Run(() => Consume(data, _cancel.Token));
    }

    private async Task Consume(PipeReader reader, CancellationToken cancel)
    {
        var free = new Queue<uint>(_buffers);
        var bytes = new byte[_bufferSize];
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                lock (OpenALAudioDevice.Sync)
                {
                    Activate();
                    alGetSourcei(_source, AL_BUFFERS_PROCESSED, out int processed);
                    while (processed-- > 0)
                    {
                        uint buffer = 0;
                        alSourceUnqueueBuffers(_source, 1, ref buffer);
                        if (_queuedSizes.Remove(buffer, out int size))
                            _bytesPlayed += size;
                        free.Enqueue(buffer);
                    }
                }
                if (free.Count == 0 || _paused || _hostPaused)
                {
                    await Task.Delay(5, cancel);
                    continue;
                }
                ReadResult result = await reader.ReadAsync(cancel);
                ReadOnlySequence<byte> sequence = result.Buffer;
                int count = (int)Math.Min(sequence.Length, _bufferSize);
                count -= count % (_device.AudioParameters.ChannelCount * 2);
                if (count > 0)
                {
                    sequence.Slice(0, count).CopyTo(bytes);
                    reader.AdvanceTo(sequence.GetPosition(count));
                    lock (OpenALAudioDevice.Sync)
                    {
                        Activate();
                        uint buffer = free.Dequeue();
                        alBufferData(
                            buffer,
                            _device.AudioParameters.ChannelCount == 2
                                ? AL_FORMAT_STEREO16
                                : AL_FORMAT_MONO16,
                            bytes,
                            count,
                            (int)_device.AudioParameters.SampleRate
                        );
                        _queuedSizes[buffer] = count;
                        alSourceQueueBuffers(_source, 1, ref buffer);
                        _current = MemoryMarshal
                            .Cast<byte, short>(bytes.AsSpan(0, count))
                            .ToArray();
                        alGetSourcei(_source, AL_SOURCE_STATE, out int state);
                        if (state != AL_PLAYING && !_paused && !_hostPaused)
                            alSourcePlay(_source);
                    }
                }
                else
                {
                    reader.AdvanceTo(sequence.Start, sequence.End);
                    if (result.IsCompleted || result.IsCanceled)
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _failure = e;
        }
    }

    public override void Pause()
    {
        _paused = true;
        UpdatePause();
    }

    public override void Resume()
    {
        _paused = false;
        UpdatePause();
    }

    internal void SetHostPause(bool paused)
    {
        _hostPaused = paused;
        UpdatePause();
    }

    private void UpdatePause()
    {
        lock (OpenALAudioDevice.Sync)
        {
            Activate();
            if (_paused || _hostPaused)
                alSourcePause(_source);
            else
            {
                alGetSourcei(_source, AL_BUFFERS_QUEUED, out int queued);
                if (queued > 0)
                    alSourcePlay(_source);
            }
        }
    }

    public override void Stop()
    {
        _cancel?.Cancel();
        _reader?.CancelPendingRead();
        _task?.GetAwaiter().GetResult();
        _task = null;
        _reader = null;
        _cancel?.Dispose();
        _cancel = null;
        FlushBuffers();
    }

    public override void FlushBuffers()
    {
        lock (OpenALAudioDevice.Sync)
        {
            Activate();
            alSourceStop(_source);
            alGetSourcei(_source, AL_BUFFERS_QUEUED, out int queued);
            while (queued-- > 0)
            {
                uint unused = 0;
                alSourceUnqueueBuffers(_source, 1, ref unused);
            }
            _queuedSizes.Clear();
            _current = [];
        }
    }

    public override ValueTask DisposeAsync()
    {
        if (_source == 0)
            return default;
        Stop();
        lock (OpenALAudioDevice.Sync)
        {
            Activate();
            alDeleteSources(1, ref _source);
            alDeleteBuffers(_buffers.Length, _buffers);
            _source = 0;
        }
        return default;
    }
}
