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

#if ANDROID
    [DllImport("libdl.so", EntryPoint = "dladdr")]
#else
    [DllImport("libdl.so.2", EntryPoint = "dladdr")]
#endif
    private static extern int LinuxDlAddr(nint address, out DlInfo info);

    [DllImport("kernel32", EntryPoint = "GetModuleHandleExW", CharSet = CharSet.Unicode)]
    private static extern int GetModuleHandleEx(uint flags, nint address, out nint module);

    [DllImport("kernel32", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileName(nint module, char* path, uint size);

    public static void Initialize(string writable)
    {
#if !IOS
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
#endif
#if ANDROID || IOS
        // APK native-library directories cannot carry arbitrary font files. Keep
        // the same font assets available to both embedded mobile hosts.
        string assets = Path.Combine(writable, "n2system-runtime");
        string fonts = Path.Combine(assets, "Fonts");
        Directory.CreateDirectory(fonts);
        Assembly host = typeof(NativeDependencies).Assembly;
        const string prefix = "AetherN2System.Fonts.";
        foreach (string resource in host.GetManifestResourceNames())
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            using Stream source = host.GetManifestResourceStream(resource)!;
            string destination = Path.Combine(fonts, resource[prefix.Length..]);
            if (File.Exists(destination) && new FileInfo(destination).Length == source.Length)
                continue;
            string temporary = destination + ".tmp";
            using (FileStream output = File.Create(temporary))
                source.CopyTo(output);
            File.Move(temporary, destination, overwrite: true);
        }
        NitroSharp.GameContext.HostAssetsDirectory = assets;
#endif
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
#if ANDROID
        string library = assembly == typeof(FFmpeg.AutoGen.ffmpeg).Assembly
            ? "libaether_n2system_media.so"
            : assembly == typeof(FreeTypeBindings.FT).Assembly
                ? "libaether_n2system_freetype.so"
                : "libaether_n2system_openal.so";
        // Resolve relative to the extracted host image first, and also support
        // Android's APK linker namespace when libraries are loaded in place.
        string fullPath = Path.Combine(DirectoryPath, library);
        if (File.Exists(fullPath))
            return NativeLibrary.Load(fullPath);
        return NativeLibrary.TryLoad(fullPath, out nint androidLibrary)
            ? androidLibrary
            : NativeLibrary.Load(library);
#else
        if (assembly == typeof(FFmpeg.AutoGen.ffmpeg).Assembly)
        {
            string mediaName = OperatingSystem.IsWindows() ? "aether_n2system_media.dll"
                : OperatingSystem.IsMacOS() ? "libaether_n2system_media.dylib" : "libaether_n2system_media.so";
            return NativeLibrary.Load(Path.Combine(DirectoryPath, mediaName));
        }
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
#endif
    }
}
