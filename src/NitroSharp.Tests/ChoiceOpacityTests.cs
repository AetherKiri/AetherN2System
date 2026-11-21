using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Veldrid;
using Xunit;
using DesignSize = NitroSharp.Size<NitroSharp.DesignPixel>;

namespace NitroSharp.Tests;

public sealed class ChoiceOpacityTests
{
    [Theory]
    [InlineData(MouseState.Over)]
    [InlineData(MouseState.Down)]
    public void PreservedChoiceKeepsTransparentHitAndAuthoredFrameOpacity(MouseState state)
    {
        var choice = CreateChoice(preserve: true);
        choice.ShowState(state);
        var group = choice.TryGetChild(EntityName.Parse(state == MouseState.Over ? "MouseOver" : "MouseClick"))!;
        Assert.True(((MouseStateEntity)group).IsVisible);
        Assert.Equal(0, ((RenderItem)group.TryGetChild(EntityName.Parse("hit"))!).Color.A);
        Assert.Equal(.5f, ((RenderItem)group.TryGetChild(EntityName.Parse("frame"))!).Color.A);
        Assert.Equal(.7f, ((RenderItem)group.TryGetChild(EntityName.Parse("text"))!).Color.A);
        Assert.Equal(0, choice.GetHitTarget()!.Color.A);
        Assert.Equal(new DesignSize(440, 55), choice.GetHitTarget()!.GetSize(null!));
    }

    [Fact]
    public void PreservedFocusDoesNotCancelAnAuthoredFade()
    {
        var choice = CreateChoice(preserve: true);
        var group = choice.TryGetChild(EntityName.Parse("MouseOver"))!;
        var frame = (RenderItem)group.TryGetChild(EntityName.Parse("frame"))!;
        frame.Fade(0, TimeSpan.FromMilliseconds(100), NsEaseFunction.Linear);
        choice.ShowState(MouseState.Over);
        Assert.True(frame.IsAnimationActive(AnimationKind.Fade));
        Assert.Equal(.5f, frame.Color.A);
    }

    [Fact]
    public void DefaultNssChoiceRetainsItsExistingOpacityReset()
    {
        var choice = CreateChoice(preserve: false);
        choice.ShowState(MouseState.Over);
        var group = choice.TryGetChild(EntityName.Parse("MouseOver"))!;
        Assert.False(choice.PreserveOpacity);
        foreach (Entity child in group.Children)
            Assert.Equal(1, ((RenderItem)child).Color.A);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(",false", false)]
    [InlineData(",true", true)]
    public void DispatcherKeepsTheOptionalFlagAndLegacyOverrideCompatibility(string argument, bool expected)
    {
        using var script = new Script("chapter main { CreateChoice(\"choice\"" + argument + "); }");
        var recording = new RecordingBuiltins();
        script.Run(recording);
        Assert.Equal(("choice", expected), recording.Choice);
        using var legacyScript = new Script("chapter main { CreateChoice(\"legacy\"); }");
        var legacy = new LegacyBuiltins();
        legacyScript.Run(legacy);
        Assert.Equal("legacy", legacy.Path);
    }

    [Theory]
    [InlineData("UnClickable", NsEntityAction.UseArrowCursor)]
    [InlineData("Clickable", NsEntityAction.UseHandCursor)]
    public void CursorHintsPreserveChoiceHitTesting(string token, NsEntityAction expected)
    {
        using var script = new Script($"chapter main {{ Request(\"choice\", {token}); }}");
        var recording = new RecordingBuiltins();
        script.Run(recording);
        Assert.Equal(expected, recording.Action);
        var choice = CreateChoice(false);
        Assert.True(choice.Request(recording.Action));
        Assert.True(choice.IsClickable);
    }

    private static ChoiceEntity CreateChoice(bool preserve)
    {
        var context = new TestContext();
        var world = new World(context.MainProcess, context.MainThread);
        var choice = world.AddEntity(new ChoiceEntity(EntityName.Parse("choice"), context.MainThread, preserve));
        foreach (string state in new[] { "MouseUsual", "MouseOver", "MouseClick" })
        {
            var group = world.AddEntity(new MouseStateEntity(EntityName.Parse(state), choice));
            foreach ((string name, float opacity) in new[] { ("hit", 0f), ("frame", .5f), ("text", .7f) })
            {
                var visual = world.AddEntity(new SolidColorRect(EntityName.Parse(name), group, 1,
                    RgbaFloat.Black, new DesignSize(440, 55)));
                visual.Fade(opacity, TimeSpan.Zero, NsEaseFunction.Linear);
            }
        }
        return choice;
    }

    private sealed class RecordingBuiltins : BuiltInFunctions
    {
        public (string, bool) Choice;
        public NsEntityAction Action;
        public override void Request(in EntityQuery query, NsEntityAction action) => Action = action;
        public override void CreateChoice(in EntityPath path, bool preserveOpacity = false)
            => Choice = (path.ToString(), preserveOpacity);
    }

    private sealed class LegacyBuiltins : BuiltInFunctions
    {
        public string? Path;
        public override void CreateChoice(in EntityPath path) => Path = path.ToString();
    }

    private sealed class Script : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "choice-opacity-test-" + Guid.NewGuid().ToString("N"));
        private readonly Stream _globals;
        private readonly NsScriptVM _vm;
        private NsScriptThreadState _thread;

        public Script(string source)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "test.nss"), source, Encoding.UTF8);
            var compilation = new Compilation(_root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], _root, "globals");
            Assert.Empty(emitted.Diagnostics);
            _globals = File.OpenRead(Path.Combine(_root, "globals"));
            _vm = new NsScriptVM(new FileSystemNsxModuleLocator(_root, _root, Encoding.UTF8), _globals);
            _thread = _vm.CreateThread("test", "main")!.Value;
        }

        public void Run(BuiltInFunctions builtins)
        {
            for (int i = 0; i < 32 && !_thread.DoneExecuting; i++) _vm.Tick(ref _thread, builtins);
            Assert.True(_thread.DoneExecuting);
        }

        public void Dispose()
        {
            _globals.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
