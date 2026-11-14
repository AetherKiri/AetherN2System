using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using NitroSharp.Graphics;
using NitroSharp.Input;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.VM;
using Veldrid;
using Xunit;
using DesignSizeU = NitroSharp.SizeU<NitroSharp.DesignPixel>;

namespace NitroSharp.Tests;

public sealed class FrameSequenceTests
{
    private static SpriteTexture? Load(string path) => path switch
    {
        "red" => SpriteTexture.SolidColor(RgbaFloat.Red, new DesignSizeU(10, 10)),
        "blue" => SpriteTexture.SolidColor(RgbaFloat.Blue, new DesignSizeU(10, 10)),
        "green" => SpriteTexture.SolidColor(RgbaFloat.Green, new DesignSizeU(10, 10)),
        _ => null
    };

    private static FrameSequence Sequence(string[]? frames = null, int ticks = 1, string final = "",
        bool remove = false, bool loop = false, Action<FrameSequence>? completed = null)
        => new(EntityName.Parse("anim8"), new TestContext().MainThread, 208,
            frames ?? ["red", "", "blue"], ticks, final, remove, loop, Load, completed);

    [Fact]
    public void SparseFramesHoldTheirPreviousImageAndCompleteWhenEndIsSelected()
    {
        using var sequence = Sequence();
        Assert.Equal(RgbaFloat.Red, sequence.CurrentTexture.Color);
        sequence.Advance(16);
        sequence.Advance(16);
        Assert.Equal(0, sequence.FrameIndex);
        sequence.Advance(1);
        Assert.Equal(1, sequence.FrameIndex);
        Assert.Equal(RgbaFloat.Red, sequence.CurrentTexture.Color);
        Assert.False(sequence.IsComplete);
        sequence.Advance(33);
        Assert.Equal(2, sequence.FrameIndex);
        Assert.Equal(RgbaFloat.Blue, sequence.CurrentTexture.Color);
        Assert.True(sequence.IsComplete);
        Assert.False(sequence.IsAnimationActive(AnimationKind.FrameSequence));
    }

    [Fact]
    public void WaitTicksAccumulateElapsedTimeAndTheFirstEngineUpdateKeepsStart()
    {
        using var sequence = Sequence(ticks: 3);
        sequence.AdvanceFrame(1000);
        Assert.Equal(0, sequence.FrameIndex);
        sequence.AdvanceFrame(98);
        Assert.Equal(0, sequence.FrameIndex);
        sequence.AdvanceFrame(1);
        Assert.Equal(1, sequence.FrameIndex);
        sequence.AdvanceFrame(99);
        Assert.True(sequence.IsComplete);
    }

    [Fact]
    public void AsyncAssetPreloadingDoesNotConsumeTheFrameTimeline()
    {
        bool ready = false;
        using var sequence = new FrameSequence(EntityName.Parse("anim8"), new TestContext().MainThread, 208,
            ["red", "blue"], 1, "", false, false, Load, ready: () => ready);
        sequence.AdvanceFrame(1000);
        Assert.Equal(0, sequence.FrameIndex);
        ready = true;
        sequence.AdvanceFrame(1000);
        Assert.Equal(0, sequence.FrameIndex);
        sequence.AdvanceFrame(33);
        Assert.True(sequence.IsComplete);
    }

