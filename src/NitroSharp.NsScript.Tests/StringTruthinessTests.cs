using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class StringTruthinessTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("achievement", true)]
    [InlineData("中文", true)]
    [InlineData(" ", true)]
    [InlineData("0", true)]
    [InlineData("false", true)]
    public void UnaryAndLogicalOperatorsAndBranchesUseStringLength(string value, bool expected)
    {
        string result = expected ? "true" : "false";
        // The legacy parser treats a quoted "false" as a Boolean literal.
        // Build that spelling at runtime to exercise an actual string value.
        string expression = value == "false" ? "\"fal\" + \"se\"" : "\"" + value + "\"";
        Execute($$"""
            chapter main {
                $value = {{expression}};
                assert(!$value == !{{result}});
                assert(!!$value == {{result}});
                assert(!!($value && true) == {{result}});
                assert(!!($value || false) == {{result}});
                assert(!!(true && $value) == {{result}});
                assert(!!(false || $value) == {{result}});
                if ($value) { assert({{result}}); }
                else { assert(!{{result}}); }
                $iterations = 0;
                while ($value) { $iterations++; $value = ""; }
                assert($iterations == {{(expected ? 1 : 0)}});
                assert(!$value);
            }
            """);
    }

    [Fact]
    public void AdAchievementNameConditionDistinguishesMissingAndPresentNames()
    {
        var builtins = Execute("""
            chapter main {
                GameMainSet("");
                GameMainSet("achievement-name");
            }
            function GameMainSet($実績名前メインセット) {
                $実績名前セット = $実績名前メインセット;
                if (!$実績名前セット || $実績名前セット == "") { CreateChoice("empty"); }
                else { CreateChoice($実績名前セット); }
                $実績名前セット = "";
            }
            """);
        Assert.Equal(new[] { "empty", "achievement-name" }, builtins.Choices);
    }

    [Fact]
    public void ArrayRowsAndRootsUseTheirStringScalarInsteadOfTheirChildren()
    {
        Execute("""
            chapter main {
                Array($rows, "", "present");
                Array($rows[0], 7, 8);
                Array($rows[1], 9);
                assert(!$rows[0]);
                assert(!!$rows[1]);
                assert(($rows[0] || $rows[1]) == true);
                assert(($rows[0] && $rows[1]) == false);
                if ($rows[0]) { assert(false); }
                if ($rows[1]) { assert(Count($rows[1]) == 1); }
                Array(#records, "label");
                Array(#records[0], 1, 2);
                assert(!!#records[0]);
                #records[0] = "";
                assert(!#records[0]);
                assert(Count(#records[0]) == 2);
            }
            """);
    }

    [Fact]
    public void NumericAndBooleanFlagsKeepTheirExistingRulesAndNullStaysUnsupported()
    {
        Execute("""
            chapter main {
                #zero = 0;
                #negative = -1;
                #positive = 2;
                #off = false;
                #on = true;
                assert(!#zero && !#negative && #positive);
                assert(!#off && #on);
                if (#negative) { assert(false); }
                if (#positive) { assert(true); }
                while (#on) { #on = false; }
                assert(!#on);
            }
            """);
        Assert.Null(ConstantValue.Null.AsBool());
        var error = Assert.Throws<InvalidOperationException>(() => Execute("chapter main { $value = null; if (!$value) { } }"));
        Assert.Contains("type 'Null'", error.Message);
    }

    private static TestBuiltins Execute(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "string-truthiness-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), source, Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            var state = vm.CreateThread("test", "main")!.Value;
            var builtins = new TestBuiltins();
            for (int i = 0; i < 100 && !state.DoneExecuting; i++) vm.Tick(ref state, builtins);
            Assert.True(state.DoneExecuting);
            return builtins;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public List<string> Choices { get; } = [];
        private int _assertions;
        public override void AssertTrue(bool value) => Assert.True(value, $"Script assertion {++_assertions} failed.");
        public override void CreateChoice(in EntityPath path) => Choices.Add(path.ToString());
    }
}
