using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using NitroSharp.Common;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using NitroSharp.Saving;
using NitroSharp.Text;
using Veldrid;
using Xunit;
using DesignSize = NitroSharp.Size<NitroSharp.DesignPixel>;
using DesignSizeU = NitroSharp.SizeU<NitroSharp.DesignPixel>;

namespace NitroSharp.Tests;

public sealed class ScenePersistenceTests
{
    [Fact]
    public void SceneRoundtripKeepsHierarchyAliasesChoiceReferencesAndDrawState()
    {
        using var script = new Script();
        GameContext ctx = script.Context;
        Process original = ctx.World.MainProcess;
        Thread thread = original.MainThread;
        var scope = ctx.World.AddEntity(new CallScopeEntity(EntityName.Parse("scene"), thread));
        var first = ctx.World.AddEntity(new ChoiceEntity(EntityName.Parse("first"), scope, true));
        var second = ctx.World.AddEntity(new ChoiceEntity(EntityName.Parse("second"), scope));
        first.SetNextFocus(NsFocusDirection.Right, second);
        var usual = ctx.World.AddEntity(new MouseStateEntity(EntityName.Parse("MouseUsual"), first));
        var visual = ctx.World.AddEntity(new SolidColorRect(EntityName.Parse("image"), usual, 7,
            new RgbaFloat(.1f, .2f, .3f, .4f), new DesignSize(320, 180)));
        visual.Transform.Position = new Vector3(17, 29, 3);
        visual.Transform.Scale = new Vector3(2, 3, 1);
        visual.Transform.Rotation = new Vector3(0, 0, 30);
        visual.Transform.Pivot = new Vector2(5, 8);
        visual.BlendMode = BlendMode.Additive;
        visual.FilterMode = FilterMode.Point;
        var knob = ctx.World.AddEntity(new Scrollbar(EntityName.Parse("scroll"), scope, 9,
            SpriteTexture.SolidColor(RgbaFloat.White, new DesignSizeU(10, 20)),
            Vector2.Zero, new Vector2(100, 0), NsScrollDirection.Horizontal, .25f));
        var sprite = ctx.World.AddEntity(new Sprite(EntityName.Parse("plate"), scope, 11,
            SpriteTexture.SolidColor(RgbaFloat.White, new DesignSizeU(8, 8))));
        sprite.SetTone(NsTone.Sepia);
        sprite.Request(NsEntityAction.DestroyWhenIdle);
        knob.ScrollTarget = visual; knob.ScrollSpeed = 3;
        visual.SetAlias(EntityAlias.Parse("active-image"));
        ctx.World.BeginFrame();
        visual.Request(NsEntityAction.Lock); second.Disable();
        visual.Request(NsEntityAction.Disable);
        original.RestoreCurrentThread(thread);
        byte[] saved = ScenePersistence.Capture(ctx, original);
        visual.Transform.Position = Vector3.Zero;
        Process restored = ScenePersistence.Restore(ctx, saved);
        Assert.Same(original, ctx.World.MainProcess);
        Assert.NotSame(original, restored);
        var restoredScope = restored.MainThread.TryGetChild(EntityName.Parse("scene"))!;
        var restoredFirst = (ChoiceEntity)restoredScope.TryGetChild(EntityName.Parse("first"))!;
        var restoredSecond = (ChoiceEntity)restoredScope.TryGetChild(EntityName.Parse("second"))!;
        Assert.Same(restoredSecond, restoredFirst.GetNextFocus(NsFocusDirection.Right));
        Assert.True(restoredFirst.PreserveOpacity);
        Assert.False(restoredSecond.IsEnabled);
        var results = new SmallList<Entity>();
        restored.Aliases.Query(new EntityPattern("active-image", false), ref results);
        var restoredVisual = Assert.IsType<SolidColorRect>(results[0]);
        Assert.Equal(new Vector3(17, 29, 3), restoredVisual.Transform.Position);
        Assert.Equal(new Vector3(2, 3, 1), restoredVisual.Transform.Scale);
        Assert.Equal(new Vector2(5, 8), restoredVisual.Transform.Pivot);
        Assert.True(restoredVisual.IsHidden); Assert.True(restoredVisual.IsLocked);
        Assert.Equal(BlendMode.Additive, restoredVisual.BlendMode);
        var restoredKnob = (Scrollbar)restoredScope.TryGetChild(EntityName.Parse("scroll"))!;
        var restoredPlate = Assert.IsType<Sprite>(restoredScope.TryGetChild(EntityName.Parse("plate")));
        Assert.Equal(NsTone.Sepia, restoredPlate.Tone);
        Assert.True(restoredPlate.RetireWhenIdle);
        Assert.Same(restoredVisual, restoredKnob.ScrollTarget);
        Assert.Equal(.25f, restoredKnob.Value); Assert.Equal(3, restoredKnob.ScrollSpeed);
        ctx.World.BeginFrame();
        Assert.False(restoredSecond.IsEnabled);
        Assert.Equal(saved, ScenePersistence.Capture(ctx, restored));
        ctx.World.DisposeAll();
    }

