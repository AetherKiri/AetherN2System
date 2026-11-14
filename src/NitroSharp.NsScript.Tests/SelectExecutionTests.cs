using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Syntax;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class SelectExecutionTests
{
    [Theory]
    [InlineData("Start", "Start")]
    [InlineData("\"Start\"", "Start")]
    [InlineData("\"Black\"", "Black")]
    [InlineData("\"Enter\"", "Enter")]
    [InlineData("\"prompt-right\"", "prompt-right")]
    [InlineData("$names[0]", "route-one")]
    [InlineData("#names[0]", "flag-choice")]
    [InlineData("(\"route-\" + $suffix)", "route-one")]
    [InlineData("String(\"route-%s\", $suffix)", "route-one")]
    [InlineData("String(\"%s\", Center == \"Center\")", "1")]
    [InlineData("@alias", "@alias")]
    public void CaseLabelsEvaluateExpressionsAndKeepBareNamesLiteral(string label, string expected)
    {
        var builtins = Execute($$"""
            chapter main {
                $Start = "variable-value";
                $suffix = "one";
                Array($names, "route-one");
                Array(#names, "flag-choice");
                select { case {{label}} { CreateChoice("picked"); break; } }
                CreateChoice("returned");
            }
            """, expected);
        Assert.Equal(new[] { expected }, builtins.Queries);
        Assert.Equal(new[] { "picked", "returned" }, builtins.Choices);
        Assert.Equal(1, builtins.SelectEnds);
    }

    [Fact]
    public void QuotedCancelBranchReceivesTheActualEntityName()
    {
        var builtins = Execute("""
            chapter main {
                $pending = true;
                while ($pending) {
                    select {
                        case "prompt-left" { assert(false); }
                        case "prompt-right" { $pending = false; break; }
                    }
                }
                CreateChoice("cancelled");
            }
            """, "prompt-right");
        Assert.Equal(new[] { "prompt-left", "prompt-right" }, builtins.Queries);
        Assert.Equal(new[] { "cancelled" }, builtins.Choices);
    }

    [Theory]
    [InlineData("\"prompt-right\"")]
    [InlineData("$names[0]")]
    [InlineData("(\"route-\" + $suffix)")]
    public void ExpressionLabelsRemainInTheSyntaxTree(string label)
    {
        var section = Assert.IsType<SelectSection>(SyntaxTree.ParseStatement($"case {label} {{}}").Root);
        Assert.NotNull(section.LabelExpression);
        Assert.Equal(label, section.Label.Value);
    }

    private static TestBuiltins Execute(string source, string pressed)
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-select-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), source, Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            var thread = vm.CreateThread("test", "main")!.Value;
            var builtins = new TestBuiltins(pressed);
            for (int i = 0; i < 100 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
            return builtins;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestBuiltins(string pressed) : BuiltInFunctions
    {
        public List<string> Queries { get; } = [];
        public List<string> Choices { get; } = [];
        public int SelectEnds { get; private set; }
        public override void AssertTrue(bool value) => Assert.True(value);
        public override void CreateChoice(in EntityPath path) => Choices.Add(path.ToString());
        public override bool HandleInputEvents(in EntityQuery query)
        {
            Queries.Add(query.ToString());
            return query.ToString() == pressed;
        }
        public override void SelectEnd() => SelectEnds++;
    }
}
