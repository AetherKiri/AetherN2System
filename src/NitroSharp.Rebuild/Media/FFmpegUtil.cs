using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace NitroSharp.Media;

internal static unsafe class FFmpegUtil
{
    private static readonly Lazy<bool> AbiCompatible = new(() =>
    {
        VerifyVersion("avcodec", ffmpeg.avcodec_version(), 62);
        VerifyVersion("avformat", ffmpeg.avformat_version(), 62);
        VerifyVersion("avutil", ffmpeg.avutil_version(), 60);
        VerifyVersion("swresample", ffmpeg.swresample_version(), 6);
        VerifyVersion("swscale", ffmpeg.swscale_version(), 9);
        return true;
    });

    public static void ValidateAbi() => _ = AbiCompatible.Value;

    private static void VerifyVersion(string library, uint version, uint expectedMajor)
    {
        uint actualMajor = version >> 16;
        if (actualMajor != expectedMajor)
            throw new FFmpegException($"{library} ABI {actualMajor} is incompatible; expected FFmpeg 8.1 ABI {expectedMajor}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CheckResult(int result)
    {
        if (result < 0)
        {
            Throw(result);
        }
    }

    private static void Throw(int result)
    {
        throw new FFmpegException(GetErrorMessage(result));
    }

    private static string GetErrorMessage(int error)
    {
        const int bufferSize = 1024;
        byte* buffer = stackalloc byte[bufferSize];
        CheckResult(ffmpeg.av_strerror(error, buffer, bufferSize));
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? string.Empty;
    }

    public static bool IsNullPacket(this in AVPacket packet)
        => packet.data is null;

    public static double ToDouble(this AVRational rational)
        => rational.num / (double)rational.den;

    public static ulong GetAvChannelLayout(ChannelLayout channelLayout)
    {
        return channelLayout == ChannelLayout.Mono
            ? ffmpeg.AV_CH_LAYOUT_MONO
            : ffmpeg.AV_CH_LAYOUT_STEREO;
    }

    public static void CopyPointers(this byte_ptr8 src, Span<IntPtr> dst)
    {
        for (uint i = 0; i < 8; i++)
        {
            dst[(int)i] = (IntPtr)src[i];
        }
    }

    public static void CopyTo(this int8 src, Span<int> dst)
    {
        for (uint i = 0; i < 8; i++)
        {
            dst[(int)i] = src[i];
        }
    }
}

public class FFmpegException(string message) : Exception(message);
