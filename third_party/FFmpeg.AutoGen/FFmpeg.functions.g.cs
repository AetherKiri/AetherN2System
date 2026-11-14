// Adapted from FFmpeg.AutoGen v8.1.0.0; see Readme.md.
using System;
using System.Runtime.InteropServices;

namespace FFmpeg.AutoGen;

public static unsafe partial class ffmpeg
{
    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void av_channel_layout_default(AVChannelLayout* @ch_layout, int @nb_channels);

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void av_channel_layout_uninit(AVChannelLayout* @channel_layout);

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int av_find_best_stream(AVFormatContext* @ic, AVMediaType @type, int @wanted_stream_nb, int @related_stream, AVCodec** @decoder_ret, int @flags);

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern AVFrame* av_frame_alloc();

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void av_frame_free(AVFrame** @frame);

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void av_frame_unref(AVFrame* @frame);

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void av_free(void* @ptr);

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern AVRational av_guess_frame_rate(AVFormatContext* @ctx, AVStream* @stream, AVFrame* @frame);

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void av_log_set_level(int @level);

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void* av_malloc(ulong @size);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern AVPacket* av_packet_alloc();

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void av_packet_free(AVPacket** @pkt);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void av_packet_unref(AVPacket* @pkt);

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int av_read_frame(AVFormatContext* @s, AVPacket* @pkt);

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern long av_rescale_rnd(long @a, long @b, long @c, AVRounding @rnd);

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int av_samples_get_buffer_size(int* @linesize, int @nb_channels, int @nb_samples, AVSampleFormat @sample_fmt, int @align);

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int av_strerror(int @errnum, byte* @errbuf, ulong @errbuf_size);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern AVCodecContext* avcodec_alloc_context3(AVCodec* @codec);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern AVPixelFormat avcodec_default_get_format(AVCodecContext* @s, AVPixelFormat* @fmt);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern AVCodec* avcodec_find_decoder(AVCodecID @id);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void avcodec_flush_buffers(AVCodecContext* @avctx);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void avcodec_free_context(AVCodecContext** @avctx);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int avcodec_open2(AVCodecContext* @avctx, AVCodec* @codec, AVDictionary** @options);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int avcodec_parameters_to_context(AVCodecContext* @codec, AVCodecParameters* @par);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int avcodec_receive_frame(AVCodecContext* @avctx, AVFrame* @frame);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int avcodec_send_packet(AVCodecContext* @avctx, AVPacket* @avpkt);

    [DllImport(NativeLibs.avcodec.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint avcodec_version();

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern AVFormatContext* avformat_alloc_context();

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void avformat_close_input(AVFormatContext** @s);

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int avformat_find_stream_info(AVFormatContext* @ic, AVDictionary** @options);

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int avformat_open_input(AVFormatContext** @ps,
    [MarshalAs(UnmanagedType.LPUTF8Str)]
        string @url, AVInputFormat* @fmt, AVDictionary** @options);

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int avformat_seek_file(AVFormatContext* @s, int @stream_index, long @min_ts, long @ts, long @max_ts, int @flags);

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint avformat_version();

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern AVIOContext* avio_alloc_context(byte* @buffer, int @buffer_size, int @write_flag, void* @opaque, avio_alloc_context_read_packet_func @read_packet, avio_alloc_context_write_packet_func @write_packet, avio_alloc_context_seek_func @seek);

    [DllImport(NativeLibs.avformat.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void avio_context_free(AVIOContext** @s);

    [DllImport(NativeLibs.avutil.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint avutil_version();

    [DllImport(NativeLibs.swresample.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int swr_alloc_set_opts2(SwrContext** @ps, AVChannelLayout* @out_ch_layout, AVSampleFormat @out_sample_fmt, int @out_sample_rate, AVChannelLayout* @in_ch_layout, AVSampleFormat @in_sample_fmt, int @in_sample_rate, int @log_offset, void* @log_ctx);

    [DllImport(NativeLibs.swresample.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int swr_convert(SwrContext* @s, byte** @out, int @out_count, byte** @in, int @in_count);

    [DllImport(NativeLibs.swresample.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void swr_free(SwrContext** @s);

    [DllImport(NativeLibs.swresample.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern long swr_get_delay(SwrContext* @s, long @base);

    [DllImport(NativeLibs.swresample.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int swr_init(SwrContext* @s);

    [DllImport(NativeLibs.swresample.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint swresample_version();

    [DllImport(NativeLibs.swscale.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sws_freeContext(SwsContext* @swsContext);

    [DllImport(NativeLibs.swscale.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern SwsContext* sws_getContext(int @srcW, int @srcH, AVPixelFormat @srcFormat, int @dstW, int @dstH, AVPixelFormat @dstFormat, int @flags, SwsFilter* @srcFilter, SwsFilter* @dstFilter, double* @param);

    [DllImport(NativeLibs.swscale.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sws_scale(SwsContext* @c, byte** @srcSlice, int* @srcStride, int @srcSliceY, int @srcSliceH, byte** @dst, int* @dstStride);

    [DllImport(NativeLibs.swscale.Name, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint swscale_version();
}
