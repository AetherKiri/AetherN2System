using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class ColorPriorityExecutionTests
{
    [Fact]
    public void AuthoredBlackoutPriorityKeepsItsNativeIntegerPrefix()
    {
        var priorities = Run("""
            chapter main { EndFileBlackOut(20000i); CreateColor("signed", " -42tail", 0, 0, 2, 2, "Black"); }
            function EndFileBlackOut($Pri) { CreateColorEX("EndFileBlackOut", $Pri, "#000000"); }
            function CreateColorEX("name", priority, image) { CreateColor("name", priority, 1024, 0, 1024, 576, image); }
            """);
        Assert.Equal(new[] { 20000, -42 }, priorities.Priorities);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("+tail")]
    [InlineData("2147483648i")]
    [InlineData("-2147483649i")]
    public void MissingDigitsAndOverflowRemainRejected(string priority)
        => Assert.Throws<NsxCallDispatchException>(() => Run("chapter main { CreateColor(\"rect\", \"" + priority + "\", 0, 0, 2, 2, \"Black\"); }"));

    [Theory]
    [InlineData("＃FFFFFF", 0xFFFFFF)]
    [InlineData("＃CC0000", 0xCC0000)]
    public void AuthoredFullwidthHashColorsReachTheRectangle(string color, int rgb)
    {
        var result = Run($$"""
            chapter main { EffectZoomDXadd("{{color}}"); }
            function EffectZoomDXadd(effectColor) { CreateColor("flash", 4011, 1024, 0, 1024, 576, effectColor); }
            """);
        Assert.Equal(NsColor.FromRgb(rgb), Assert.Single(result.Colors));
    }

    [Theory]
    [InlineData("＃＃FFFFFF")]
    [InlineData("＃FFGGFF")]
    [InlineData("＃ＦＦＦＦＦＦ")]
    public void FullwidthHashDoesNotNormalizeOtherMalformedColorText(string color)
        => Assert.Throws<ArgumentException>(() => NsColor.FromString(color));

    private static RecordingBuiltins Run(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-color-priority-" + Guid.NewGuid().ToString("N"));
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
            return builtins;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RecordingBuiltins : BuiltInFunctions
    {
        public List<int> Priorities { get; } = [];
        public List<NsColor> Colors { get; } = [];
        public override void CreateRectangle(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y,
            uint width, uint height, NsColor color) { Priorities.Add(priority); Colors.Add(color); }
    }
}
