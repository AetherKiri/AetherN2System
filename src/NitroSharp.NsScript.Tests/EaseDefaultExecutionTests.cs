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

public sealed class EaseDefaultExecutionTests
{
    [Fact]
    public void AutoHelperUsesLinearForUnsetNumericEaseAndPreservesKnownEasing()
    {
        var calls = Run("""
            function CP_RollBar_Auto() {
                Rotate("@CPCenter", 1000, @0, @0, @+5, $CPRB_Sp, true);
            }
            chapter main {
                CP_RollBar_Auto();
                Move("following", 20, 0, 0, Dxl1, false);
                Fade("following", 30, 1000, Axl1, false);
            }
            """);
        Assert.Equal(new[] { NsEaseFunction.Linear, NsEaseFunction.QuadraticEaseOut, NsEaseFunction.QuadraticEaseIn }, calls);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("\"0\"")]
    public void UnsupportedEaseValuesRemainRejected(string ease)
        => Assert.Throws<NsxCallDispatchException>(() => Run("chapter main { Rotate(\"x\", 10, 0, 0, 0, " + ease + ", false); }"));

    private static List<NsEaseFunction> Run(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-ease-default-" + Guid.NewGuid().ToString("N"));
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
            return builtins.Calls;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RecordingBuiltins : BuiltInFunctions
    {
        public List<NsEaseFunction> Calls { get; } = [];
        public override void Rotate(in EntityQuery query, TimeSpan duration, NsNumeric x, NsNumeric y, NsNumeric z,
            NsEaseFunction ease, TimeSpan timeout) => Calls.Add(ease);
        public override void Move(in EntityQuery query, TimeSpan duration, NsCoordinate x, NsCoordinate y,
            NsEaseFunction ease, TimeSpan timeout) => Calls.Add(ease);
        public override void Fade(in EntityQuery query, TimeSpan duration, NsRational opacity,
            NsEaseFunction ease, TimeSpan timeout) => Calls.Add(ease);
    }
}
