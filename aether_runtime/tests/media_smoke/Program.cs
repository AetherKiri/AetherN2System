using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using NitroSharp.Media;

internal static class Program
{
    private static readonly int Again = OperatingSystem.IsMacOS() ? -35 : -11;

    private static async Task Main(string[] args)
    {
        if (args.Length != 6)
            throw new ArgumentException("Expected media shim, native layout file, and four media fixtures.");

        VerifyLayouts(args[1]);
        NativeLibrary.SetDllImportResolver(typeof(ffmpeg).Assembly,
            (_, _, _) => NativeLibrary.Load(args[0]));

        foreach (string file in args.Skip(2))
            Decode(file);
        foreach (string file in args.Skip(2).Where(path => !path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)))
            await VerifyMediaStream(file);
    }

    private static unsafe void VerifyLayouts(string path)
    {
        Dictionary<string, long> layouts = [];
        AVFormatContext format = default;
        AVIOContext io = default;
        AVCodecContext codec = default;
        AVStream stream = default;
        AVFrame frame = default;
        AVPacket packet = default;
        layouts["AVFormatContext.size"] = sizeof(AVFormatContext);
        layouts["AVFormatContext.pb"] = (byte*)&format.pb - (byte*)&format;
        layouts["AVFormatContext.streams"] = (byte*)&format.streams - (byte*)&format;
        layouts["AVFormatContext.nb_streams"] = (byte*)&format.nb_streams - (byte*)&format;
        layouts["AVFormatContext.iformat"] = (byte*)&format.iformat - (byte*)&format;
        layouts["AVIOContext.size"] = sizeof(AVIOContext);
        layouts["AVIOContext.buffer"] = (byte*)&io.buffer - (byte*)&io;
        layouts["AVCodecContext.size"] = sizeof(AVCodecContext);
        layouts["AVCodecContext.get_format"] = (byte*)&codec.get_format - (byte*)&codec;
        layouts["AVCodecContext.ch_layout"] = (byte*)&codec.ch_layout - (byte*)&codec;
        layouts["AVCodecContext.sample_rate"] = (byte*)&codec.sample_rate - (byte*)&codec;
        layouts["AVCodecContext.sample_fmt"] = (byte*)&codec.sample_fmt - (byte*)&codec;
        layouts["AVStream.size"] = sizeof(AVStream);
        layouts["AVStream.codecpar"] = (byte*)&stream.codecpar - (byte*)&stream;
        layouts["AVStream.time_base"] = (byte*)&stream.time_base - (byte*)&stream;
        layouts["AVStream.duration"] = (byte*)&stream.duration - (byte*)&stream;
        layouts["AVStream.discard"] = (byte*)&stream.discard - (byte*)&stream;
        layouts["AVStream.index"] = (byte*)&stream.index - (byte*)&stream;
        layouts["AVFrame.size"] = sizeof(AVFrame);
        layouts["AVFrame.best_effort_timestamp"] = (byte*)&frame.best_effort_timestamp - (byte*)&frame;
        layouts["AVFrame.ch_layout"] = (byte*)&frame.ch_layout - (byte*)&frame;
        layouts["AVFrame.extended_data"] = (byte*)&frame.extended_data - (byte*)&frame;
        layouts["AVFrame.sample_rate"] = (byte*)&frame.sample_rate - (byte*)&frame;
        layouts["AVFrame.nb_samples"] = (byte*)&frame.nb_samples - (byte*)&frame;
        layouts["AVFrame.data"] = (byte*)&frame.data - (byte*)&frame;
        layouts["AVFrame.linesize"] = (byte*)&frame.linesize - (byte*)&frame;
        layouts["AVPacket.size"] = sizeof(AVPacket);
        layouts["AVPacket.data"] = (byte*)&packet.data - (byte*)&packet;
        layouts["AVPacket.stream_index"] = (byte*)&packet.stream_index - (byte*)&packet;
        layouts["AVChannelLayout.size"] = sizeof(AVChannelLayout);

        foreach (string line in File.ReadLines(path))
        {
            string[] parts = line.Split('=');
            long native = long.Parse(parts[1]);
            if (!layouts.Remove(parts[0], out long managed) || native != managed)
                throw new InvalidOperationException($"FFmpeg ABI layout mismatch: {line}, C#={managed}.");
        }
        if (layouts.Count != 0)
            throw new InvalidOperationException("Native layout file omitted expected FFmpeg fields.");
        Console.WriteLine("FFmpeg 8.1 ABI structure sizes and accessed field offsets match native headers.");
    }

    private static unsafe void Decode(string file)
    {
        using var input = new MemoryStream(File.ReadAllBytes(file), writable: false);
        using var format = new FormatContext(input);
        bool video = file.EndsWith(".avi", StringComparison.OrdinalIgnoreCase);
        int streamIndex = ffmpeg.av_find_best_stream(format.Inner,
            video ? AVMediaType.AVMEDIA_TYPE_VIDEO : AVMediaType.AVMEDIA_TYPE_AUDIO,
            -1, -1, null, 0);
        if (streamIndex < 0)
            throw new InvalidOperationException($"No requested stream in {file}.");

        AVCodecContext* codec = format.OpenStream(streamIndex);
        AVFrame* frame = ffmpeg.av_frame_alloc();
        using Resampler? resampler = video ? null : new Resampler(codec, AudioParameters.Default);
        long pcmBytes = 0;
        long signalMagnitude = 0;
        int decodedFrames = 0;

        void ReceiveFrames()
        {
            while (true)
            {
                int result = ffmpeg.avcodec_receive_frame(codec, frame);
                if (result == Again || result == ffmpeg.AVERROR_EOF)
                    break;
                FFmpegUtil.CheckResult(result);
                decodedFrames++;
                if (video)
                {
                    if (frame->width != 64 || frame->height != 48 || frame->data[0] is null)
                        throw new InvalidOperationException("Decoded video frame has invalid dimensions or planes.");
                }
                else
                {
                    BufferRequirements requirements = resampler!.GetBufferRequirements(*frame);
                    byte[] pcm = new byte[requirements.SizeInBytes];
                    int written = resampler.Convert(*frame, requirements, pcm);
                    pcmBytes += written;
                    foreach (short sample in MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, written)))
                        signalMagnitude += Math.Abs((int)sample);
                }
                ffmpeg.av_frame_unref(frame);
            }
        }

        try
        {
            while (ffmpeg.av_read_frame(format.Inner, format.RecvPacket) >= 0)
            {
                if (format.RecvPacket->stream_index == streamIndex)
                {
                    FFmpegUtil.CheckResult(ffmpeg.avcodec_send_packet(codec, format.RecvPacket));
                    ReceiveFrames();
                }
                ffmpeg.av_packet_unref(format.RecvPacket);
            }
            FFmpegUtil.CheckResult(ffmpeg.avcodec_send_packet(codec, null));
            ReceiveFrames();
            if (decodedFrames == 0 || (!video && (pcmBytes < 70000 || signalMagnitude == 0)))
                throw new InvalidOperationException($"No decoded media output for {file}.");

            Console.WriteLine($"{Path.GetFileName(file)}: {decodedFrames} decoded frames, {pcmBytes} PCM bytes.");
            FFmpegUtil.CheckResult(ffmpeg.avformat_seek_file(format.Inner, -1,
                -800000, 200000, 200000, 0));
            ffmpeg.avcodec_flush_buffers(codec);
            if (ffmpeg.av_read_frame(format.Inner, format.RecvPacket) < 0)
                throw new InvalidOperationException($"Could not read a packet after seeking {file}.");
            Console.WriteLine($"{Path.GetFileName(file)}: seek after EOF passed.");
        }
        finally
        {
            AVFrame* releasedFrame = frame;
            AVCodecContext* releasedCodec = codec;
            ffmpeg.av_frame_free(&releasedFrame);
            ffmpeg.avcodec_free_context(&releasedCodec);
        }
    }

    private static async Task VerifyMediaStream(string file)
    {
        await using var audio = new CaptureAudioSource();
        using var media = new MediaStream(
            new MemoryStream(File.ReadAllBytes(file)), null, audio, AudioParameters.Default);
        media.Start();
        await audio.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (audio.Bytes < 70000 || audio.SignalMagnitude == 0)
            throw new InvalidOperationException($"MediaStream emitted no decoded PCM for {file}.");

        for (int attempt = 0; media.IsPlaying && attempt < 100; attempt++)
            await Task.Delay(10);
        if (media.IsPlaying)
            throw new InvalidOperationException($"MediaStream did not reach EOF for {file}.");
        Console.WriteLine($"{Path.GetFileName(file)}: MediaStream emitted {audio.Bytes} PCM bytes and reached EOF.");
    }
}

internal sealed class CaptureAudioSource : AudioSource
{
    public long Bytes { get; private set; }
    public long SignalMagnitude { get; private set; }
    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool IsPlaying => !Completed.Task.IsCompleted;
    public override double SecondsElapsed => Bytes / 176400.0;
    public override float Volume { get; set; }
    public override void Play(PipeReader audioData) => _ = Consume(audioData);

    private async Task Consume(PipeReader reader)
    {
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();
                foreach (ReadOnlyMemory<byte> memory in result.Buffer)
                {
                    Bytes += memory.Length;
                    foreach (byte value in memory.Span)
                        SignalMagnitude += value;
                }
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                    break;
            }
            await reader.CompleteAsync();
            Completed.TrySetResult();
        }
        catch (Exception exception)
        {
            Completed.TrySetException(exception);
        }
    }

    public override void Pause() { }
    public override void Resume() { }
    public override void Stop() { }
    public override void FlushBuffers() { }
    public override ReadOnlySpan<short> GetCurrentBuffer() => default;
    public override ValueTask DisposeAsync() => default;
}
