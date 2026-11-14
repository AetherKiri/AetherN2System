using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using AetherN2System;
using NitroSharp;
using NitroSharp.Graphics;
using NitroSharp.Input;
using NitroSharp.Media;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Primitives;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

if (args.Length != 3)
    throw new ArgumentException("Expected native library directory, generated MPEG, and silent MPEG.");
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Game.InitializeHostLogging();
NativeDependencies.Initialize();
string native = Path.GetFullPath(args[0]);
typeof(NativeDependencies).GetProperty("DirectoryPath")!.SetValue(null, native);
GameContext.HostAssetsDirectory = native;

string output = Path.Combine(Path.GetTempPath(), "aether-video-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(output, "nss"));
File.WriteAllText(Path.Combine(output, "nss", "boot.nss"), "chapter main { WaitKey(); } function noop() { } function reset_probe() { $probe_keep=7; Reset(); $probe_wrong=1; }", Encoding.UTF8);
File.WriteAllText(Path.Combine(output, "aether-n2system.json"), """
    {"product.name":"Movie fixture","product.displayName":"Movie fixture","startupScript":"boot.nss","designResolution":"800x600","dev.contentRoot":".","dev.scriptRoot":"nss","dev.useUtf8":true}
    """);
File.Copy(args[1], Path.Combine(output, "generated.mpg"));
File.Copy(args[2], Path.Combine(output, "silent.mpg"));
string writable = Path.Combine(output, "writable");
Directory.CreateDirectory(writable);
var profile = (GameProfile)typeof(Exports).GetMethod("ReadProfile", BindingFlags.Static | BindingFlags.NonPublic)!
    .Invoke(null, new object[] { output, writable })!;
profile.WritableDirectory = writable;
using var window = new HostedWindow(new SizeU<ScreenPixel>(800, 600));
string backend = OperatingSystem.IsMacOS() ? "Metal" : OperatingSystem.IsWindows() ? "Direct3D11" : "Vulkan";
using var config = new MemoryStream(Encoding.UTF8.GetBytes(
    $"{{\"graphics.backend\":\"{backend}\",\"graphics.vsync\":false,\"audio.backend\":\"Auto\"}}"));
using var ctx = GameContext.Create(window, Config.Read(config), profile).GetAwaiter().GetResult();
ctx.World.MainProcess.MainThread.Request(NsEntityAction.Stop);
Step();

Run("generated", 30);
Interaction(false);
Interaction(true);
AudioWait();
Run("silent", 11);
PauseAtLastFrame();
Loop();
ResetScene();
Console.WriteLine("Video smoke passed; screenshots: " + output);

Video CreateVideo(string name, string source = "generated", bool loop = false)
{
    var video = new Video(EntityName.Parse(name), ctx.World.MainProcess.MainThread, 11000,
        File.OpenRead(Path.Combine(output, source + ".mpg")), ctx.RenderContext.GraphicsDevice,
        ctx.AudioContext, loop, false);
    video.WithPosition(ctx.RenderContext,
        NsCoordinate.FromValue(ConstantValue.BuiltInConstant(BuiltInConstant.Center)),
        NsCoordinate.FromValue(ConstantValue.BuiltInConstant(BuiltInConstant.Middle)));
    ctx.World.AddEntity(video);
    video.Start();
    return video;
}

void Step()
{
    ctx.Step(1000f / 60);
    window.EndFrame();
}

void Remove(Video video)
{
    ctx.World.DestroyEntity(video);
    Step();
}

void Run(string label, int expectedFrames)
{
    Video video = CreateVideo("probe-" + label, label);
    int duration = ctx.Builtins.GetMediaDuration(EntityQuery.Parse("probe-" + label));
    int expectedDuration = label == "generated" ? 1200 : 440;
    if (Math.Abs(duration - expectedDuration) > 200)
        throw new Exception($"Incorrect video duration for {label}: {duration} ms.");
    var timer = Stopwatch.StartNew();
    bool screenshot = false;
    while (timer.Elapsed.TotalSeconds < 5)
    {
        Step();
        if (!screenshot && video.PresentedFrames >= 5)
        {
            Save(label);
            screenshot = true;
        }
        if (!video.IsPlaying) break;
        System.Threading.Thread.Sleep(8);
    }
    Console.WriteLine($"RESULT {label} frames={video.PresentedFrames} playing={video.IsPlaying} duration_ms={duration} wall={timer.Elapsed.TotalSeconds:F3}");
    if (video.PresentedFrames != expectedFrames || video.IsPlaying)
        throw new Exception("Movie did not present every frame and complete: " + label);
    Remove(video);
}

void Save(string label)
{
    var pixels = new byte[800 * 600 * 4];
    ctx.RenderContext.CopyRgba(pixels);
    using var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(pixels, 800, 600);
    image.SaveAsPng(Path.Combine(output, label + ".png"));
}

void Interaction(bool skip)
{
    var thread = ctx.World.MainProcess.MainThread;
    Video video = CreateVideo("probe-interact");
    var timer = Stopwatch.StartNew();
    while (video.PresentedFrames < 5 && timer.Elapsed.TotalSeconds < 5)
    {
        Step();
        System.Threading.Thread.Sleep(8);
    }
    if (video.PresentedFrames < 5) throw new Exception("Movie did not start.");
    video.Request(NsEntityAction.Pause);
    int pausedFrames = video.PresentedFrames;
    for (int i = 0; i < 20; i++)
    {
        Step();
        System.Threading.Thread.Sleep(8);
    }
    if (pausedFrames != video.PresentedFrames) throw new Exception("Pause did not preserve the frame.");
    video.Request(NsEntityAction.Resume);
    thread.WaitForMedia(EntityQuery.Parse("probe-interact"), skip);
    foreach (Key key in new[] { Key.LeftShift, Key.Escape })
    {
        window.Event(1, (int)key, 0, 0);
        Step();
        window.Event(2, (int)key, 0, 0);
        Step();
        if (!video.IsPlaying) throw new Exception("Unsupported movie skip key: " + key);
    }
    window.Event(4, 3, 100, 100);
    Step();
    window.Event(5, 3, 100, 100);
    Step();
    if (video.IsPlaying == skip) throw new Exception("Incorrect skip policy: " + skip);
    if (ctx.World.CurrentProcess != ctx.World.MainProcess) throw new Exception("Movie input opened the menu.");
    Console.WriteLine($"RESULT skip={skip} playing={video.IsPlaying} pausedframes={pausedFrames}");
    video.Request(NsEntityAction.Stop);
    thread.AdvanceScript(ctx.VM, ctx.Builtins);
    Remove(video);
}

void AudioWait()
{
    var thread = ctx.World.MainProcess.MainThread;
    var audio = new AudioEntity(EntityName.Parse("probe-audio"), thread,
        () => File.OpenRead(Path.Combine(output, "generated.mpg")), ctx.AudioContext,
        NsAudioKind.BackgroundMusic, ctx.VM);
    ctx.World.AddEntity(audio);
    int duration = ctx.Builtins.GetMediaDuration(EntityQuery.Parse("probe-audio"));
    if (Math.Abs(duration - 1200) > 200)
        throw new Exception($"Incorrect audio duration: {duration} ms.");
    audio.SetLooping(true);
    audio.Request(NsEntityAction.Play);
    if (audio.IsPlaybackWaitActive) throw new Exception("Steady audio loop blocks playback waits.");
    audio.SetVolume(0, TimeSpan.FromMilliseconds(300));
    ctx.Builtins.WaitAction(EntityQuery.Parse("probe-audio"), null);
    if (!HasPendingWait(thread)) throw new Exception("WaitAction did not suspend for the fade.");
    if (!audio.IsPlaybackWaitActive) throw new Exception("Audio fade was not waited.");
    for (int i = 0; i < 20; i++) Step();
    if (audio.IsPlaybackWaitActive) throw new Exception("Looping audio fade did not finish.");
    thread.AdvanceScript(ctx.VM, ctx.Builtins);
    if (HasPendingWait(thread)) throw new Exception("WaitAction did not release after the fade.");
    audio.Request(NsEntityAction.Stop);
    audio.SetLooping(false);
    audio.SetVolume(1, TimeSpan.Zero);
    audio.Request(NsEntityAction.Play);
    ctx.Builtins.WaitAction(EntityQuery.Parse("probe-audio"), null);
    if (!HasPendingWait(thread)) throw new Exception("WaitAction did not suspend for finite audio.");
    var timeoutThread = ctx.World.AddEntity(new NitroSharp.Thread(EntityName.Parse("audio-timeout"), thread,
        ctx.VM.CreateThread("boot", "noop")!.Value, isMain: false));
    timeoutThread.WaitForEntityActions(EntityQuery.Parse("probe-audio"), ctx.Clock.Elapsed + TimeSpan.FromMilliseconds(40));
    var timeoutClock = Stopwatch.StartNew();
    while (!timeoutThread.State.DoneExecuting && timeoutClock.Elapsed.TotalSeconds < 1)
    {
        Step();
        System.Threading.Thread.Sleep(8);
    }
    if (!timeoutThread.State.DoneExecuting || !audio.IsPlaybackWaitActive)
        throw new Exception("WaitAction timeout did not release before finite playback ended.");
    ctx.World.DestroyEntity(timeoutThread);
    var timer = Stopwatch.StartNew();
    while (audio.IsPlaybackWaitActive && timer.Elapsed.TotalSeconds < 5)
    {
        Step();
        System.Threading.Thread.Sleep(8);
    }
    thread.AdvanceScript(ctx.VM, ctx.Builtins);
    if (HasPendingWait(thread)) throw new Exception("WaitAction did not release after finite audio.");
    Console.WriteLine("RESULT audio-wait steady-loop-nonblocking fade300-completed finite-playback-completed timeout40-completed");
    ctx.World.DestroyEntity(audio);
    Step();
}

void Loop()
{
    Video video = CreateVideo("probe-loop", loop: true);
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed.TotalSeconds < 3)
    {
        Step();
        System.Threading.Thread.Sleep(8);
    }
    Console.WriteLine($"RESULT loop frames={video.PresentedFrames} playing={video.IsPlaying}");
    if (video.PresentedFrames < 65 || !video.IsPlaying) throw new Exception("Movie loop did not continue.");
    Remove(video);
}

