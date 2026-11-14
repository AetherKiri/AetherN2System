using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NitroSharp.Common;
using NitroSharp.Content;
using NitroSharp.Graphics;
using NitroSharp.Graphics.Core;
using NitroSharp.Input;
using NitroSharp.Media;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using NitroSharp.Text;
using NitroSharp.Utilities;
using Veldrid;
using ZeroLog;

[assembly: InternalsVisibleTo("NitroSharp.Tests")]
[assembly: InternalsVisibleTo("aether_n2system")]

namespace NitroSharp;

internal readonly record struct FrameStamp(long FrameId, long StopwatchTicks)
{
    public static FrameStamp Invalid => new(-1, -1);
    public bool IsValid => FrameId >= 0 && StopwatchTicks >= 0;
}

internal sealed class GameContext : IDisposable
{
    public required GameWindow Window { get; init; }
    internal required GameProfile Profile { get; init; }
    internal required RenderContext RenderContext { get; init; }
    internal required ContentManager Content { get; init; }
    internal required GlyphRasterizer GlyphRasterizer { get; init; }
    internal required AudioContext AudioContext { get; init; }
    internal required InputContext InputContext { get; init; }
    internal required NsScriptVM VM { get; init; }
    internal Builtins Builtins { get; private set; }
    internal required World World { get; init; }
    internal Stopwatch Clock { get; } = Stopwatch.StartNew();
    internal required Log RootLog { get; init; }
    internal CancellationTokenSource ShutdownSignal { get; } = new();

    internal float DeltaTime { get; private set; }
    internal FrameStamp FrameStamp { get; private set; }

    public void Run()
    {
        long prevFrameTicks = 0L;
        long frameId = 0L;

        bool needsResize = false;
        bool closeRequested = false;
        Window.Resized += () => needsResize = true;
        Window.CloseRequested += () => closeRequested = true;

        while (!ShutdownSignal.IsCancellationRequested && !closeRequested)
        {
            long currentFrameTicks = Clock.ElapsedTicks;
            float deltaMilliseconds =
                (float)(currentFrameTicks - prevFrameTicks) / Stopwatch.Frequency * 1000.0f;
            prevFrameTicks = currentFrameTicks;

            try
            {
                DeltaTime = deltaMilliseconds;
                FrameStamp = new FrameStamp(frameId++, Clock.ElapsedTicks);
                Tick();
            }
            catch (OperationCanceledException) { }
        }
    }

    private long _hostFrame;
    private long _hostTicks;

