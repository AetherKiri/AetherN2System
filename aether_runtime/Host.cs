using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NitroSharp;
using NitroSharp.Graphics;
using Veldrid;
using ScreenSizeU = NitroSharp.SizeU<NitroSharp.ScreenPixel>;

namespace AetherN2System;

internal sealed class Instance : IDisposable
{
    public required GameContext Context;
    public required HostedWindow Window;
    public required byte[] Pixels;
    public required GCHandle Pin;
    public bool Paused;
    public bool Failed;
    public ulong Sequence;
    public nint Error;

    public void SetError(Exception exception, bool fatal)
    {
        if (Error != 0)
            Marshal.FreeCoTaskMem(Error);
        Error = Marshal.StringToCoTaskMemUTF8(exception.ToString());
        Failed |= fatal;
    }

    public void Dispose()
    {
        try
        {
            Context.Dispose();
        }
        finally
        {
            Window.Dispose();
            if (Pin.IsAllocated)
                Pin.Free();
            if (Error != 0)
                Marshal.FreeCoTaskMem(Error);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Frame
{
    public byte* Pixels;
    public uint Width,
        Height,
        Stride;
    public ulong Sequence;
}

internal static unsafe class Exports
{
    private static readonly ConcurrentDictionary<nint, Instance> Instances = [];
    private static long s_next;

    [ThreadStatic]
    private static nint s_error;
    private static readonly object InitializationLock = new();
    private static bool s_initialized;

    private static void GlobalError(Exception e)
    {
        if (s_error != 0)
            Marshal.FreeCoTaskMem(s_error);
        s_error = Marshal.StringToCoTaskMemUTF8(e.ToString());
    }

    [UnmanagedCallersOnly(
        EntryPoint = "aether_n2system_abi_version",
        CallConvs = [typeof(CallConvCdecl)]
    )]
    public static uint Version() => 1;

    [UnmanagedCallersOnly(
        EntryPoint = "aether_n2system_create",
        CallConvs = [typeof(CallConvCdecl)]
    )]
    public static int Create(
        byte* rootPtr,
        byte* writablePtr,
        uint width,
        uint height,
        nint* output
    )
    {
        if (output == null)
            return -1;
        *output = 0;
        GameContext? context = null;
        HostedWindow? window = null;
        try
        {
            string root = Path.GetFullPath(
                Marshal.PtrToStringUTF8((nint)rootPtr) ?? throw new ArgumentNullException("root")
            );
            string writable = Path.GetFullPath(
                Marshal.PtrToStringUTF8((nint)writablePtr)
                    ?? Path.Combine(Path.GetTempPath(), "AetherN2System")
            );
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(root);
            Directory.CreateDirectory(writable);
            lock (InitializationLock)
                if (!s_initialized)
                {
                    Game.InitializeHostLogging();
                    NativeDependencies.Initialize(writable);
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    s_initialized = true;
                }
            GameProfile profile = ReadProfile(root, writable);
            profile.WritableDirectory = writable;
            profile.SaveDataDirectory = SaveStorageLocation.Select(root, writable, profile.ProductName, profile.Name);
            ScreenSizeU size =
                width == 0 && height == 0
                    ? profile.DesignResolution.Convert(Scale<DesignPixel, ScreenPixel>.Identity)
                    : new ScreenSizeU(width, height);
            if (size.Width == 0 || size.Height == 0 || size.Width > 8192 || size.Height > 8192)
                throw new ArgumentOutOfRangeException("resolution");
            string backend =
#if IOS
                "Metal";
#else
                OperatingSystem.IsMacOS() ? "Metal"
                : OperatingSystem.IsWindows() ? "D3D11"
                : "Vulkan";
#endif
            using var configStream = new MemoryStream(
                Encoding.UTF8.GetBytes(
                    "{\"graphics.backend\":\""
                        + backend
                        + "\",\"graphics.vsync\":false,\"audio.backend\":\"Auto\"}"
                )
            );
            Config config = Config.Read(configStream);
            window = new HostedWindow(size);
            // The native create ABI is synchronous and runs without a managed UI context.
#pragma warning disable VSTHRD002
            context = GameContext.Create(window, config, profile).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            byte[] pixels = new byte[checked((int)(size.Width * size.Height * 4))];
            var instance = new Instance
            {
                Context = context,
                Window = window,
                Pixels = pixels,
                Pin = GCHandle.Alloc(pixels, GCHandleType.Pinned),
            };
            nint id = (nint)System.Threading.Interlocked.Increment(ref s_next);
            if (!Instances.TryAdd(id, instance))
                throw new InvalidOperationException("Could not register N2 instance.");
            *output = id;
            return 0;
        }
        catch (Exception e)
        {
            try
            {
                context?.Dispose();
                window?.Dispose();
            }
            catch { }
            GlobalError(e);
            return -1;
        }
    }

    private static GameProfile ReadProfile(string root, string writable)
    {
        string? file = FindFile(root, "aether-n2system.json") ?? FindFile(root, "profiles.json");
        JsonObjectBuilder builder = new(file);
        PrepareLegacyScripts(builder, root, writable);
        ApplyKnownInstallDefaults(builder, root);
        builder.Default("startupScript", "boot.nss");
        builder.Default("product.name", Path.GetFileName(root));
        builder.Default(
            "product.displayName",
            builder.String("product.name") ?? Path.GetFileName(root)
        );
        builder.Default("designResolution", "1280x720");
        builder.Default("icons.waitLine", "unused#1");
        builder.Default("icons.waitPage", "unused#1");
        builder.Default("icons.waitAuto", "unused#1");
        builder.Default("icons.backlogVoice", "unused#1");
        builder.Default("dev.contentRoot", ".");
        builder.Default("dev.scriptRoot", "nss");
        builder.ResolveContentRoot(root);
        PreparePackedScripts(builder, root, writable);
        builder.AddAutomaticMounts(builder.EffectiveString("dev.contentRoot") ?? root);
        using MemoryStream stream = new(builder.Bytes());
        GameProfile profile = GameProfile.Read(stream);
        string startup = Path.Combine(
            profile.ContentRoot,
            profile.ScriptRoot,
            profile.SysScripts.Startup
        );
        if (!File.Exists(startup))
            throw new NotSupportedException(
                $"N2 startup script is unavailable: {startup}. Packed/encrypted script archives need extraction or a game-specific profile; automatic commercial-game compatibility is not implemented."
            );
        return profile;
    }

    private static string? FindFile(string directory, string name)
    {
        foreach (string path in Directory.EnumerateFiles(directory))
            if (Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
                return path;
        return null;
    }

    private static void ApplyKnownInstallDefaults(JsonObjectBuilder builder, string root)
    {
        // Use launcher names so renamed game directories get the same defaults.
        // Explicit profile values win.
        if (FindFile(root, "hanachirasu.exe") is not null)
        {
            builder.Default("product.name", "Hanachirasu");
            builder.Default("designResolution", "800x600");
            builder.Default("dev.npaScheme", "hanachirasu");
            return;
        }

        bool main = FindFile(root, "Muramasa_chs.exe") is not null || FindFile(root, "Muramasa.exe") is not null;
        bool extra = FindFile(root, "MuramasaAD_cn.exe") is not null || FindFile(root, "MuramasaAD.exe") is not null;
        if (!main && !extra) return;
        builder.Default("startupScript", "0_boot.nss");
        builder.Default("designResolution", "1024x576");
        builder.Default("dev.npaScheme", extra ? "muramasa-ad" : "muramasa");
        builder.Default("dev.archiveEncoding", main && FindFile(root, "Muramasa_chs.exe") is not null ? "gbk" : "shift-jis");
        builder.Default("dev.scriptEncoding", main && FindFile(root, "Muramasa_chs.exe") is not null ? "gbk" : "shift-jis");
        if (extra && FindFile(root, "MuramasaAD_cn.exe") is not null)
        {
            builder.Default("dev.dialogueEncoding", "gbk");
            builder.Default("dev.dialogueVariables", "TITLE_NOW");
        }
    }

    private static void PrepareLegacyScripts(JsonObjectBuilder builder, string root, string writable)
    {
        if (builder.EffectiveString("startupScript") is not null
            || (FindFile(root, "game_cn.ini") ?? FindFile(root, "game.ini")) is null
            || !IsLegacyPak(FindFile(root, "script.pak"))
            || !IsLegacyPak(FindFile(root, "system.pak"))) return;

        LegacyNpsInstallSettings install = LegacyNpsInstall.Prepare(root, writable);
        builder.Default("startupScript", install.StartupScript);
        builder.Default("dev.scriptRoot", install.ScriptRoot);
        builder.Default("dev.contentRoot", install.ContentRoot);
        builder.Default("dev.scriptEncoding", "utf-8");
        builder.Default("dev.archiveEncoding", "shift-jis");
        builder.Default("designResolution", $"{install.Width}x{install.Height}");
        builder.Default("product.name", install.ProductName);
        builder.Default("product.displayName", install.DisplayName);
        builder.DefaultNode("font.size", System.Text.Json.Nodes.JsonValue.Create(install.FontSize)!);
        builder.DefaultNode("dev.useUtf8", System.Text.Json.Nodes.JsonValue.Create(true)!);
        var mounts = new System.Text.Json.Nodes.JsonArray();
        foreach (MountPoint mount in install.Mounts)
            mounts.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["archive"] = mount.ArchiveName,
                ["mount"] = mount.MountName
            });
        builder.DefaultNode("dev.mounts", mounts);
        foreach (string diagnostic in install.Diagnostics.Take(8))
            Console.Error.WriteLine("[N2 legacy] " + diagnostic);
        if (install.Diagnostics.Count > 8)
            Console.Error.WriteLine($"[N2 legacy] {install.Diagnostics.Count - 8} additional compatibility notes; unsupported commands report their source and line when reached.");
    }

    private static bool IsLegacyPak(string? path)
    {
        if (path is null) return false;
        using var stream = File.OpenRead(path);
        if (stream.Length < 0x108) return false;
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 3) return false;
        stream.Position = 0x104;
        return reader.ReadUInt32() == 100;
    }

    private static void PreparePackedScripts(
        JsonObjectBuilder builder,
        string root,
        string writable
    )
    {
        string content = builder.EffectiveString("dev.contentRoot") ?? root;
        string scriptRoot = builder.EffectiveString("dev.scriptRoot") ?? "nss";
        string startup = builder.EffectiveString("startupScript") ?? "boot.nss";
        if (File.Exists(Path.Combine(content, scriptRoot, startup)))
            return;
        string? archivePath = FindFile(content, "nss.npa");
        if (archivePath is null)
            return;
        using var mapped = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
            archivePath,
            FileMode.Open,
            null,
            0,
            System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read
        );
        using NitroSharp.Content.NpaFile archive = NitroSharp.Content.NpaFile.TryLoad(
            mapped,
            Encoding.GetEncoding(builder.EffectiveString("dev.archiveEncoding") ?? "shift-jis"),
            builder.EffectiveString("dev.npaScheme")
        ) ?? throw new InvalidDataException("nss.npa has an unsupported signature.");
        string cache = Path.Combine(writable, "scripts-" + Guid.NewGuid().ToString("N"), "nss");
        Directory.CreateDirectory(cache);
        long total = 0;
        foreach (string name in archive.FileNames)
        {
            string relative = name.Replace('\\', '/');
            if (relative.StartsWith("nss/", StringComparison.OrdinalIgnoreCase))
                relative = relative[4..];
            string destination = Path.GetFullPath(Path.Combine(cache, relative));
            if (
                !destination.StartsWith(
                    cache + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal
                )
            )
                throw new InvalidDataException("Unsafe path in nss.npa.");
            using Stream source = archive.OpenStream(name);
            total = checked(total + source.Length);
            if (total > 512L * 1024 * 1024)
                throw new InvalidDataException("nss.npa script extraction exceeds 512 MiB.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using FileStream target = File.Create(destination);
            source.CopyTo(target);
        }
        builder.SetEffective("dev.scriptRoot", cache);
        builder.AddAutomaticMounts(content);
    }

    private static int With(nint handle, Action<Instance> action, bool fatal = false)
    {
        try
        {
            if (!Instances.TryGetValue(handle, out Instance? i))
                throw new ArgumentException("Invalid N2 instance.");
            try
            {
                action(i);
                return 0;
            }
            catch (Exception e)
            {
                i.SetError(e, fatal);
                return -1;
            }
        }
        catch (Exception e)
        {
            GlobalError(e);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "aether_n2system_tick", CallConvs = [typeof(CallConvCdecl)])]
    public static int Tick(nint handle, double delta) =>
        With(
            handle,
            i =>
            {
                if (!double.IsFinite(delta) || delta < 0 || delta > 10)
                    throw new ArgumentOutOfRangeException(nameof(delta));
                if (i.Failed)
                    throw new InvalidOperationException("Instance is in a failed state.");
                if (i.Paused || i.Context.ShutdownSignal.IsCancellationRequested)
                    return;
                i.Context.Step((float)(delta * 1000));
                i.Window.EndFrame();
                i.Context.RenderContext.CopyRgba(i.Pixels);
                i.Sequence++;
            },
            fatal: true
        );

    [UnmanagedCallersOnly(
        EntryPoint = "aether_n2system_event",
        CallConvs = [typeof(CallConvCdecl)]
    )]
    public static int Event(nint handle, uint type, int code, float x, float y) =>
        With(handle, i => i.Window.Event(type, code, x, y));

    [UnmanagedCallersOnly(
        EntryPoint = "aether_n2system_pause",
        CallConvs = [typeof(CallConvCdecl)]
    )]
    public static int Pause(nint handle, int paused) => With(handle, i =>
        {
            i.Paused = paused != 0;
            if (i.Paused)
                i.Context.Clock.Stop();
            else
                i.Context.Clock.Start();
            if (i.Context.AudioContext.Device is NitroSharp.Media.OpenALAudioDevice device)
                device.PauseAll(i.Paused);
        });

    [UnmanagedCallersOnly(
        EntryPoint = "aether_n2system_frame",
        CallConvs = [typeof(CallConvCdecl)]
    )]
    public static int GetFrame(nint handle, Frame* output)
    {
        try
        {
            if (output == null || !Instances.TryGetValue(handle, out Instance? i))
                return -1;
            *output = new Frame
            {
                Pixels = (byte*)i.Pin.AddrOfPinnedObject(),
                Width = i.Window.Size.Width,
                Height = i.Window.Size.Height,
                Stride = i.Window.Size.Width * 4,
                Sequence = i.Sequence,
            };
            return 0;
        }
        catch (Exception e)
        {
            GlobalError(e);
            return -1;
        }
    }

    [UnmanagedCallersOnly(
        EntryPoint = "aether_n2system_status",
        CallConvs = [typeof(CallConvCdecl)]
    )]
    public static int Status(nint handle)
    {
        try
        {
            return !Instances.TryGetValue(handle, out Instance? i) || i.Failed ? -1
                : i.Context.ShutdownSignal.IsCancellationRequested ? 2
                : i.Paused ? 1
                : 0;
        }
        catch (Exception e)
        {
            GlobalError(e);
            return -1;
        }
    }

    [UnmanagedCallersOnly(
        EntryPoint = "aether_n2system_last_error",
        CallConvs = [typeof(CallConvCdecl)]
    )]
    public static byte* Error(nint handle)
    {
        try
        {
            return (byte*)(Instances.TryGetValue(handle, out Instance? i) ? i.Error : s_error);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(
        EntryPoint = "aether_n2system_destroy",
        CallConvs = [typeof(CallConvCdecl)]
    )]
    public static void Destroy(nint handle)
    {
        try
        {
            if (Instances.TryRemove(handle, out Instance? i))
                i.Dispose();
        }
        catch (Exception e)
        {
            GlobalError(e);
        }
    }
}

// JsonNode uses the JSON DOM only; no reflection-based serializer is required.
internal sealed class JsonObjectBuilder
{
    private readonly System.Text.Json.Nodes.JsonObject _root;

    public JsonObjectBuilder(string? file) =>
        _root = file is null
            ? new()
            : System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();

    public void Default(string key, string value)
    {
        if (!_root.ContainsKey(key))
            _root[key] = value;
    }

    public string? String(string key) => _root[key]?.GetValue<string>();

    public void DefaultNode(string key, System.Text.Json.Nodes.JsonNode value)
    {
        if (!_root.ContainsKey(key)) _root[key] = value;
    }

    public void ResolveContentRoot(string root)
    {
        _root["dev.contentRoot"] = Path.GetFullPath(
            Path.Combine(root, String("dev.contentRoot") ?? ".")
        );
        string? active = String("activeProfile");
        if (
            active is not null
            && _root["profiles"]?[active] is System.Text.Json.Nodes.JsonObject profile
            && profile["dev.contentRoot"] is { } path
        )
            profile["dev.contentRoot"] = Path.GetFullPath(
                Path.Combine(root, path.GetValue<string>())
            );
    }

    private System.Text.Json.Nodes.JsonObject? Active =>
        String("activeProfile") is { } name
            ? _root["profiles"]?[name] as System.Text.Json.Nodes.JsonObject
            : null;

    public string? EffectiveString(string key) => Active?[key]?.GetValue<string>() ?? String(key);

    public void SetEffective(string key, string value)
    {
        if (Active is { } active)
            active[key] = value;
        else
            _root[key] = value;
    }

    public void AddAutomaticMounts(string content)
    {
        var mounts = _root["dev.mounts"] as System.Text.Json.Nodes.JsonArray
            ?? Active?["dev.mounts"] as System.Text.Json.Nodes.JsonArray
            ?? new System.Text.Json.Nodes.JsonArray();
        _root["dev.mounts"] = mounts;
        var mountedArchives = mounts
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(mount => mount["archive"]?.GetValue<string>())
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(content)
            .Where(p => Path.GetExtension(p).Equals(".npa", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => Path.GetFileNameWithoutExtension(p).EndsWith("-patch", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(path).Equals("nss.npa", StringComparison.OrdinalIgnoreCase))
                continue;
            string archiveName = Path.GetFileName(path);
            if (!mountedArchives.Add(archiveName))
                continue;
            string stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            string overlay = MountName(stem);
            AddMount(overlay);
            // Split packs are overlays, but scripts can also address the numbered pack directly.
            if (stem is "cg2" or "sound2" or "voice2") AddMount(stem);

            void AddMount(string name) => mounts.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["mount"] = name,
                ["archive"] = archiveName,
            });
        }
        _root["dev.mounts"] = mounts;
    }

    private static string MountName(string name)
    {
        string normalized = name.ToLowerInvariant();
        if (normalized.EndsWith("-patch")) normalized = normalized[..^6];
        // Only combine known split resource packs; arbitrary digit-ending
        // mounts in other games keep their authored name.
        if (normalized is "cg2" or "sound2" or "voice2") normalized = normalized[..^1];
        return normalized;
    }

    public byte[] Bytes() => Encoding.UTF8.GetBytes(_root.ToJsonString());
}
