using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class RelativeCoordinateExecutionTests
{
    [Fact]
    public void ComicHelpersKeepRelativeCoordinatesThroughRepeatedDeltaAndOffsets()
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-relative-comic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), """
                chapter main {
                    SetComic(@0, @0);
                    SetComic(@20, @-10);
                }
                function SetComic($COMIC_X, $COMIC_Y) {
                    Move("heart", 0, @$COMIC_X, @$COMIC_Y, null, true);
                    $COMIC_Y = $COMIC_Y + 30;
                    $COMIC_X = $COMIC_X + 10;
                    Move("heart", 0, $COMIC_X, $COMIC_Y, null, true);
                    $COMIC_X_1 = "@" + $COMIC_X;
                    $COMIC_Y_2 = $COMIC_Y - 24;
                    $COMIC_Y_2 = "@" + $COMIC_Y_2;
                    Move("heart", 0, $COMIC_X_1, @$COMIC_Y_2, null, true);
                    $negative = "@" + "-" + 5;
                    Move("heart", 0, $negative, "@2.5", null, true);
                }
                """, Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            var thread = vm.CreateThread("test", "main")!.Value;
            var builtins = new TestBuiltins();
            for (int i = 0; i < 100 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
            Assert.Equal(new[] { (0f, 0f), (10f, 30f), (10f, 6f), (-5f, 2.5f), (20f, -10f), (30f, 20f), (30f, -4f), (-5f, 2.5f) }, builtins.Positions);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RelativeArithmeticLeavesOrdinaryNumbersAndStringLabelsUnchanged()
    {
        ConstantValue ordinary = ConstantValue.Number(10) + ConstantValue.Number(20);
        Assert.Equal(BuiltInType.Numeric, ordinary.Type);
        Assert.Equal(30f, ordinary.AsNumber());
        Assert.Equal(7f, (ConstantValue.Number(10) - ConstantValue.Number(3)).AsNumber());
        Assert.Equal("offset@12", (ConstantValue.String("offset") + ConstantValue.Delta(12)).AsString());
        Assert.Equal("label10", (ConstantValue.String("label") + ConstantValue.Number(10)).AsString());
        Assert.Equal(12f, (ConstantValue.String("@") + ConstantValue.Number(12)).AsDeltaNumber());
    }

    [Theory]
    [InlineData("1.5", 1.5f, false)]
    [InlineData("@-12.5", -12.5f, true)]
    [InlineData("@2e1", 20f, true)]
    public void NumericCoordinateStringsUseInvariantCulture(string text, float expected, bool relative)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            NsCoordinate coordinate = NsCoordinate.FromValue(ConstantValue.String(text));
            Assert.Equal((expected, relative), coordinate.Value);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("@@1")]
    [InlineData("NaN")]
    [InlineData("@Infinity")]
    [InlineData("1e99")]
    [InlineData("1,5")]
    [InlineData("UnknownPivot")]
    public void InvalidCoordinateStringsRemainRejected(string text)
        => Assert.Throws<ArgumentException>(() => NsCoordinate.FromValue(ConstantValue.String(text)));

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public List<(float, float)> Positions { get; } = [];
        public override void Move(in EntityQuery query, TimeSpan duration, NsCoordinate x, NsCoordinate y,
            NsEaseFunction ease, TimeSpan timeout)
        {
            Assert.Equal(NsCoordinateKind.Value, x.Kind);
            Assert.Equal(NsCoordinateKind.Value, y.Kind);
            Assert.True(x.Value.isRelative);
            Assert.True(y.Value.isRelative);
            Positions.Add((x.Value.pos, y.Value.pos));
        }
    }
}
