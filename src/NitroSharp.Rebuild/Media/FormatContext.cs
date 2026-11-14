using System;
using System.Diagnostics;
using System.IO;
using FFmpeg.AutoGen;
using static NitroSharp.Media.FFmpegUtil;

namespace NitroSharp.Media;

internal sealed unsafe class FormatContext : IDisposable
{
    private const int IoBufferSize = 4096;
    private const int ErrorIo = -5;
    private const int ErrorInvalidArgument = -22;

    private readonly Stream _stream;
    private AVFormatContext* _ctx;
    private AVIOContext* _ioContext;
    private AVPacket* _recvPacket;

    // Native code retains these function pointers until the contexts are freed.
    private readonly avio_alloc_context_read_packet _readFunc;
    private readonly avio_alloc_context_seek _seekFunc;
    private readonly AVCodecContext_get_format _getFormatFunc;

    public FormatContext(Stream stream)
    {
        ValidateAbi();
        _stream = stream;
        _readFunc = IoReadPacket;
        _seekFunc = IoSeek;
        _getFormatFunc = GetFormat;
        byte* ioBuffer = null;
        try
        {
            ioBuffer = (byte*)ffmpeg.av_malloc(IoBufferSize);
            if (ioBuffer is null)
                throw new OutOfMemoryException();
            _ioContext = ffmpeg.avio_alloc_context(
                ioBuffer, IoBufferSize,
                write_flag: 0, opaque: null,
                _readFunc, default, _seekFunc
            );
            if (_ioContext is null)
                throw new OutOfMemoryException();
            ioBuffer = null; // AVIO may replace the buffer; free its final buffer at disposal.
            _ctx = ffmpeg.avformat_alloc_context();
            _recvPacket = ffmpeg.av_packet_alloc();
            if (_ctx is null || _recvPacket is null)
                throw new OutOfMemoryException();
            _ctx->pb = _ioContext;
            fixed (AVFormatContext** context = &_ctx)
                CheckResult(ffmpeg.avformat_open_input(context, string.Empty, null, null));
            CheckResult(ffmpeg.avformat_find_stream_info(_ctx, null));
        }
        catch
        {
            ffmpeg.av_free(ioBuffer);
            Dispose();
            throw;
        }
    }

    private static AVPixelFormat GetFormat(AVCodecContext* s, AVPixelFormat* fmt)
    {
        int i = 0;
        while ((int)fmt[i] != -1)
        {
            if (fmt[i] == AVPixelFormat.AV_PIX_FMT_YUVJ444P)
                fmt[i] = AVPixelFormat.AV_PIX_FMT_YUV444P;
            i++;
        }
        return ffmpeg.avcodec_default_get_format(s, fmt);
    }

    public AVFormatContext* Inner => _ctx;
    public AVPacket* RecvPacket => _recvPacket;

    public AVCodecContext* OpenStream(int index)
    {
        if (index < 0)
            return null;
        AVStream* stream = _ctx->streams[index];
        AVCodec* codec = DecoderCollection.Shared.Get(stream->codecpar->codec_id);
        AVCodecContext* codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (codecCtx is null)
            throw new OutOfMemoryException();
        try
        {
            codecCtx->get_format = _getFormatFunc;
            CheckResult(ffmpeg.avcodec_parameters_to_context(codecCtx, stream->codecpar));
            CheckResult(ffmpeg.avcodec_open2(codecCtx, codec, null));
            return codecCtx;
        }
        catch
        {
            ffmpeg.avcodec_free_context(&codecCtx);
            throw;
        }
    }

    private long IoSeek(void* opaque, long offset, int whence)
    {
        // Managed exceptions must never unwind through FFmpeg's native frames.
        try
        {
            if (!_stream.CanSeek)
                return ErrorInvalidArgument;
            if ((whence & ffmpeg.AVSEEK_SIZE) != 0)
                return _stream.Length;
            whence &= ~ffmpeg.AVSEEK_FORCE;
            if (whence < (int)SeekOrigin.Begin || whence > (int)SeekOrigin.End)
                return ErrorInvalidArgument;
            return _stream.Seek(offset, (SeekOrigin)whence);
        }
        catch
        {
            return ErrorIo;
        }
    }

    private int IoReadPacket(void* opaque, byte* buf, int bufSize)
    {
        try
        {
            Debug.Assert(_stream.CanRead);
            int result = _stream.Read(new Span<byte>(buf, bufSize));
            return result == 0 ? ffmpeg.AVERROR_EOF : result;
        }
        catch
        {
            return ErrorIo;
        }
    }

    public void Dispose()
    {
        fixed (AVFormatContext** context = &_ctx)
            ffmpeg.avformat_close_input(context);
        fixed (AVPacket** packet = &_recvPacket)
            ffmpeg.av_packet_free(packet);
        if (_ioContext is not null)
        {
            // avformat_close_input does not own caller-provided custom AVIO.
            ffmpeg.av_free(_ioContext->buffer);
            _ioContext->buffer = null;
            fixed (AVIOContext** context = &_ioContext)
                ffmpeg.avio_context_free(context);
        }
        GC.KeepAlive(_readFunc);
        GC.KeepAlive(_seekFunc);
        GC.KeepAlive(_getFormatFunc);
    }
}
