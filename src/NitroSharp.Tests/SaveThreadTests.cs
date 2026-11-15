using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using MessagePack;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using NitroSharp.Text;
using Xunit;

namespace NitroSharp.Tests;

public sealed class SaveThreadTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void RestoredVmContinuesPartialExpressionAndOutputReferences(int instructions)
    {
        using var script = new Script("""
            chapter main {
                CursorPosition($x, #y);
                assert($x == 12);
                assert(#y == 34);
                helper();
                assert($after == 9);
            }
            function helper() { $after = 9; }
            """);
        var original = script.CreateVm();
        var state = original.CreateThread("test", "main")!.Value;
        var builtins = new TestBuiltins();
        original.InstructionBudget = instructions;
        original.Tick(ref state, builtins);
        byte[] globals = SaveGlobals(original);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true)) original.WriteThreadState(writer, state);
        var restored = script.CreateVm();
        RestoreGlobals(restored, globals);
        bytes.Position = 0;
        using var reader = new BinaryReader(bytes, Encoding.UTF8, leaveOpen: true);
        state = restored.ReadThreadState(reader);
        Assert.Equal(bytes.Length, bytes.Position);
        for (int i = 0; i < 100 && !state.DoneExecuting; i++) restored.Tick(ref state, builtins);
        Assert.True(state.DoneExecuting);
        Assert.Equal(3, builtins.Assertions);
    }

    [Fact]
    public void EveryArrayInstructionCheckpointRestoresCanonicalNestedAndAliasedNodes()
    {
        using var script = new Script("""
            chapter main {
                Array($rows, "first", "second");
                AssocArray($rows, "first", "second");
                Array($rows[first], 10, 20);
                $alias = $rows;
                $child = $rows[first];
                Array($rows, "updated", "second");
                Array($rows[0], 30, 40);
                $child = $rows[0];
                $alias[0][1] += 2;
                $child[0] = 31;
                assert($rows[0][0] == 31);
                assert($rows[0][1] == 42);
                Array($child, 50, 60);
                assert($rows[0][1] == 60);
            }
            """);
        var vm = script.CreateVm();
        var state = vm.CreateThread("test", "main")!.Value;
        var originalBuiltins = new TestBuiltins();
        vm.InstructionBudget = 1;
        int checkpoints = 0;
        while (!state.DoneExecuting && checkpoints++ < 300)
        {
            vm.Tick(ref state, originalBuiltins);
            byte[] globals = SaveGlobals(vm);
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true)) vm.WriteThreadState(writer, state);
            var restored = script.CreateVm();
            RestoreGlobals(restored, globals);
            bytes.Position = 0;
            using var reader = new BinaryReader(bytes, Encoding.UTF8, leaveOpen: true);
            var copy = restored.ReadThreadState(reader);
            var builtins = new TestBuiltins();
            for (int i = 0; i < 300 && !copy.DoneExecuting; i++) restored.Tick(ref copy, builtins);
            Assert.True(copy.DoneExecuting, $"Checkpoint {checkpoints} did not terminate.");
        }
        Assert.True(state.DoneExecuting);
        Assert.Equal(3, originalBuiltins.Assertions);
        Assert.True(checkpoints > 20);
    }

    [Fact]
    public void ScopedSceneRoundtripRetainsTheCallersInheritedFunctions()
    {
        using var script = new Script("""
            chapter main {
                $target = "episode.nss->entry";
                call_scene $target;
                assert($seen == 7);
            }
            function SharedHelper() { $seen = 7; }
            """, new Dictionary<string, string> { ["episode.nss"] = "scene entry { SharedHelper(); }" });
        var vm = script.CreateVm();
        var state = vm.CreateThread("test", "main")!.Value;
        var builtins = new TestBuiltins();
        for (int i = 0; i < 20 && state.CurrentFrame.Module.SourceScopes.Length == 0; i++) vm.Tick(ref state, builtins);
        Assert.NotEmpty(state.CurrentFrame.Module.SourceScopes);
        byte[] globals = SaveGlobals(vm);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true)) vm.WriteThreadState(writer, state);
        var restored = script.CreateVm();
        RestoreGlobals(restored, globals);
        bytes.Position = 0;
        using var reader = new BinaryReader(bytes, Encoding.UTF8, leaveOpen: true);
        state = restored.ReadThreadState(reader);
        for (int i = 0; i < 100 && !state.DoneExecuting; i++) restored.Tick(ref state, builtins);
        Assert.True(state.DoneExecuting);
        Assert.Equal(1, builtins.Assertions);
    }

    [Fact]
    public void VmRoundtripRetainsBoundedFramesScopesAndNamedReferenceSlots()
    {
        using var script = new Script("chapter main { Wait(1); }");
        var vm = script.CreateVm();
        vm.SetHostFlag("dynamic-reference", ConstantValue.Number(42));
        var state = vm.CreateThread("test", "main")!.Value;
        state.CurrentFrame.EndPosition = state.CurrentFrame.ProgramCounter;
        state.CurrentFrame.HasCallScope = true;
        state.SelectResult = true;
        int slot = vm.GlobalsLookup.Flags["dynamic-reference"];
        state.EvalStack.Push(ConstantValue.Number(42).WithSlot((short)(-slot - 2)));
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true)) vm.WriteThreadState(writer, state);
        var restored = script.CreateVm();
        restored.SetHostFlag("different-dynamic-flag", ConstantValue.True);
        bytes.Position = 0;
        using var reader = new BinaryReader(bytes, Encoding.UTF8, leaveOpen: true);
        var copy = restored.ReadThreadState(reader);
        Assert.True(copy.SelectResult);
        Assert.True(copy.EvalStack.Peek().GetSlotInfo(out short restoredSlot));
        Assert.Equal(-restored.GlobalsLookup.Flags["dynamic-reference"] - 2, restoredSlot);
        Assert.Equal(42, copy.EvalStack.Peek().AsNumber());
        var builtins = new TestBuiltins();
        restored.Tick(ref copy, builtins);
        Assert.True(copy.DoneExecuting);
        Assert.Equal(1, builtins.ScopeExits);
    }

    [Fact]
    public void ThreadRoundtripRebindsWaitAndCallScopeReferencesAndPreservesRemainingTime()
    {
        using var script = new Script("chapter main { }");
        var vm = script.CreateVm();
        var process = new Process(EntityName.Parse("process"), null, new FontSettings());
        var thread = new Thread(EntityName.Parse("main"), process, vm.CreateThread("test", "main")!.Value, true);
        var world = new World(process, thread);
        thread.EnterCallScope();
        Entity scope = thread.EntityContext;
        var target = world.AddEntity(new Thread(EntityName.Parse("target"), scope, vm.CreateThread("test", "main")!.Value, false));
        thread.Wait(new Thread.WaitOperation(Thread.WaitCondition.EntityActionCompleted, EntityQuery.Parse("*"), TimeSpan.FromSeconds(12))
        {
            ActionTargets = [target], AllowMovieSkip = true
        });
        thread.Request(NsEntityAction.Pause);
        Entity[] entities = [scope, target];
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
            thread.WriteSave(writer, vm, entity => Array.IndexOf(entities, entity), TimeSpan.FromSeconds(10));
        bytes.Position = 0;
        using var reader = new BinaryReader(bytes, Encoding.UTF8, leaveOpen: true);
        var copy = Thread.ReadSave(EntityName.Parse("copy"), process, reader, vm, TimeSpan.FromSeconds(30), out var resolve);
        resolve(entities);
        Assert.Same(scope, copy.EntityContext);
        Assert.Equal(0, copy.AdvanceScript(vm, new TestBuiltins()));
        using var savedAgain = new MemoryStream();
        using (var writer = new BinaryWriter(savedAgain, Encoding.UTF8, leaveOpen: true))
            copy.WriteSave(writer, vm, entity => Array.IndexOf(entities, entity), TimeSpan.FromSeconds(30));
        Assert.Equal(bytes.ToArray(), savedAgain.ToArray());
        world.DisposeAll();
    }

    [Fact]
    public void RestoreGlobalsClearsValuesCreatedAfterTheSnapshot()
    {
        using var script = new Script("chapter main { }");
        var vm = script.CreateVm();
        byte[] saved = SaveGlobals(vm);
        vm.RestoreVariables(new GlobalsDump { Globals = [("later-variable", ConstantValue.Number(9))] });
        vm.SetHostFlag("later-flag", ConstantValue.True);
        RestoreGlobals(vm, saved);
        Assert.Equal(BuiltInType.Uninitialized, vm.GetHostVariable("later-variable").Type);
        Assert.Equal(BuiltInType.Uninitialized, vm.GetHostFlag("later-flag").Type);
    }

    [Fact]
    public void VmRejectsOffsetsWhenTheSavedSourceChanges()
    {
        using var script = new Script("chapter main { Wait(1); }");
        var vm = script.CreateVm();
        var state = vm.CreateThread("test", "main")!.Value;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true)) vm.WriteThreadState(writer, state);
        script.ReplaceSource("chapter main { Wait(7); }");
        bytes.Position = 0;
        using var reader = new BinaryReader(bytes, Encoding.UTF8, leaveOpen: true);
        Assert.Throws<InvalidDataException>(() => script.CreateVm().ReadThreadState(reader));
    }

    [Fact]
    public void RestoreGlobalsCreatesDynamicEntriesInsteadOfDroppingThem()
    {
        using var script = new Script("chapter main { }");
        var original = script.CreateVm();
        original.SetHostFlag("SYSTEM_save_path", ConstantValue.String("/save/0042/"));
        byte[] bytes = SaveGlobals(original);
        var restored = script.CreateVm();
        RestoreGlobals(restored, bytes);
        Assert.Equal("/save/0042/", restored.GetHostFlag("SYSTEM_save_path").AsString());
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 4097)]
    public void VmRejectsUnsupportedVersionsAndInvalidStackSizes(int version, int count)
    {
        using var script = new Script("chapter main { }");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true)) { writer.Write(version); writer.Write(count); }
        bytes.Position = 0;
        using var reader = new BinaryReader(bytes);
        Assert.Throws<InvalidDataException>(() => script.CreateVm().ReadThreadState(reader));
    }

    private static byte[] SaveGlobals(NsScriptVM vm)
    {
        var bytes = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bytes);
        vm.DumpVariables().Serialize(ref writer);
        vm.DumpFlags().Serialize(ref writer);
        writer.Flush();
        return bytes.WrittenSpan.ToArray();
    }

    private static void RestoreGlobals(NsScriptVM vm, byte[] bytes)
    {
        var reader = new MessagePackReader(bytes);
        vm.RestoreVariables(new GlobalsDump(ref reader));
        vm.RestoreFlags(new GlobalsDump(ref reader));
    }

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public int Assertions;
        public int ScopeExits;
        public override Vector2 GetCursorPosition() => new(12, 34);
        public override void AssertTrue(bool value) { Assert.True(value); Assertions++; }
        public override void LeaveCallScope() => ScopeExits++;
    }

    private sealed class Script : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "n2-save-thread-" + Guid.NewGuid().ToString("N"));
        public Script(string source, Dictionary<string, string>? additionalSources = null)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "test.nss"), source, Encoding.UTF8);
            foreach ((string name, string text) in additionalSources ?? [])
                File.WriteAllText(Path.Combine(_directory, name), text, Encoding.UTF8);
            var compilation = new Compilation(_directory, Encoding.UTF8);
            Assert.Empty(compilation.Emit([compilation.GetSourceModule("test.nss")], _directory, "globals").Diagnostics);
        }
        public void ReplaceSource(string source) => File.WriteAllText(Path.Combine(_directory, "test.nss"), source, Encoding.UTF8);
        public NsScriptVM CreateVm() => new(new FileSystemNsxModuleLocator(_directory, _directory, Encoding.UTF8),
            new MemoryStream(File.ReadAllBytes(Path.Combine(_directory, "globals"))));
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
