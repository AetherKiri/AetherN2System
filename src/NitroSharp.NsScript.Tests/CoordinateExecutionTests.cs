using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class CoordinateExecutionTests
{
    [Fact]
    public void RequestAliasesSurviveBareQuotedAndForwardedArguments()
    {
        var builtins = Execute("""
            chapter main {
                Request("@choice", Clickable);
                Request("@choice", UnClickable);
                Forward("clickable");
                Forward("UNCLICKABLE");
                Forward(Clickable);
                Request("@choice", Lock);
                Request("@choice", UnLock);
                Request("@worker", Start);
                Request("@worker", Stop);
                Request("@sound", Play);
                Request("@sprite", Erase);
            }
            function Forward($action) { Request("@choice", $action); }
            """);
        Assert.Equal(new[]
        {
            ("@choice", NsEntityAction.Enable), ("@choice", NsEntityAction.Disable),
            ("@choice", NsEntityAction.Enable), ("@choice", NsEntityAction.Disable),
            ("@choice", NsEntityAction.Enable), ("@choice", NsEntityAction.Lock),
            ("@choice", NsEntityAction.Unlock), ("@worker", NsEntityAction.Start),
            ("@worker", NsEntityAction.Stop), ("@sound", NsEntityAction.Play),
            ("@sprite", NsEntityAction.Disable)
        }, builtins.Actions);
    }

    [Fact]
    public void RequestStillRejectsUnknownActionStrings()
        => Assert.Throws<NsxCallDispatchException>(() => Execute("chapter main { Request(\"item\", \"unknown-action\"); }"));

    [Fact]
    public void PlainEffectAndPassiveSurviveBareQuotedAndForwardedArguments()
    {
        var builtins = Execute("""
            chapter main {
                CreateEffect("snapshot", 500, 512, 0, 512, 576, "Plain");
                Request("snapshot", Passive);
                Request("snapshot", "passive");
                ForwardPassive(Passive);
                ForwardPassive("PASSIVE");
            }
            function ForwardPassive($action) { Request("snapshot", $action); }
            """);
        Assert.Equal(new[]
        {
            ("snapshot", NsEntityAction.Passive), ("snapshot", NsEntityAction.Passive),
            ("snapshot", NsEntityAction.Passive), ("snapshot", NsEntityAction.Passive)
        }, builtins.Actions);
        var effect = Assert.Single(builtins.Effects);
        Assert.Equal("snapshot", effect.path);
        Assert.Equal(500, effect.priority);
        Assert.Equal((512f, false), effect.x.Value);
        Assert.Equal((0f, false), effect.y.Value);
        Assert.Equal(512u, effect.width);
        Assert.Equal(576u, effect.height);
        Assert.Equal("Plain", effect.name);
    }

    [Fact]
    public void SetScrollSpeedRetainsTheUnscaledNumericStep()
    {
        var builtins = Execute("chapter main { SetScrollSpeed(\"slider\", 3); SetScrollSpeed(\"fraction\", 1.5); }");
        Assert.Equal(new[] { ("slider", 3f), ("fraction", 1.5f) }, builtins.ScrollSpeeds);
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("None")]
    [InlineData("none")]
    public void SetFontNoneDisablesOutlineAndRetainsCustomWeight(string none)
    {
        var builtins = Execute($"chapter main {{ SetFont(\"Sample font\", 21, #000000, #FFFFFF, 500, {none}); }}");
        var font = Assert.Single(builtins.Fonts);
        Assert.Equal(NsOutlineOffset.Unspecified, font.outline);
        Assert.Equal(NsFontWeightVariant.Custom, font.weight.Variant);
        Assert.Equal(500, font.weight.Custom);
        Assert.Equal(NsColor.Black, font.color);
        Assert.Equal(NsColor.White, font.outlineColor);
    }

    [Fact]
    public void SetFontRejectsUnrelatedOutlineConstants()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Execute("chapter main { SetFont(\"Sample font\", 21, Black, White, 500, Center); }"));
    }

    [Fact]
    public void BareAlignmentConstantsSurviveHelperCallsAndConstantComparisons()
    {
        var builtins = Execute("""
            chapter main {
                Forward(Center, Middle);
                CheckLabel("Center");
                Array($labels, Start, Center);
                assert($labels[0] == "Start");
                CreateChoice(Center);
            }
            function Forward($x, $y) { Inner($x, $y); }
            function Inner($x, $y) {
                if ($x == "Center" && $y == "Middle") { SetVertex("matched", 7, 9); }
                else { assert(false); }
                SetVertex("aligned", $x, $y);
            }
            function CheckLabel($label) { CreateChoice($label); }
            """);
        Assert.Equal(new[] { "Center", "Center" }, builtins.Choices);
        Assert.Collection(builtins.Vertices,
            value =>
            {
                Assert.Equal("matched", value.query);
                Assert.Equal(NsCoordinateKind.Value, value.x.Kind);
                Assert.Equal(7f, value.x.Value.pos);
                Assert.Equal(9f, value.y.Value.pos);
            },
            value =>
            {
                Assert.Equal("aligned", value.query);
                Assert.Equal(NsCoordinateKind.Alignment, value.x.Kind);
                Assert.Equal(NsAlignment.Center, value.x.Alignment);
                Assert.Equal(NsAlignment.Center, value.y.Alignment);
            });
    }

    [Fact]
    public void SetVertexDispatchRetainsNumericAndRelativeCoordinates()
    {
        var builtins = Execute("chapter main { SetVertex(\"sprite\", 20, @-5); }");
        var value = Assert.Single(builtins.Vertices);
        Assert.Equal("sprite", value.query);
        Assert.Equal((20f, false), value.x.Value);
        Assert.Equal((-5f, true), value.y.Value);
    }

    [Fact]
    public void SetVertexReportsUnknownAxisAndPreservesOnlyThatComponent()
    {
        var builtins = Execute("chapter main { SetVertex(\"sprite\", UnknownPivot, 110); }");
        var vertex = Assert.Single(builtins.Vertices);
        Assert.Equal(NsCoordinateKind.Inherit, vertex.x.Kind);
        Assert.Equal((110f, false), vertex.y.Value);
        var warning = Assert.Single(builtins.InvalidArguments);
        Assert.Equal(("SetVertex", "x", "UnknownPivot"), warning);
    }

    [Fact]
    public void MoveStillRejectsUnknownCoordinateLabels()
    {
        Assert.Throws<ArgumentException>(() => Execute("chapter main { Move(\"sprite\", 0, UnknownPivot, 110, null, true); }"));
    }

    private static TestBuiltins Execute(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-coordinate-test-" + Guid.NewGuid().ToString("N"));
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
            var builtins = new TestBuiltins();
            for (int i = 0; i < 100 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
            return builtins;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public List<string> Choices { get; } = [];
        public List<(string query, NsCoordinate x, NsCoordinate y)> Vertices { get; } = [];
        public List<(string function, string argument, string value)> InvalidArguments { get; } = [];
        public List<(NsFontWeight weight, NsOutlineOffset outline, NsColor color, NsColor outlineColor)> Fonts { get; } = [];
        public List<(string query, float speed)> ScrollSpeeds { get; } = [];
        public List<(string query, NsEntityAction action)> Actions { get; } = [];
        public List<(string path, int priority, NsCoordinate x, NsCoordinate y, uint width, uint height, string name)> Effects { get; } = [];
        public override void AssertTrue(bool value) => Assert.True(value);
        public override void CreateChoice(in EntityPath entityPath) => Choices.Add(entityPath.ToString());
        public override void SetVertex(in EntityQuery query, NsCoordinate x, NsCoordinate y) => Vertices.Add((query.ToString(), x, y));
        public override void ReportInvalidArgument(string functionName, string argumentName, in ConstantValue value)
            => InvalidArguments.Add((functionName, argumentName, value.ConvertToString()));
        public override void SetFont(string family, uint size, NsColor color, NsColor outlineColor, NsFontWeight weight, NsOutlineOffset outlineOffset)
            => Fonts.Add((weight, outlineOffset, color, outlineColor));
        public override void SetScrollSpeed(in EntityQuery query, float speed) => ScrollSpeeds.Add((query.ToString(), speed));
        public override void Request(in EntityQuery query, NsEntityAction action) => Actions.Add((query.ToString(), action));
        public override void CreateEffect(in EntityPath entityPath, int priority, NsCoordinate x, NsCoordinate y, uint width, uint height, string effectName)
            => Effects.Add((entityPath.ToString(), priority, x, y, width, height, effectName));
    }
}
