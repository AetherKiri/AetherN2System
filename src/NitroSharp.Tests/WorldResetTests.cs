using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using NitroSharp.Text;
using Xunit;

namespace NitroSharp.Tests;

public sealed class WorldResetTests
{
    [Fact]
    public void ResetEndsTheOldScriptAndDefersSceneDisposalWhilePreservingGlobals()
    {
        using var script = new Scripts();
        Process original = script.World.MainProcess;
        var asset = script.World.AddEntity(new TrackedEntity(original.MainThread));
        asset.SetAlias(EntityAlias.Parse("old-asset"));
        script.World.BeginFrame();
        original.MainThread.AdvanceScript(script.Vm, script.Builtins);
        Assert.Empty(script.Builtins.Markers);
        Assert.False(asset.Disposed);
        Assert.Same(original, script.World.MainProcess);
        Assert.True(script.World.ApplyPendingReset());
        Assert.True(original.IsPendingDestruction);
        Assert.False(asset.Disposed);
        Assert.NotSame(original, script.World.MainProcess);
        Assert.Same(script.World.MainProcess, script.World.CurrentProcess);
        Assert.Empty(script.World.Query(EntityQuery.Parse("@old-asset")).AsReadOnlySpan().ToArray());
        Assert.False(script.World.ApplyPendingReset());
        script.World.BeginFrame();
        Assert.True(asset.Disposed);
        script.World.MainProcess.MainThread.AdvanceScript(script.Vm, script.Builtins);
        Assert.Equal(new[] { "startup", "globals-kept" }, script.Builtins.Markers);
    }

    [Fact]
    public void ResetFromAModalMenuRestartsTheSceneInsteadOfReturningToTheCaller()
    {
        using var script = new Scripts();
        Process original = script.World.MainProcess;
        FontSettings callerFont = original.FontSettings;
        Assert.True(script.World.BeginSystemMenu(script.Vm.CreateThread("menu", "main")!.Value));
        Process modal = script.World.CurrentProcess;
        modal.FontSettings = modal.FontSettings with { DefaultFontSize = new PtFontSize(72) };
        modal.MainThread.AdvanceScript(script.Vm, script.Builtins);
        Assert.True(script.World.ApplyPendingReset());
        Assert.NotSame(original, script.World.MainProcess);
        Assert.NotSame(modal, script.World.CurrentProcess);
        Assert.True(original.IsPendingDestruction);
        Assert.True(modal.IsPendingDestruction);
        Assert.Equal(callerFont, script.World.MainProcess.FontSettings);
        Assert.False(script.World.FinishSystemMenu());
        script.World.BeginFrame();
        script.World.MainProcess.MainThread.AdvanceScript(script.Vm, script.Builtins);
        Assert.Equal(new[] { "startup" }, script.Builtins.Markers);
    }

    private sealed class TrackedEntity(Entity parent) : Entity(EntityName.Parse("old-asset"), parent)
    {
        public bool Disposed;
        public override void Dispose() => Disposed = true;
    }

    private sealed class Scripts : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "world-reset-test-" + Guid.NewGuid().ToString("N"));
        private readonly Stream _globals;
        public NsScriptVM Vm { get; }
        public World World { get; }
        public ResetBuiltins Builtins { get; }

        public Scripts()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "story.nss"), "chapter main { $keep=7; Reset(); CreateChoice(\"wrong-old-scene\"); }", Encoding.UTF8);
            File.WriteAllText(Path.Combine(_root, "menu.nss"), "chapter main { Reset(); CreateChoice(\"wrong-old-menu\"); }", Encoding.UTF8);
            File.WriteAllText(Path.Combine(_root, "startup.nss"), "chapter main { CreateChoice(\"startup\"); if($keep==7) { CreateChoice(\"globals-kept\"); } }", Encoding.UTF8);
            var compilation = new Compilation(_root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("story.nss"), compilation.GetSourceModule("menu.nss"), compilation.GetSourceModule("startup.nss")], _root, "globals");
            Assert.Empty(emitted.Diagnostics);
            _globals = File.OpenRead(Path.Combine(_root, "globals"));
            Vm = new NsScriptVM(new FileSystemNsxModuleLocator(_root, _root, Encoding.UTF8), _globals);
            var process = new Process(EntityName.Parse("test"), null, new FontSettings());
            var thread = new Thread(EntityName.Parse("main"), process, Vm.CreateThread("story", "main")!.Value, isMain: true);
            World = new World(process, thread);
            World.BeginFrame();
            Builtins = new ResetBuiltins(World, Vm);
        }

        public void Dispose()
        {
            World.BeginFrame();
            World.DisposeAll();
            _globals.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ResetBuiltins(World world, NsScriptVM vm) : BuiltInFunctions
    {
        public List<string> Markers { get; } = [];
        public override void CreateChoice(in EntityPath path) => Markers.Add(path.ToString());
        public override void Reset() => world.RequestReset(vm.CreateThread("startup", "main")!.Value);
    }
}