    [Fact]
    public void ActiveOpacityAnimationResumesAtTheSameTimeAndDestination()
    {
        var source = new SolidColorRect(EntityName.Parse("source"), null, 1, RgbaFloat.White, default);
        var animation = new OpacityAnimation(source, 1, 0, TimeSpan.FromMilliseconds(100), NsEaseFunction.QuadraticEaseIn);
        animation.Update(0); animation.Update(40);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        AnimationPersistence.Write(writer, animation); writer.Flush(); stream.Position = 0;
        var restored = new SolidColorRect(EntityName.Parse("restored"), null, 1, RgbaFloat.White, default);
        var resumed = AnimationPersistence.Read(new BinaryReader(stream), restored)!;
        Assert.True(animation.Update(25)); Assert.True(resumed.Update(25));
        Assert.Equal(source.Color, restored.Color);
        Assert.Equal(.5775f, restored.Color.A, 4);
        Assert.False(animation.Update(100)); Assert.False(resumed.Update(100));
        Assert.Equal(0, restored.Color.A);
    }

    [Fact]
    public void TruncatedSceneRollsBackWithoutChangingTheOriginalProcess()
    {
        using var script = new Script();
        Process original = script.Context.World.MainProcess;
        byte[] saved = ScenePersistence.Capture(script.Context, original);
        Assert.Throws<EndOfStreamException>(() => ScenePersistence.Restore(script.Context, saved[..^1]));
        Assert.Same(original, script.Context.World.MainProcess);
        Assert.False(original.IsPendingDestruction);
        script.Context.World.BeginFrame();
        script.Context.World.DisposeAll();
    }

    private static GameContext MinimalContext(World? world = null, NsScriptVM? vm = null) => new()
    {
        Window = null!, Profile = null!, RenderContext = null!, Content = null!, GlyphRasterizer = null!,
        AudioContext = null!, InputContext = null!, VM = vm!, World = world!, RootLog = null!
    };

    private sealed class Script : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "scene-save-test-" + Guid.NewGuid().ToString("N"));
        private readonly Stream _globals;
        internal GameContext Context { get; }
        public Script()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "story.nss"), "chapter main { WaitKey(); }", Encoding.UTF8);
            var compilation = new Compilation(_root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("story.nss")], _root, "globals");
            Assert.Empty(emitted.Diagnostics);
            _globals = File.OpenRead(Path.Combine(_root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(_root, _root, Encoding.UTF8), _globals);
            var process = new Process(EntityName.Parse("story"), null, new FontSettings());
            var thread = new Thread(EntityName.Parse("main"), process, vm.CreateThread("story", "main")!.Value, true);
            Context = MinimalContext(new World(process, thread), vm);
        }
        public void Dispose() { _globals.Dispose(); Directory.Delete(_root, true); }
    }
}

