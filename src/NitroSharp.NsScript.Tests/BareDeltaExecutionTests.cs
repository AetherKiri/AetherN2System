using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.Syntax;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class BareDeltaExecutionTests
{
    [Fact]
    public void BareRelativeCoordinatePreservesEveryMoveArgumentAndFollowingStatement()
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-bare-delta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), """
                chapter main {
                    Move("絵演村正", 0, @, @400, null, true);
                    Move("following", 123, 12, @, null, true);
                }
                """, Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            var thread = vm.CreateThread("test", "main")!.Value;
            var builtins = new RecordingBuiltins();
            for (int i = 0; i < 100 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
            Assert.Equal(2, builtins.Calls.Count);
            var first = builtins.Calls[0];
            Assert.Equal("絵演村正", first.Query);
            Assert.Equal(TimeSpan.Zero, first.Duration);
            Assert.Equal((0f, true), first.X.Value);
            Assert.Equal((400f, true), first.Y.Value);
            Assert.Equal(NsEaseFunction.Linear, first.Ease);
            Assert.Equal(TimeSpan.Zero, first.Timeout);
            var second = builtins.Calls[1];
            Assert.Equal("following", second.Query);
            Assert.Equal(TimeSpan.FromMilliseconds(123), second.Duration);
            Assert.Equal((12f, false), second.X.Value);
            Assert.Equal((0f, true), second.Y.Value);
            Assert.Equal(NsEaseFunction.Linear, second.Ease);
            Assert.Equal(second.Duration, second.Timeout);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("Move(\"x\", 0, -, 0, null, true)")]
    [InlineData("Move(\"x\", 0, !, 0, null, true)")]
    [InlineData("Move(\"x\", 0, @-, 0, null, true)")]
    public void OtherMissingUnaryOperandsStillReportDiagnostics(string source)
        => Assert.NotEmpty(SyntaxTree.ParseExpression(source).Diagnostics);

    private sealed class RecordingBuiltins : BuiltInFunctions
    {
        public List<(string Query, TimeSpan Duration, NsCoordinate X, NsCoordinate Y, NsEaseFunction Ease, TimeSpan Timeout)> Calls { get; } = [];
        public override void Move(in EntityQuery query, TimeSpan duration, NsCoordinate x, NsCoordinate y,
            NsEaseFunction ease, TimeSpan timeout) => Calls.Add((query.ToString(), duration, x, y, ease, timeout));
    }
}
