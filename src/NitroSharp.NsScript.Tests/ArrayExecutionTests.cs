using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using MessagePack;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Syntax;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class ArrayExecutionTests
{
    [Fact]
    public void OrderedAndNamedRowsShareChildrenWithoutLosingTheirScalar()
    {
        Execute("""
            chapter main {
                Array($rows, "Start", "Load");
                AssocArray($rows, "Start", "Load");
                Array($rows[Start], 40, 90, true);
                assert($rows[0] == "Start");
                assert("title-" + $rows[0] == "title-Start");
                assert(Count($rows) == 2);
                assert(Count($rows[0]) == 3);
                $i = 0;
                $rows[$i][2] = false;
                assert($rows[Start][2] == false);
                $rows[Start][0] += 5;
                $rows[$i][0]++;
                assert($rows[0][0] == 46);
                $rows[0][0]--;
                assert($rows[Start][0] == 45);
                Array(#names, "one", "two");
                AssocArray(#names, "one", "two");
                Array(#names[one], 42);
                assert(#names[0] == "one");
                assert(#names[0][0] == 42);
                assert(Count(#names) == 2);
            }
            """);
    }

    [Fact]
    public void ArrayDrivenMenuCreatesAllSevenChoices()
    {
        var builtins = Execute("""
            chapter main {
                Array($menu, "resume", "start", "load", "settings", "gallery", "quit", "credits");
                AssocArray($menu, "resume", "start", "load", "settings", "gallery", "quit", "credits");
                Array($menu[start], 40, 20, true, "New game", "start");
                Array($menu[load], 40, 60, true, "Load game", "load");
                Array($menu[settings], 40, 100, true, "Settings", "settings");
                Array($menu[resume], 40, 140, true, "Resume", "resume");
                Array($menu[gallery], 40, 180, true, "Gallery", "gallery");
                Array($menu[quit], 40, 220, true, "Quit", "quit");
                Array($menu[credits], 40, 260, true, "Credits", "credits");
                $index = 0;
                while (Count($menu) > $index) {
                    if ($menu[$index][2]) {
                        CreateChoice("menu-" + $menu[$index]);
                    }
                    $index++;
                }
                assert(Count($menu) == 7);
            }
            """);
        Assert.Equal(new[] { "menu-resume", "menu-start", "menu-load", "menu-settings", "menu-gallery", "menu-quit", "menu-credits" }, builtins.Choices);
    }

    [Fact]
    public void GlobalsRoundtripPreservesNestedChildrenAndNamedAliases()
    {
        Execute("""
            chapter main {
                Array($rows, "first", "second");
                AssocArray($rows, "first", "second");
                Array($rows[first], 10, 20);
                $rows[custom] = "extra";
                Array(#flags, "enabled");
                AssocArray(#flags, "enabled");
                Array(#flags[enabled], true);
            }
            chapter restored {
                assert(Count($rows) == 2);
                assert($rows[0] == "first");
                assert($rows[0][1] == 20);
                assert($rows[custom] == "extra");
                $rows[first][1] += 3;
                assert($rows[0][1] == 23);
                $rows[1] = "updated";
                assert($rows[second] == "updated");
                assert(#flags[0][0] == true);
                #flags[enabled][0] = false;
                assert(#flags[0][0] == false);
            }
            """, roundtripGlobals: true);
    }

    [Fact]
    public void ScalarRecordsRetainTheirExistingMessagePackFormat()
    {
        foreach (ConstantValue original in new[] { ConstantValue.Null, ConstantValue.Number(17), ConstantValue.String("label"), ConstantValue.True })
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            original.Serialize(ref writer);
            writer.Flush();
            var header = new MessagePackReader(buffer.WrittenMemory);
            Assert.Equal(original.IsNull ? 1 : 2, header.ReadArrayHeader());
            var reader = new MessagePackReader(buffer.WrittenMemory);
            Assert.Equal(original, new ConstantValue(ref reader));
            Assert.True(reader.End);
        }
    }

    [Fact]
    public void OutputBuiltinsAddressVariableAndFlagReferences()
    {
        Execute("""
            chapter main {
                CursorPosition($x, #y);
                assert($x == 12);
                assert(#y == 34);
                Position("sprite", #x, $y);
                assert(#x == 56);
                assert($y == 78);
                DateTime(#year, $month, #day, $hour, #minute, $second);
                assert(#year == 2024);
                assert($month == 3);
                assert(#day == 5);
                assert($hour == 6);
                assert(#minute == 7);
                assert($second == 8);
            }
            """);
    }

    [Fact]
    public void QuotedVariableNamesKeepLiteralBrackets()
    {
        var expression = Assert.IsType<NameExpression>(SyntaxTree.ParseExpression("\"$legacy[name]\"").Root);
        Assert.Equal("legacy[name]", expression.Name);
    }

    private static TestBuiltins Execute(string source, bool roundtripGlobals = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-array-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), source, Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            EmittedCompilation emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root), globals);
            NsScriptThreadState thread = vm.CreateThread("test", "main")!.Value;
            var builtins = new TestBuiltins();
            for (int i = 0; i < 1000 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting, "The array script did not terminate.");
            if (roundtripGlobals)
            {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new MessagePackWriter(buffer);
                vm.DumpVariables().Serialize(ref writer);
                vm.DumpFlags().Serialize(ref writer);
                writer.Flush();
                globals.Position = 0;
                vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root), globals);
                var reader = new MessagePackReader(buffer.WrittenMemory);
                vm.RestoreVariables(new GlobalsDump(ref reader));
                vm.RestoreFlags(new GlobalsDump(ref reader));
                Assert.True(reader.End);
                thread = vm.CreateThread("test", "restored")!.Value;
                for (int i = 0; i < 1000 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
                Assert.True(thread.DoneExecuting, "The restored array script did not terminate.");
            }
            return builtins;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public List<string> Choices { get; } = [];
        public override void AssertTrue(bool value) => Assert.True(value);
        public override void CreateChoice(in EntityPath path) => Choices.Add(path.ToString());
        public override Vector2 GetCursorPosition() => new(12, 34);
        public override Vector2 GetPosition(in EntityQuery query) => new(56, 78);
        public override DateTime GetDateTime() => new(2024, 3, 5, 6, 7, 8);
    }
}
