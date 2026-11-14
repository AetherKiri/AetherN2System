using System;

namespace FFmpeg.AutoGen
{
    public static class NativeLibs
    {
        public static class avcodec
        {
            public const string PortableName = "libavcodec.62";
            public const string WindowsName = "avcodec-62.dll";
            public const string OsxName = "libavcodec.62.dylib";
            public const string LinuxName = "libavcodec.so.62";

#if WINDOWS
            public const string Name = WindowsName;
#elif MACOS
            public const string Name = OsxName;
#elif LINUX
            public const string Name = LinuxName;
#else
            public const string Name = PortableName;
#endif
        }

        public static class avdevice
        {
            public const string PortableName = "libavdevice.62";
            public const string WindowsName = "avdevice-62.dll";
            public const string OsxName = "libavdevice.62.dylib";
            public const string LinuxName = "libavdevice.so.62";

#if WINDOWS
            public const string Name = WindowsName;
#elif MACOS
            public const string Name = OsxName;
#elif LINUX
            public const string Name = LinuxName;
#else
            public const string Name = PortableName;
#endif
        }

        public static class avformat
        {
            public const string PortableName = "libavformat.62";
            public const string WindowsName = "avformat-62.dll";
            public const string OsxName = "libavformat.62.dylib";
            public const string LinuxName = "libavformat.so.62";

#if WINDOWS
            public const string Name = WindowsName;
#elif MACOS
            public const string Name = OsxName;
#elif LINUX
            public const string Name = LinuxName;
#else
            public const string Name = PortableName;
#endif
        }

        public static class avutil
        {
            public const string PortableName = "libavutil.60";
            public const string WindowsName = "avutil-60.dll";
            public const string OsxName = "libavutil.60.dylib";
            public const string LinuxName = "libavutil.so.60";

#if WINDOWS
            public const string Name = WindowsName;
#elif MACOS
            public const string Name = OsxName;
#elif LINUX
            public const string Name = LinuxName;
#else
            public const string Name = PortableName;
#endif
        }

        public static class avfilter
        {
            public const string PortableName = "libavfilter.11";
            public const string WindowsName = "avfilter-11.dll";
            public const string OsxName = "libavfilter.11.dylib";
            public const string LinuxName = "libavfilter.so.11";

#if WINDOWS
            public const string Name = WindowsName;
#elif MACOS
            public const string Name = OsxName;
#elif LINUX
            public const string Name = LinuxName;
#else
            public const string Name = PortableName;
#endif
        }

        public static class swresample
        {
            public const string PortableName = "libswresample.6";
            public const string WindowsName = "swresample-6.dll";
            public const string OsxName = "libswresample.6.dylib";
            public const string LinuxName = "libswresample.so.6";

#if WINDOWS
            public const string Name = WindowsName;
#elif MACOS
            public const string Name = OsxName;
#elif LINUX
            public const string Name = LinuxName;
#else
            public const string Name = PortableName;
#endif
        }

        public static class swscale
        {
            public const string PortableName = "libswscale.9";
            public const string WindowsName = "swscale-9.dll";
            public const string OsxName = "libswscale.9.dylib";
            public const string LinuxName = "libswscale.so.9";

#if WINDOWS
            public const string Name = WindowsName;
#elif MACOS
            public const string Name = OsxName;
#elif LINUX
            public const string Name = LinuxName;
#else
            public const string Name = PortableName;
#endif
        }
    }
}
