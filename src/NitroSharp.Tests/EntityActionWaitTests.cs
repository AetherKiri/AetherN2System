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

public sealed class EntityActionWaitTests
{
    [Fact]
    public void IdleTargetsAndMissingQueriesContinueImmediately()
    {
        using var script = new Script();
        script.World.AddEntity(new BlankEntity(EntityName.Parse("target"), script.Main));
        script.Advance();
        Assert.Equal(new[] { "before", "after" }, script.Builtins.Markers);
        using var missing = new Script();
        missing.Advance();
        Assert.Equal(new[] { "before", "after" }, missing.Builtins.Markers);
    }

    [Theory]
    [InlineData((int)AnimationKind.Move)]
    [InlineData((int)AnimationKind.Zoom)]
    [InlineData((int)AnimationKind.Rotate)]
    [InlineData((int)AnimationKind.BezierMove)]
    [InlineData((int)AnimationKind.Transition)]
    [InlineData((int)AnimationKind.Fade)]
    [InlineData((int)AnimationKind.FrameSequence)]
    public void WaitsForEveryActionOnNestedEntities(int kind)
    {
        using var script = new Script();
        var root = script.World.AddEntity(new BlankEntity(EntityName.Parse("target"), script.Main));
        var child = script.World.AddEntity(new AnimatedEntity(root, (AnimationKind)kind));
        script.Advance();
        Assert.Equal(new[] { "before" }, script.Builtins.Markers);
        Assert.Equal(0, script.Advance());
        child.Active = null;
        script.Advance();
        Assert.Equal(new[] { "before", "after" }, script.Builtins.Markers);
    }

    [Fact]
    public void ChildProcessCompletionAndStopReleaseTheWait()
    {
        using var script = new Script();
        Thread child = script.AddChild();
        script.Advance();
        Assert.Equal(new[] { "before" }, script.Builtins.Markers);
        child.AdvanceScript(script.Vm, script.Builtins);
        script.Advance();
        Assert.Equal(new[] { "before", "child", "after" }, script.Builtins.Markers);
        using var stopped = new Script();
        Thread paused = stopped.AddChild();
        stopped.Advance();
        paused.Request(NsEntityAction.Stop);
        stopped.Advance();
        Assert.Equal(new[] { "before", "after" }, stopped.Builtins.Markers);
    }

    [Fact]
    public void ReplacingTheTargetDoesNotExtendTheOriginalWait()
    {
        using var script = new Script();
        var original = script.World.AddEntity(new AnimatedEntity(script.Main, AnimationKind.Fade, "target"));
        script.Advance();
        Assert.Equal(new[] { "before" }, script.Builtins.Markers);
        var replacement = script.World.AddEntity(new AnimatedEntity(script.Main, AnimationKind.Move, "target"));
        Assert.True(original.IsPendingDestruction);
        script.Advance();
        Assert.Equal(new[] { "before", "after" }, script.Builtins.Markers);
        Assert.True(replacement.IsAnimationActive(AnimationKind.Move));
    }

    [Fact]
    public void QueryingTheCallingThreadDoesNotWaitOnItself()
    {
        using var script = new Script("@caller");
        script.Main.SetAlias(EntityAlias.Parse("caller"));
        script.Advance();
        Assert.Equal(new[] { "before", "after" }, script.Builtins.Markers);
    }

    private sealed class AnimatedEntity(Entity parent, AnimationKind kind, string name = "child")
        : Entity(EntityName.Parse(name), parent)
    {
        public AnimationKind? Active = kind;
        public override bool IsAnimationActive(AnimationKind animationKind) => Active == animationKind;
    }

    private sealed class Script : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "action-wait-test-" + Guid.NewGuid().ToString("N"));
        private readonly Stream _globals;
        public NsScriptVM Vm { get; }
        public World World { get; }
        public Thread Main { get; }
        public ScriptBuiltins Builtins { get; }

        public Script(string query = "target")
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "test.nss"),
                "chapter main { CreateChoice(\"before\"); WaitAction(\"" + query + "\", null); CreateChoice(\"after\"); } function child() { CreateChoice(\"child\"); }", Encoding.UTF8);
            var compilation = new Compilation(_root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], _root, "globals");
            Assert.Empty(emitted.Diagnostics);
            _globals = File.OpenRead(Path.Combine(_root, "globals"));
            Vm = new NsScriptVM(new FileSystemNsxModuleLocator(_root, _root, Encoding.UTF8), _globals);
            var process = new Process(EntityName.Parse("test"), null, new FontSettings());
            Main = new Thread(EntityName.Parse("main"), process, Vm.CreateThread("test", "main")!.Value, isMain: true);
            World = new World(process, Main);
            World.BeginFrame();
            Builtins = new ScriptBuiltins(Main);
        }

        public Thread AddChild()
        {
            Thread child = World.AddEntity(new Thread(EntityName.Parse("target"), Main,
                Vm.CreateThread("test", "child")!.Value, isMain: false));
            World.BeginFrame();
            return child;
        }

        public int Advance() => Main.AdvanceScript(Vm, Builtins);
        public void Dispose()
        {
            World.DisposeAll();
            _globals.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ScriptBuiltins(Thread main) : BuiltInFunctions
    {
        public List<string> Markers { get; } = [];
        public override void CreateChoice(in EntityPath path) => Markers.Add(path.ToString());
        public override void WaitAction(in EntityQuery query, TimeSpan? timeout) => main.WaitForEntityActions(query, timeout);
    }
}
