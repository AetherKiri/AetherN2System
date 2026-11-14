using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AetherN2System;

internal static unsafe class NativeDependencies
{
    public static string DirectoryPath { get; private set; } = AppContext.BaseDirectory;

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public nint Name,
            Base,
            Symbol,
            Address;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dladdr")]
    private static extern int MacDlAddr(nint address, out DlInfo info);

    [DllImport("libdl.so.2", EntryPoint = "dladdr")]
    private static extern int LinuxDlAddr(nint address, out DlInfo info);

    [DllImport("kernel32", EntryPoint = "GetModuleHandleExW", CharSet = CharSet.Unicode)]
    private static extern int GetModuleHandleEx(uint flags, nint address, out nint module);

    [DllImport("kernel32", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileName(nint module, char* path, uint size);

    public static void Initialize()
    {
        nint address = (nint)(delegate* unmanaged[Cdecl]<uint>)&Exports.Version;
        if (OperatingSystem.IsWindows())
        {
            if (GetModuleHandleEx(6, address, out nint module) != 0)
            {
                char* path = stackalloc char[32768];
                uint length = GetModuleFileName(module, path, 32768);
                if (length > 0)
                    DirectoryPath = Path.GetDirectoryName(new string(path, 0, (int)length))!;
            }
        }
        else
        {
            DlInfo info;
            int found = OperatingSystem.IsMacOS()
                ? MacDlAddr(address, out info)
                : LinuxDlAddr(address, out info);
            if (found != 0)
                DirectoryPath = Path.GetDirectoryName(Marshal.PtrToStringUTF8(info.Name))!;
        }
        NitroSharp.GameContext.HostAssetsDirectory = DirectoryPath;
        NativeLibrary.SetDllImportResolver(typeof(FreeTypeBindings.FT).Assembly, Resolve);
        NativeLibrary.SetDllImportResolver(typeof(OpenAL.NativeLib).Assembly, Resolve);
        NativeLibrary.SetDllImportResolver(typeof(FFmpeg.AutoGen.ffmpeg).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        foreach (string directory in new[] { DirectoryPath, Path.Combine(DirectoryPath, "native") })
        foreach (
            string candidate in new[]
            {
                name,
                name
                    + (
                        OperatingSystem.IsMacOS() ? ".dylib"
                        : OperatingSystem.IsWindows() ? ".dll"
                        : ".so"
                    ),
            }
        )
            if (NativeLibrary.TryLoad(Path.Combine(directory, candidate), out nint value))
                return value;
        if (
            OperatingSystem.IsMacOS()
            && name.Contains("openal", StringComparison.OrdinalIgnoreCase)
            && NativeLibrary.TryLoad(
                "/System/Library/Frameworks/OpenAL.framework/OpenAL",
                out nint systemOpenAL
            )
        )
            return systemOpenAL;
        return 0;
    }
}