    internal void Step(float milliseconds)
    {
        DeltaTime = milliseconds;
        _hostTicks += (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
        FrameStamp = new FrameStamp(_hostFrame++, _hostTicks);
        Tick();
    }

    public void Dispose()
    {
        var failures = new List<Exception>();
        Release(() => ShutdownSignal.Cancel());
        Release(World.DisposeAll);
        Release(InputContext.Dispose);
        Release(() => AudioContext.DisposeAsync().AsTask().GetAwaiter().GetResult());
        Release(Content.Dispose);
        Release(RenderContext.Dispose);
        Release(GlyphRasterizer.Dispose);
        Release(ShutdownSignal.Dispose);
        if (failures.Count > 0)
            throw new AggregateException("N2 instance cleanup failed.", failures);

        void Release(Action release)
        {
            try
            {
                release();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private void Tick()
    {
        InputContext.Update(Window, VM.SystemVariables);
        RenderContext.BeginFrame(FrameStamp);
        World.BeginFrame();
        World.Update(this);
        if (Content.ResolveAssets())
        {
            RenderContext.ResolveGlyphs();
            RenderContext.MainBatch.Clear(RgbaFloat.Black);
            World.Render(this);
        }
        RenderContext.EndFrame();
        RenderContext.Present();
    }

    public static async Task<GameContext> Create(
        GameWindow window,
        Config config,
        GameProfile profile
    )
    {
        Log log = LogManager.GetLogger("root");
        log.Info("**** Start apprication ****");
        log.Info(
            $"""
            Game: {profile.ProductDisplayName}
            Profile: {profile.Name}
            """
        );

        GraphicsDevice? gd = null;
        Swapchain? swapchain = null;
        ContentManager? content = null;
        GlyphRasterizer? glyphs = null;
        InputContext? input = null;
        RenderContext? render = null;
        AudioContext? audio = null;
        try
        {
            // Create resources in ownership order so failed initialization can unwind.
            (glyphs, FontSettings fontSettings) = await LoadFonts(profile);
            (NsScriptVM vm, NsScriptThreadState state) = LoadStartupScript(profile, log);
            audio = InitAudio(config);
            (gd, swapchain) = InitGraphics(window, config);
            content = CreateContentManager(gd, profile);
            input = new InputContext(window);
            render = new RenderContext(
                window,
                config,
                profile,
                gd,
                swapchain,
                content,
                glyphs,
                vm.SystemVariables
            );
            (Process process, Thread thread) = CreateProcess(
                state,
                profile.SysScripts.Startup,
                fontSettings
            );
            var context = new GameContext
            {
                Window = window,
                AudioContext = audio,
                Content = content,
                GlyphRasterizer = glyphs,
                Profile = profile,
                RenderContext = render,
                InputContext = input,
                VM = vm,
                World = new World(process, thread),
                RootLog = log,
            };
            context.Builtins = new Builtins(context);
            log.Info(
                $"Init successful. Graphics backend: {gd.BackendType}; audio backend: {audio.Device.Backend}."
            );
            return context;
        }
        catch
        {
            IgnoreFailure(() => input?.Dispose());
            IgnoreFailure(() => content?.Dispose());
            if (render is not null)
                IgnoreFailure(render.Dispose);
            else
            {
                IgnoreFailure(() => swapchain?.Dispose());
                IgnoreFailure(() => gd?.Dispose());
            }
            IgnoreFailure(() => glyphs?.Dispose());
            if (audio is not null)
            {
                try
                {
                    await audio.DisposeAsync();
                }
                catch { }
            }
            throw;
        }
    }

    private static void IgnoreFailure(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch { }
    }

    internal static string HostAssetsDirectory { get; set; } = AppContext.BaseDirectory;

    private static async Task<(GlyphRasterizer, FontSettings)> LoadFonts(GameProfile gameProfile)
    {
        var glyphRasterizer = new GlyphRasterizer();
        var defaultFont = new FontFaceKey(gameProfile.FontFamily, FontStyle.Regular);
        var defaultFontSettings = new FontSettings
        {
            DefaultFont = defaultFont,
            ItalicFont = null,
            DefaultFontSize = gameProfile.FontSize,
            DefaultTextColor = RgbaFloat.White.ToVector4(),
            DefaultOutlineColor = RgbaFloat.Black.ToVector4(),
            RubyFontSizeMultiplier = 0.4f,
        };

        if (OperatingSystem.IsWindows())
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            await glyphRasterizer.AddFontAsync($@"{winDir}\Fonts\msgothic.ttc");
        }

        if (Directory.Exists(Path.Combine(HostAssetsDirectory, "Fonts")))
        {
            await glyphRasterizer.AddFontsAsync(
                Directory.EnumerateFiles(Path.Combine(HostAssetsDirectory, "Fonts"))
            );
        }

        return (glyphRasterizer, defaultFontSettings);
    }

    private static AudioContext InitAudio(Config config)
    {
        var audioParameters = AudioParameters.Default;
        AudioBackend backend = AudioDevice.GetPlatformDefaultBackend();
        if (
            config.PreferredAudioBackend is { } preferredBackend
            && AudioDevice.IsBackendAvailable(preferredBackend)
        )
        {
            backend = preferredBackend;
        }

        var audioDevice = AudioDevice.Create(backend, audioParameters);
        return new AudioContext(audioDevice);
    }

    private static (GraphicsDevice device, Swapchain? swapchain) InitGraphics(
        GameWindow window,
        Config configuration
    )
    {
        var options = new GraphicsDeviceOptions(
            debug: false,
            swapchainDepthFormat: null,
            configuration.EnableVSync
        );
        options.PreferStandardClipSpaceYDirection = true;
        options.ResourceBindingModel = ResourceBindingModel.Improved;
#if DEBUG
        options.Debug = true;
#endif
        GraphicsBackend backend = configuration.GraphicsBackend;
        if (window is HostedWindow)
        {
            GraphicsDevice offscreenDevice = backend switch
            {
                GraphicsBackend.Metal => GraphicsDevice.CreateMetal(options),
                GraphicsBackend.Direct3D11 => GraphicsDevice.CreateD3D11(options),
                GraphicsBackend.Vulkan => GraphicsDevice.CreateVulkan(options),
                _ => throw new PlatformNotSupportedException(
                    "Embedded graphics requires Metal, D3D11 or Vulkan."
                ),
            };
            return (offscreenDevice, null);
        }
        ScreenSizeU renderResolution = window.Size;
        var swapchainDesc = new SwapchainDescription(
            window.SwapchainSource,
            renderResolution.Width,
            renderResolution.Height,
            options.SwapchainDepthFormat,
            options.SyncToVerticalBlank
        );

        if (backend is GraphicsBackend.OpenGL or GraphicsBackend.OpenGLES)
        {
            var wnd = (Sdl3Window)window;
            GraphicsDevice glDevice =
                backend == GraphicsBackend.OpenGL
                    ? VeldridStartupGL.CreateDefaultOpenGLGraphicsDevice(options, wnd, backend)
                    : GraphicsDevice.CreateOpenGLES(options, swapchainDesc);
            return (glDevice, glDevice.MainSwapchain.NotNull());
        }

        GraphicsDevice device = backend switch
        {
            GraphicsBackend.Direct3D11 => GraphicsDevice.CreateD3D11(options),
            GraphicsBackend.Vulkan => GraphicsDevice.CreateVulkan(options),
            _ => throw new Exception($"Backend {backend} is not currently supported."),
        };

        Swapchain swapchain = device.ResourceFactory.CreateSwapchain(in swapchainDesc);
        return (device, swapchain);
    }

    private static ContentManager CreateContentManager(
        GraphicsDevice device,
        GameProfile gameProfile
    )
    {
        TextureLoader textureLoader = OperatingSystem.IsWindows()
            ? new WicTextureLoader(device)
            : new ImageSharpTextureLoader(device);

        var content = new ContentManager(
            gameProfile.ContentRoot,
            textureLoader,
            gameProfile.MountPoints
        );
        return content;
    }

    private static (NsScriptVM vm, NsScriptThreadState mainThread) LoadStartupScript(
        GameProfile gameProfile,
        Log log
    )
    {
        DirectoryInfo tempDir = gameProfile.WritableDirectory is { } writable
            ? Directory.CreateDirectory(
                Path.Combine(writable, "bytecode-" + Guid.NewGuid().ToString("N"))
            )
            : Directory.CreateTempSubdirectory("NitroSharp");

        const string globalsFileName = "_globals";

        string nssFolder = Path.Combine(gameProfile.ContentRoot, gameProfile.ScriptRoot);
        string bytecodeCacheDir = tempDir.FullName;

        string globalsPath = Path.Combine(bytecodeCacheDir, globalsFileName);
        if (gameProfile.SkipUpToDateCheck || !File.Exists(globalsPath))
        {
            if (!Directory.Exists(bytecodeCacheDir))
            {
                Directory.CreateDirectory(bytecodeCacheDir);
                log.Info("Bytecode cache is empty. Compiling the scripts...");
            }
            else
            {
                log.Info("Bytecode cache is not up-to-date. Recompiling the scripts...");
                foreach (
                    string file in Directory.EnumerateFiles(
                        bytecodeCacheDir,
                        "*.nsx",
                        SearchOption.AllDirectories
                    )
                )
                {
                    File.Delete(file);
                }
            }

            Encoding? sourceEncoding = null;
            if (!gameProfile.DetectEncoding)
            {
                sourceEncoding = gameProfile.UseUtf8 ? Encoding.UTF8 : SourceText.DefaultEncoding;
            }

            SystemScripts sysScripts = gameProfile.SysScripts;
            string[] moduleNames =
            [
                sysScripts.Startup,
                sysScripts.Backlog,
                sysScripts.Menu,
                sysScripts.Load,
                sysScripts.Save,
            ];

            var nssFolderInfo = new DirectoryInfo(nssFolder);
            string[] existingModules = moduleNames
                .Where(x => nssFolderInfo.EnumerateFiles(x).Any())
                .ToArray();
            IEnumerable<string> nonExistingModules = moduleNames.Except(existingModules);

            foreach (string nonExistingModule in nonExistingModules)
            {
                log.Warn($"System module '{nonExistingModule}' is missing");
            }

            TimeSpan compilationTime;
            using (TimingScope.Start(out compilationTime))
            {
                var compilation = new Compilation(nssFolder, sourceEncoding);
                SourceModuleSymbol[] modules = existingModules
                    .Select(compilation.GetSourceModule)
                    .ToArray();

                // ReSharper disable once RedundantAssignment
                compilation = compilation.Emit(modules, bytecodeCacheDir, globalsFileName);
            }
            log.Info($"Compilation finished in {compilationTime.TotalMilliseconds} ms.");
        }
        else
        {
            log.Info("Bytecode cache is up-to-date.");
        }

        var nsxLocator = new FileSystemNsxModuleLocator(bytecodeCacheDir, nssFolder);
        var vm = new NsScriptVM(nsxLocator, File.OpenRead(globalsPath));
        string moduleName = Path.ChangeExtension(gameProfile.SysScripts.Startup, null);
        NsScriptThreadState mainThread = vm.CreateThread(moduleName, "main")!.Value;
        return (vm, mainThread);
    }

    private static (Process process, Thread thread) CreateProcess(
        in NsScriptThreadState threadState,
        string modulePath,
        FontSettings fontSettings
    )
    {
        string moduleName = Path.GetFileNameWithoutExtension(modulePath);
        var process = new Process(EntityName.Parse(moduleName), parent: null, fontSettings);
        var thread = new Thread(
            EntityName.Parse("main"),
            parent: process,
            threadState,
            isMain: true
        );
        return (process, thread);
    }
}