    [Fact]
    public void SingleFrameWaitsOneIntervalAndLargeDeltasPreserveTheAuthoredSchedule()
    {
        using var single = Sequence(["red"]);
        single.Advance(32);
        Assert.False(single.IsComplete);
        single.Advance(1);
        Assert.True(single.IsComplete);
        using var sequence = Sequence(["red", "blue", "green", "red"]);
        sequence.Advance(100);
        Assert.Equal(3, sequence.FrameIndex);
        Assert.True(sequence.IsComplete);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("green", false)]
    [InlineData("", true)]
    public void CompletionHoldsEndUsesTheSpecifiedStillOrRequestsRemoval(string final, bool remove)
    {
        int completions = 0;
        bool removed = false;
        using var sequence = Sequence(final: final, remove: remove, completed: item =>
        {
            completions++;
            removed = item.RemoveOnCompletion;
        });
        sequence.Advance(66);
        Assert.True(sequence.IsComplete);
        Assert.Equal(final == "green" ? RgbaFloat.Green : RgbaFloat.Blue, sequence.CurrentTexture.Color);
        Assert.Equal(remove, removed);
        sequence.Advance(1000);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void LoopKeepsWaitingUntilStopAndStopRetainsTheCurrentImage()
    {
        int completed = 0;
        using var sequence = Sequence(["red", "blue", "green"], loop: true,
            completed: _ => completed++);
        sequence.Advance(33 * 1000001.0);
        Assert.Equal(2, sequence.FrameIndex);
        Assert.Equal(RgbaFloat.Green, sequence.CurrentTexture.Color);
        Assert.True(sequence.IsAnimationActive(AnimationKind.FrameSequence));
        Assert.True(sequence.Request(NsEntityAction.Stop));
        Assert.True(sequence.IsComplete);
        Assert.Equal(RgbaFloat.Green, sequence.CurrentTexture.Color);
        Assert.Equal(1, completed);
        Assert.True(sequence.Request(NsEntityAction.Start));
        sequence.AdvanceFrame(1000);
        Assert.Equal(2, sequence.FrameIndex);
        sequence.AdvanceFrame(33);
        Assert.Equal(0, sequence.FrameIndex);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("green", false)]
    [InlineData("", true)]
    public void StopAppliesTheSameStillFinalizationAsNaturalCompletion(string final, bool remove)
    {
        int completed = 0;
        bool removed = false;
        using var sequence = Sequence(["red", "blue"], final: final, remove: remove, loop: true,
            completed: item => { completed++; removed = item.RemoveOnCompletion; });
        sequence.Advance(33);
        Assert.True(sequence.Request(NsEntityAction.Stop));
        Assert.Equal(final == "green" ? RgbaFloat.Green : RgbaFloat.Blue, sequence.CurrentTexture.Color);
        Assert.True(sequence.IsComplete);
        Assert.Equal(remove, removed);
        sequence.Request(NsEntityAction.Stop);
        Assert.Equal(1, completed);
    }

    [Fact]
    public void RepeatedAndSparsePathsOwnEachLoadedResourceOnce()
    {
        int loads = 0, releases = 0;
        var sequence = new FrameSequence(EntityName.Parse("anim8"), new TestContext().MainThread, 208,
            ["red", "missing", "red", "blue"], 1, "blue", false, false,
            path => { loads++; return Load(path); }, release: texture => { releases++; texture.Dispose(); });
        Assert.Equal(3, loads);
        sequence.Dispose();
        sequence.Dispose();
        Assert.Equal(2, releases);
        Assert.False(sequence.IsAnimationActive(AnimationKind.FrameSequence));
        Assert.Throws<FileNotFoundException>(() => Sequence(["missing", "red"]));
    }

    [Fact]
    public void SynchronousScriptWaitsForSequenceTicksAndReusesTheSameSlotSerially()
    {
        using var script = new Script("""
            chapter main {
                Array($frames, "red", "", "blue");
                CreateFrameAnimation("anim8", 208, 200, 0, $frames, 1, "green", false, false, false);
                CreateChoice("se1");
                WaitFrameAnimation("anim8");
                CreateChoice("first-complete");
                CreateFrameAnimation("anim8", 208, -200, 0, $frames, 1, "", false, false, true);
                CreateChoice("second-complete");
            }
            """);
        script.Advance();
        Assert.Equal(new[] { "se1" }, script.Builtins.Markers);
        FrameSequence first = Assert.Single(script.Builtins.Sequences);
        Assert.Equal(new Vector3(200, 0, 0), first.Transform.Position);
        first.Advance(33);
        Assert.Equal(0, script.Advance());
        first.Advance(33);
        script.Advance();
        Assert.Equal(new[] { "se1", "first-complete" }, script.Builtins.Markers);
        Assert.True(first.IsPendingDestruction);
        FrameSequence second = script.Builtins.Sequences[1];
        Assert.Same(second, script.World.Get(EntityPath.Parse("anim8")));
        Assert.Equal(new Vector3(-200, 0, 0), second.Transform.Position);
        second.Advance(66);
        script.Advance();
        Assert.Equal(new[] { "se1", "first-complete", "second-complete" }, script.Builtins.Markers);
        Assert.True(script.Thread.State.DoneExecuting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopWaitReleasesWhenTheOriginalSlotIsDeletedOrReplaced(bool replace)
    {
        using var script = new Script("""
            chapter main {
                Array($frames, "red", "blue");
                CreateFrameAnimation("anim8", 208, 0, 0, $frames, 1, "", false, true, true);
                CreateChoice("complete");
                WaitFrameAnimation("missing");
                CreateChoice("no-deadlock");
            }
            """);
        script.Advance();
        FrameSequence first = Assert.Single(script.Builtins.Sequences);
        first.Advance(10000);
        Assert.Equal(0, script.Advance());
        if (replace)
            script.World.AddEntity(new FrameSequence(first.Name, script.Thread, 208,
                ["blue", "red"], 1, "", false, true, Load));
        else script.World.DestroyEntity(first);
        script.Advance();
        Assert.Equal(new[] { "complete", "no-deadlock" }, script.Builtins.Markers);
        Assert.True(script.Thread.State.DoneExecuting);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AuthoredAdvanceInputsInterruptSynchronousSequencesAndApplyStill(int inputKind)
    {
        using var script = new Script("""
            chapter main {
                Array($frames, "red", "blue");
                CreateFrameAnimation("anim8", 208, 0, 0, $frames, 1, "green", false, false, true);
                CreateChoice("interrupted");
            }
            """);
        script.Advance();
        Assert.Equal(0, script.Advance());
        Assert.True(script.PressInterrupt(inputKind));
        script.Advance();
        Assert.Equal("interrupted", Assert.Single(script.Builtins.Markers));
        Assert.Equal(RgbaFloat.Green, Assert.Single(script.Builtins.Sequences).CurrentTexture.Color);
        Assert.True(script.Thread.State.DoneExecuting);
    }

    [Fact]
    public void EscapeDoesNotInterruptASequenceWait()
    {
        using var script = new Script("""
            chapter main {
                Array($frames, "red", "blue");
                CreateFrameAnimation("anim8", 208, 0, 0, $frames, 1, "green", false, true, true);
                CreateChoice("complete");
            }
            """);
        script.Advance();
        Assert.False(script.PressInterrupt(5));
        Assert.Equal(0, script.Advance());
        Assert.Empty(script.Builtins.Markers);
    }

    private sealed class Script : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "frame-sequence-test-" + Guid.NewGuid().ToString("N"));
        private readonly Stream _globals;
        private readonly NsScriptVM _vm;
        private readonly HostedWindow _window = new(new SizeU<ScreenPixel>(320, 180));
        private readonly InputContext _input;
        public Thread Thread { get; }
        public World World { get; }
        public SequenceBuiltins Builtins { get; }

        public Script(string source)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "test.nss"), source, Encoding.UTF8);
            var compilation = new Compilation(_root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], _root, "globals");
            Assert.Empty(emitted.Diagnostics);
            _globals = File.OpenRead(Path.Combine(_root, "globals"));
            _vm = new NsScriptVM(new FileSystemNsxModuleLocator(_root, _root, Encoding.UTF8), _globals);
            _input = new InputContext(_window);
            var context = new TestContext();
            Thread = new Thread(EntityName.Parse("sequence-test"), context.MainProcess,
                _vm.CreateThread("test", "main")!.Value, isMain: true);
            World = new World(context.MainProcess, Thread);
            World.BeginFrame();
            Builtins = new SequenceBuiltins(World, Thread);
        }

        public int Advance() => Thread.AdvanceScript(_vm, Builtins);
        public bool PressInterrupt(int kind)
        {
            if (kind is 2 or 3) _window.Event(4, kind == 2 ? 1 : 3, 0, 0);
            else _window.Event(1, (int)(kind switch
            {
                0 => Key.Return,
                1 => Key.Space,
                4 => Key.LeftShift,
                _ => Key.Escape
            }), 0, 0);
            _input.Update(_window, _vm.SystemVariables);
            return Thread.InterruptFrameSequenceWait(_input);
        }
        public void Dispose()
        {
            _input.Dispose();
            _window.Dispose();
            World.BeginFrame();
            World.DisposeAll();
            _globals.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class SequenceBuiltins(World world, Thread thread) : BuiltInFunctions
    {
        public List<FrameSequence> Sequences { get; } = [];
        public List<string> Markers { get; } = [];
        public override void CreateChoice(in EntityPath path) => Markers.Add(path.ToString());
        public override void WaitFrameAnimation(in EntityQuery query) => thread.WaitForFrameSequence(query);
        public override void CreateFrameAnimation(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y,
            ReadOnlySpan<string> frames, int ticksPerFrame, string finalFrame, bool removeOnCompletion, bool loop, bool wait)
        {
            Assert.True(world.TryResolvePath(path, out EntityName name, out Entity? parent));
            var sequence = world.AddEntity(new FrameSequence(name, parent, priority, frames, ticksPerFrame,
                finalFrame, removeOnCompletion, loop, Load,
                completed => { if (completed.RemoveOnCompletion) world.DestroyEntity(completed); }));
            sequence.Transform.Position = new Vector3(x.Value.pos, y.Value.pos, 0);
            Sequences.Add(sequence);
            if (wait) thread.WaitForFrameSequence(EntityQuery.Parse(path.ToString()));
        }
    }
}
