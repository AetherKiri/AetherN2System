using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.Tests;

public sealed class ThreadExecutionTests
{
    [Fact]
    public void SingleFramePressReachesCaseAfterBuiltinDispatch()
    {
        using var script = new Script("""
            chapter main {
                select {
                    case @other { CreateChoice("wrong"); }
                    ExistSave(0);
                    case @target { CreateChoice("clicked"); }
                }
            }
            """);
        script.Advance();
        Assert.Empty(script.Builtins.Choices);
        script.Builtins.Pressed = true;
        script.Advance();
        Assert.Equal(new[] { "clicked" }, script.Builtins.Choices);
        script.Builtins.Pressed = false;
        script.Advance();
        Assert.True(script.Thread.State.DoneExecuting);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void WaitStopsTheCurrentFrameAndBlocksFurtherAdvances(int duration)
    {
        using var script = new Script($"chapter main {{ CreateChoice(\"before\"); Wait({duration}); CreateChoice(\"after\"); }}");
        Assert.Equal(2, script.Advance());
        Assert.Equal(new[] { "before" }, script.Builtins.Choices);
        Assert.Equal(0, script.Advance());
        Assert.Equal(new[] { "before" }, script.Builtins.Choices);
        Assert.False(script.Thread.State.DoneExecuting);
    }

    [Fact]
    public void ExplicitYieldRunsOnePollingLoopPerFrame()
    {
        using var script = new Script("chapter main { select { CreateChoice(\"loop\"); } }");
        script.Advance();
        Assert.Single(script.Builtins.Choices);
        script.Advance();
        Assert.Equal(new[] { "loop", "loop" }, script.Builtins.Choices);
    }

    [Fact]
    public void SliceBudgetBoundsLoopsThatNeverYield()
    {
        using var script = new Script("chapter main { while(true) { CreateChoice(\"loop\"); } }");
        Assert.Equal(7, script.Advance(7));
        Assert.Equal(7, script.Builtins.Choices.Count);
        Assert.False(script.Thread.State.DoneExecuting);
        Assert.Equal(4, script.Advance(4));
        Assert.Equal(11, script.Builtins.Choices.Count);
    }

    [Theory]
    [InlineData(NsEntityAction.Stop, NsEntityAction.Resume)]
    [InlineData(NsEntityAction.Pause, NsEntityAction.Start)]
    public void PauseRequestsStopInsideTheFrameAndResumeAtTheNextInstruction(NsEntityAction pause, NsEntityAction resume)
    {
        using var script = new Script($"chapter main {{ CreateChoice(\"before\"); Request(\"self\", {pause}); CreateChoice(\"after\"); }}");
        script.Advance();
        Assert.Equal(new[] { "before" }, script.Builtins.Choices);
        Assert.Equal(0, script.Advance());
        Assert.True(script.Thread.Request(resume));
        script.Advance();
        Assert.Equal(new[] { "before", "after" }, script.Builtins.Choices);
        Assert.True(script.Thread.State.DoneExecuting);
        Assert.Equal(0, script.Advance());
    }

    [Fact]
    public void DisabledThreadStopsBeforeTheNextBuiltin()
    {
        using var script = new Script("chapter main { CreateChoice(\"before\"); CreateChoice(\"after\"); }");
        script.Builtins.OnChoice = () => script.Thread.Disable();
        script.Advance();
        Assert.Equal(new[] { "before" }, script.Builtins.Choices);
        Assert.Equal(0, script.Advance());
    }

    [Fact]
    public void OtherRequestsStillUseEntityBehavior()
    {
        using var script = new Script("chapter main { }");
        Assert.True(script.Thread.Request(NsEntityAction.Lock));
        Assert.True(script.Thread.IsLocked);
        Assert.True(script.Thread.Request(NsEntityAction.Unlock));
        Assert.False(script.Thread.IsLocked);
    }

    private sealed class Script : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "thread-execution-test-" + Guid.NewGuid().ToString("N"));
        private readonly Stream _globals;
        private readonly NsScriptVM _vm;
        public Thread Thread { get; }
        public ScriptBuiltins Builtins { get; }

        public Script(string source)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "test.nss"), source, Encoding.UTF8);
            var compilation = new Compilation(_root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], _root, "globals");
            Assert.Empty(emitted.Diagnostics);
            _globals = File.OpenRead(Path.Combine(_root, "globals"));
            _vm = new NsScriptVM(new FileSystemNsxModuleLocator(_root, _root, Encoding.UTF8), _globals);
            Thread = new Thread(EntityName.Parse("main"), null, _vm.CreateThread("test", "main")!.Value, isMain: true);
            Thread.Enable();
            Builtins = new ScriptBuiltins(Thread);
        }

        public int Advance(int sliceBudget = global::NitroSharp.Thread.ScriptSlicesPerFrame) => Thread.AdvanceScript(_vm, Builtins, sliceBudget);

        public void Dispose()
        {
            _globals.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ScriptBuiltins(Thread thread) : BuiltInFunctions
    {
        public bool Pressed;
        public List<string> Choices { get; } = [];
        public Action? OnChoice;
        public override bool SaveExists(uint slot) => false;
        public override bool HandleInputEvents(in EntityQuery uiElementPath) => Pressed && uiElementPath.ToString() == "@target";
        public override void CreateChoice(in EntityPath entityPath)
        {
            Choices.Add(entityPath.ToString());
            OnChoice?.Invoke();
        }
        public override void Wait(TimeSpan delay) => thread.Wait(Thread.WaitOperation.Suspend(delay));
        public override void Request(in EntityQuery query, NsEntityAction action) => Assert.True(thread.Request(action));
        public override void SelectEnd() { }
    }
}