public sealed class DialogueSaveStateTests(FontContext fonts) : IClassFixture<FontContext>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullDialoguePageRoundtripKeepsWrappedBoundsAndRevealLayout(bool vertical)
    {
        string root = Path.Combine(Path.GetTempPath(), "dialogue-page-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string markup = "A long wrapped sentence with several words and punctuation.\nAnother explicit line with <RUBY TEXT=むらまさ>村正</RUBY> ruby.";
            File.WriteAllText(Path.Combine(root, "story.nss"), "chapter main {\n<PRE box>\n[page]\n" + markup + "\n</PRE>\n}", Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("story.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            var font = new FontSettings
            {
                DefaultFont = fonts.DefaultFont, DefaultFontSize = new PtFontSize(20),
                DefaultTextColor = Vector4.One, RubyFontSizeMultiplier = .4f, VerticalGlyphs = vertical
            };
            var process = new Process(EntityName.Parse("story"), null, font);
            var thread = new Thread(EntityName.Parse("main"), process, vm.CreateThread("story", "main")!.Value, true);
            var world = new World(process, thread);
            var ctx = new GameContext
            {
                Window = null!, Profile = null!, RenderContext = null!, Content = null!,
                GlyphRasterizer = fonts.Rasterizer, AudioContext = null!, InputContext = null!, VM = vm,
                World = world, RootLog = null!
            };
            var token = new DialogueBlockToken("box", "page", vm.GetSaveModule("story"), 0, 0);
            var page = world.AddEntity(new DialoguePage(EntityName.Parse("page"), thread, 10, token, 160, 420, 28));
            page.Request(NsEntityAction.Enable);
            var layout = (TextLayout<NitroSharp.DesignPixel>)typeof(DialoguePage)
                .GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            foreach (TextSegment segment in Dialogue.Parse(markup, font).Segments.OfType<TextSegment>())
                layout.Append(fonts.Rasterizer, segment.TextRuns.AsSpan());
            Assert.True(layout.Lines.Length > 2);
            var animation = new DialogueTextAnimation(layout, 0, 48, 200);
            animation.Update(135);
            typeof(DialoguePage).GetField("_textAnimation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, animation);
            world.BeginFrame();
            byte[] saved = ScenePersistence.Capture(ctx, process);

            Process restored = ScenePersistence.Restore(ctx, saved);
            var restoredPage = (DialoguePage)restored.MainThread.TryGetChild(EntityName.Parse("page"))!;
            var restoredLayout = (TextLayout<NitroSharp.DesignPixel>)typeof(DialoguePage)
                .GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(restoredPage)!;

            Assert.Equal(layout.MaxBounds, restoredLayout.MaxBounds);
            Assert.Equal(layout.BoundingBox, restoredLayout.BoundingBox);
            Assert.Equal(layout.Glyphs.ToArray(), restoredLayout.Glyphs.ToArray());
            Assert.Equal(layout.OpacityValues.ToArray(), restoredLayout.OpacityValues.ToArray());
            Assert.Equal(saved, ScenePersistence.Capture(ctx, restored));
            world.DisposeAll();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void StyledRubyRunsAndMidRevealProgressSurviveBinaryRoundtrip()
    {
        var runs = new[]
        {
            TextRun.WithRubyText("村正".AsMemory(), "むらまさ".AsMemory(), fonts.DefaultFont,
                new PtFontSize(20), new RgbaFloat(.2f, .3f, .4f, 1), RgbaFloat.Black),
            TextRun.Regular("次の文".AsMemory(), fonts.DefaultFont, new PtFontSize(24), RgbaFloat.White, null)
        };
        var source = new TextLayout<NitroSharp.DesignPixel>(fonts.Rasterizer, runs, 300, 200);
        var animation = new DialogueTextAnimation(source, 0, 48, 200);
        animation.Update(135);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        SaveIO.WriteRuns(writer, source.SavedRuns); animation.WriteSave(writer); writer.Flush();
        stream.Position = 0; using var reader = new BinaryReader(stream);
        var restoredRuns = SaveIO.ReadRuns(reader);
        var restored = new TextLayout<NitroSharp.DesignPixel>(fonts.Rasterizer, restoredRuns.AsSpan(), 300, 200);
        var resumed = DialogueTextAnimation.ReadSave(reader, restored);
        Assert.Equal(source.Glyphs.ToArray(), restored.Glyphs.ToArray());
        Assert.Equal(source.OpacityValues.ToArray(), restored.OpacityValues.ToArray());
        animation.Update(70); resumed.Update(70);
        Assert.Equal(source.OpacityValues.ToArray(), restored.OpacityValues.ToArray());
        Assert.True(resumed.IsActive);
        animation.Complete(); resumed.Complete();
        Assert.All(restored.OpacityValues.ToArray(), value => Assert.Equal(1, value));
    }
}
