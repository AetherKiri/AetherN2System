using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using NitroSharp.Input;
using Xunit;

namespace NitroSharp.Tests;

public sealed class SystemMenuTests
{
    [Fact]
    public void ModalMenuUsesItsOwnNamespaceAndReturnsToTheSuspendedStory()
    {
        using var scripts = new Scripts();
        var context = new TestContext();
        var story = new Thread(EntityName.Parse("main"), context.MainProcess,
            scripts.Vm.CreateThread("story", "main")!.Value, isMain: true);
        var world = new World(context.MainProcess, story);
        world.BeginFrame();
        var builtins = new TestBuiltins(world);
        story.AdvanceScript(scripts.Vm, builtins);
        Assert.Equal(new[] { "story-before" }, builtins.Markers);
        var background = world.AddEntity(new BlankEntity(EntityName.Parse("background"), story));
        Assert.True(world.TryBeginSystemMenu(scripts.Vm, "menu"));
        Process modal = world.CurrentProcess;
        var callerFont = world.MainProcess.FontSettings;
        modal.FontSettings = modal.FontSettings with { DefaultFontSize = new NitroSharp.Text.PtFontSize(72) };
        Assert.NotSame(world.MainProcess, modal);
        Assert.Null(world.Get(EntityPath.Parse("background")));
        Assert.False(world.TryBeginSystemMenu(scripts.Vm, "menu"));
        modal.MainThread.AdvanceScript(scripts.Vm, builtins);
        Assert.Equal(new[] { "story-before", "menu" }, builtins.Markers);
        var menuBackground = world.AddEntity(new BlankEntity(EntityName.Parse("background"), modal.MainThread));
        menuBackground.SetAlias(EntityAlias.Parse("menu-background"));
        Assert.True(world.FinishSystemMenu());
        Assert.Same(world.MainProcess, world.CurrentProcess);
        Assert.Equal(callerFont, world.MainProcess.FontSettings);
        Assert.Same(background, world.Get(EntityPath.Parse("background")));
        Assert.True(menuBackground.IsPendingDestruction);
        Assert.False(world.FinishSystemMenu());
        Assert.Equal(0, story.AdvanceScript(scripts.Vm, builtins));
        Assert.Equal(new[] { "story-before", "menu" }, builtins.Markers);
        world.BeginFrame();
        Assert.False(background.IsPendingDestruction);
        world.DisposeAll();
    }

    [Fact]
    public void GamesWithoutASystemMenuRetainTheirCurrentProcess()
    {
        using var scripts = new Scripts();
        var context = new TestContext();
        var world = new World(context.MainProcess, context.MainThread);
        Assert.False(world.TryBeginSystemMenu(scripts.Vm, "missing"));
        Assert.Same(world.MainProcess, world.CurrentProcess);
        world.BeginFrame();
        world.DisposeAll();
    }

    [Fact]
    public void EscapePublishesAPressPulseAfterDynamicMenuGlobalsAreAdded()
    {
        using var scripts = new Scripts();
        Assert.NotNull(scripts.Vm.CreateThread("dynamic-menu", "main"));
        using var window = new HostedWindow(new SizeU<ScreenPixel>(320, 180));
        using var input = new InputContext(window);
        window.Event(1, (int)Key.Escape, 0, 0);
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.True(scripts.Vm.SystemVariables.EscapeKeyDown.AsBool());
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.False(scripts.Vm.SystemVariables.EscapeKeyDown.AsBool());
        window.Event(2, (int)Key.Escape, 0, 0);
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.False(scripts.Vm.SystemVariables.EscapeKeyDown.AsBool());
    }

    private sealed class TestBuiltins(World world) : BuiltInFunctions
    {
        public List<string> Markers { get; } = [];
        public override void CreateChoice(in EntityPath path) => Markers.Add(path.ToString());
        public override void WaitForInput() => world.CurrentProcess.CurrentThread.Wait(Thread.WaitOperation.UserInput(null));
    }

    private sealed class Scripts : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "system-menu-test-" + Guid.NewGuid().ToString("N"));
        private readonly Stream _globals;
        public NsScriptVM Vm { get; }
        public Scripts()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "story.nss"), "chapter main { CreateChoice(\"story-before\"); WaitKey(); CreateChoice(\"story-after\"); }", Encoding.UTF8);
            File.WriteAllText(Path.Combine(_root, "menu.nss"), "chapter main { CreateChoice(\"menu\"); }", Encoding.UTF8);
            File.WriteAllText(Path.Combine(_root, "dynamic-menu.nss"), "chapter main { $SYSTEM_keydown_esc=false; }", Encoding.UTF8);
            var compilation = new Compilation(_root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("story.nss"), compilation.GetSourceModule("menu.nss")], _root, "globals");
            Assert.Empty(emitted.Diagnostics);
            _globals = File.OpenRead(Path.Combine(_root, "globals"));
            Vm = new NsScriptVM(new FileSystemNsxModuleLocator(_root, _root, Encoding.UTF8), _globals);
        }
        public void Dispose()
        {
            _globals.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