void PauseAtLastFrame()
{
    Video video = CreateVideo("last-frame-pause", "silent");
    var timer = Stopwatch.StartNew();
    while (video.PresentedFrames < 11 && timer.Elapsed.TotalSeconds < 5)
    {
        Step();
        System.Threading.Thread.Sleep(8);
    }
    if (video.PresentedFrames != 11) throw new Exception("Silent movie did not reach its last frame.");
    video.Request(NsEntityAction.Pause);
    timer.Restart();
    while (timer.Elapsed.TotalSeconds < .15)
    {
        Step();
        System.Threading.Thread.Sleep(8);
    }
    if (!video.IsPlaying || video.PresentedFrames != 11)
        throw new Exception("The paused final frame incorrectly completed playback.");
    video.Request(NsEntityAction.Resume);
    timer.Restart();
    while (video.IsPlaying && timer.Elapsed.TotalSeconds < 2)
    {
        Step();
        System.Threading.Thread.Sleep(8);
    }
    if (video.IsPlaying || video.PresentedFrames != 11)
        throw new Exception("The final frame did not complete after resuming playback.");
    Console.WriteLine("RESULT final-frame-pause retained-until-resume");
    Remove(video);
}

bool HasPendingWait(NitroSharp.Thread thread)
    => typeof(NitroSharp.Thread).GetField("_waitOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(thread) is not null;

void ResetScene()
{
    var original = ctx.World.MainProcess;
    ctx.World.AddEntity(new NitroSharp.Thread(EntityName.Parse("reset-probe"), original.MainThread,
        ctx.VM.CreateThread("boot", "reset_probe")!.Value, isMain: false));
    Step();
    if (ReferenceEquals(original, ctx.World.MainProcess) || !original.IsPendingDestruction)
        throw new Exception("Reset did not replace the old scene after its update.");
    Step();
    int keep = ctx.VM.GlobalsLookup.Variables["probe_keep"];
    int wrong = ctx.VM.GlobalsLookup.Variables["probe_wrong"];
    if (ctx.VM.GetVariable(keep).AsNumber() != 7 || ctx.VM.GetVariable(wrong).AsNumber() == 1)
        throw new Exception("Reset lost globals or continued the old script.");
    Console.WriteLine("RESULT reset new-startup-scene globals-preserved old-script-stopped");
}
