using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class MoveCoordinateExecutionTests
{
    [Fact]
    public void MoveUsesNativeIntegerFallbackWithoutCorrectingUnknownPivotNames()
    {
        var positions = Run("""
            chapter main {
                Move("impact", 0, cehter, "@-535e", null, false);
                Move("impact", 0, 12.5, @-3.5, null, false);
                Move("impact", 0, Center, Middle, null, false);
                Move("impact", 0);
            }
            """);
        Assert.Equal((0f, false), positions[0].X.Value);
        Assert.Equal((-535f, true), positions[0].Y.Value);
        Assert.Equal((12.5f, false), positions[1].X.Value);
        Assert.Equal((-3.5f, true), positions[1].Y.Value);
        Assert.Equal(NsCoordinateKind.Alignment, positions[2].X.Kind);
        Assert.Equal(NsAlignment.Center, positions[2].X.Alignment);
        Assert.Equal(NsAlignment.Center, positions[2].Y.Alignment);
        Assert.Equal((0f, false), positions[3].X.Value);
        Assert.Equal((0f, false), positions[3].Y.Value);
    }

    [Theory]
    [InlineData("2147483648i")]
    [InlineData("@-2147483649i")]
    public void MoveFallbackRejectsIntegerOverflow(string coordinate)
        => Assert.Throws<NsxCallDispatchException>(() => Run("chapter main { Move(\"impact\", 0, \"" + coordinate + "\", 0, null, false); }"));

    private static List<(NsCoordinate X, NsCoordinate Y)> Run(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-move-coordinate-" + Guid.NewGuid().ToString("N"));
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
            var builtins = new RecordingBuiltins();
            for (int i = 0; i < 100 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
            return builtins.Positions;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RecordingBuiltins : BuiltInFunctions
    {
        public List<(NsCoordinate X, NsCoordinate Y)> Positions { get; } = [];
        public override void Move(in EntityQuery query, TimeSpan duration, NsCoordinate x, NsCoordinate y,
            NsEaseFunction ease, TimeSpan timeout) => Positions.Add((x, y));
    }
}
