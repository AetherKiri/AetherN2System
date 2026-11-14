using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using NitroSharp.LegacyNps;
using Xunit;

namespace NitroSharp.Tests;

public sealed class LegacyNpsTranslatorTests
{
    [Fact]
    public void IncrementalBustupChangesRetainBackgroundAndOtherActorsUntilClear()
    {
        string source = "<BACKGROUND SRC=room><BUSTUP NAME=A FACE=normal MODE=on ALIGN=left><BUSTUP NAME=B FACE=normal MODE=on ALIGN=right>"
            + "<WIPE EFFECT=NORMAL><K><BUSTUP NAME=A MODE=off><WIPE EFFECT=FADE TIME=1><K>"
            + "<BUSTUP NAME=B FACE=happy><WIPE EFFECT=NORMAL><K><BUSTUP NAME=B MODE=off><WIPE EFFECT=NORMAL><K><CLEAR><WIPE EFFECT=NORMAL><K>";
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate(source, "scene.nps"), builtins);
        Assert.Equal(5, builtins.Scenes.Count);
        Assert.Equal(new[] { "cg/A_normal.png", "cg/B_normal.png", "cg/room.png" }, Images(builtins.Scenes[0]));
        Assert.Equal(new[] { "cg/B_normal.png", "cg/room.png" }, Images(builtins.Scenes[1]));
        Assert.Equal(new[] { "cg/B_happy.png", "cg/room.png" }, Images(builtins.Scenes[2]));
        Assert.Equal(new[] { "cg/room.png" }, Images(builtins.Scenes[3]));
        Assert.Empty(Images(builtins.Scenes[4]));
        Assert.Equal(-160, builtins.Scenes[0]["legacy_front/actor0"].X);
        Assert.Equal(160, builtins.Scenes[0]["legacy_front/actor1"].X);
        Assert.Equal(160, builtins.Scenes[2]["legacy_front/actor1"].X);
        Assert.True(builtins.PendingPriorityCoveredFront);

        static string[] Images(Dictionary<string, SceneVisual> scene)
            => scene.Values.Where(item => item.Image.Length > 0).Select(item => item.Image).Order().ToArray();
    }

    [Fact]
    public void ConfiguredNarrativeBoxPlacesFrameAndInnerTextAndHidesBoth()
    {
        var presentation = new LegacyNpsPresentation(new Dictionary<int, LegacyNpsTextBox>
            { [9] = new(35, 45, 630, 420, 17, 8, 13, 12, "data/panel.png", 75) },
            new Dictionary<string, int>());
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<BOX TYPE=9>First page<k><HIDEMESSAGE><K>Second page<k>", "text.nps", presentation: presentation), builtins);
        Assert.Equal(2, builtins.TextAreas.Count);
        Assert.All(builtins.TextAreas, area => Assert.Equal((600u, 400u), area));
        Assert.Equal((52f, 43f, 600u, 410u), builtins.Window);
        Assert.Equal("data/panel.png", builtins.Visuals["legacy_box_frame"].Image);
        Assert.Equal((35f, 45f, .75f), (builtins.Visuals["legacy_box_frame"].X, builtins.Visuals["legacy_box_frame"].Y, builtins.Visuals["legacy_box_frame"].Alpha));
        Assert.False(builtins.Scenes.Single().ContainsKey("legacy_box_frame"));
    }

    [Fact]
    public void DirectionalWipeUsesScreenMaskAndWaitsBeforeCommitting()
    {
        var presentation = new LegacyNpsPresentation(new Dictionary<int, LegacyNpsTextBox>(),
            new Dictionary<string, int>(), new Dictionary<string, string> { ["RIGHT"] = "/tmp/cache/right.png" });
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<BACKGROUND SRC=room><WIPE EFFECT=RIGHT TIME=5><K>", "wipe.nps", presentation: presentation), builtins);
        Assert.Equal((TimeSpan.FromMilliseconds(500), 0f, 1f, .1f, "/tmp/cache/right.png", TimeSpan.FromMilliseconds(500)), builtins.Transition);
        Assert.Equal("cg/room.png", builtins.Scenes.Single()["legacy_front/background"].Image);
        Assert.DoesNotContain(builtins.Scenes.Single().Keys, key => key.StartsWith("legacy_stage/"));
    }

    [Fact]
    public void AnimUsesSparseIntegerFramesAndStartsSoundBeforeVisualWait()
    {
        string source = "<ANIM SRC=fx_ NUM=8 X=200 Y=-5 START=000 END=003 STILL=003 WAIT=1 SE=cut LOOP=false>"
            + "<BGANIM SRC=fx_ NUM=8 START=001 END=003 STILL=-1 WAIT=0 LOOP=true>";
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate(source, "anim.nps", resolveAsset: (kind, file) =>
            file == "fx_002" ? throw new FormatException("Missing frame") : kind + "/" + file + (kind == "cg" ? ".png" : ".ogg")), builtins);
        Assert.Equal(2, builtins.Animations.Count);
        FrameCall first = builtins.Animations[0], second = builtins.Animations[1];
        Assert.Equal("legacy_front/anim8", first.Path);
        Assert.Equal((208, 200f, -5f, 2, "cg/fx_003.png", false, false, false),
            (first.Priority, first.X, first.Y, first.Ticks, first.FinalFrame, first.Remove, first.Loop, first.Wait));
        Assert.Equal(new[] { "cg/fx_000.png", "cg/fx_001.png", "", "cg/fx_003.png" }, first.Frames);
        Assert.Equal(new[] { "cg/fx_001.png", "", "cg/fx_003.png" }, second.Frames);
        Assert.Equal((1, "", false, true, false), (second.Ticks, second.FinalFrame, second.Remove, second.Loop, second.Wait));
        Assert.Equal(new[] { "animation", "sound", "wait", "animation" }, builtins.AnimationEvents);
    }

    [Fact]
    public void AnimStillZeroRemovesAndMissingFirstFrameRemainsExplicitFailure()
    {
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<ANIM SRC=fx_ START=0 END=0 STILL=0>", "anim.nps"), builtins);
        Assert.True(builtins.Animations.Single().Remove);
        Assert.Equal("", builtins.Animations.Single().FinalFrame);
        var missing = LegacyNpsTranslator.Translate("<ANIM SRC=fx_ START=0 END=2>", "missing.nps",
            resolveAsset: (_, _) => throw new FormatException("Missing first frame"));
        Assert.Contains("Missing first frame", missing.Source);
        Assert.DoesNotContain("CreateFrameAnimation", missing.Source);
    }

    [Fact]
    public void BlindRetainsOriginalHardMaskAndFullRequestedTime()
    {
        var presentation = new LegacyNpsPresentation(new Dictionary<int, LegacyNpsTextBox>(),
            new Dictionary<string, int>(), new Dictionary<string, string> { ["BLIND"] = "/tmp/cache/blind.png" });
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<WIPE EFFECT=Blind TIME=10>", "blind.nps", presentation: presentation), builtins);
        Assert.Equal((TimeSpan.FromMilliseconds(1000), 0f, 1f, 0f, "/tmp/cache/blind.png", TimeSpan.FromMilliseconds(1000)), builtins.Transition);
    }

    private static void Execute(LegacyNpsTranslation translation, BuiltInFunctions builtins)
    {
        string root = Path.Combine(Path.GetTempPath(), "legacy-scene-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "boot.nss"), translation.Source, Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("boot.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            var thread = vm.CreateThread("boot", "main")!.Value;
            for (int i = 0; i < 1000 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed record SceneVisual(string Image, float X, float Y, int Priority, float Alpha = 1);
    private sealed record FrameCall(string Path, int Priority, float X, float Y, string[] Frames,
        int Ticks, string FinalFrame, bool Remove, bool Loop, bool Wait);

    private sealed class SceneBuiltins : BuiltInFunctions
    {
        public readonly Dictionary<string, SceneVisual> Visuals = new();
        public readonly List<Dictionary<string, SceneVisual>> Scenes = [];
        public readonly List<(uint, uint)> TextAreas = [];
        public readonly List<FrameCall> Animations = [];
        public readonly List<string> AnimationEvents = [];
        public (float, float, uint, uint) Window;
        public bool PendingPriorityCoveredFront;
        public (TimeSpan, float, float, float, string, TimeSpan) Transition;
        protected override void NotImplemented(string functionName = "") { }
        public override void CreateSprite(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y, string source)
            => Visuals[path.ToString()] = new(source, x.Value.pos, y.Value.pos, priority);
        public override void CreateRectangle(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y, uint width, uint height, NsColor color)
            => Visuals[path.ToString()] = new("", x.Value.pos, y.Value.pos, priority);
        public override void DestroyEntities(in EntityQuery query)
        {
            string text = query.ToString(), prefix = text.TrimEnd('*').TrimEnd('/');
            foreach (string key in Visuals.Keys.Where(key => key == prefix || key.StartsWith(prefix + "/")).ToArray()) Visuals.Remove(key);
        }
        public override void Fade(in EntityQuery query, TimeSpan duration, NsRational opacity, NsEaseFunction ease, TimeSpan wait)
        {
            string key = query.ToString();
            if (Visuals.TryGetValue(key, out SceneVisual? item)) Visuals[key] = item with { Alpha = opacity };
        }
        public override void Wait(TimeSpan delay)
        {
            if (Visuals.TryGetValue("legacy_stage/blank", out SceneVisual? stage))
                PendingPriorityCoveredFront |= Visuals.Where(pair => pair.Key.StartsWith("legacy_front/"))
                    .All(pair => pair.Value.Priority < stage.Priority);
        }
        public override void WaitForInput() => Scenes.Add(new(Visuals));
        public override void CreateDialogueBox(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y, uint width, uint height, bool inherit)
            => Window = (x.Value.pos, y.Value.pos, width, height);
        public override void LoadDialogueBlock(in DialogueBlockToken token, uint width, uint height, int letter, int line)
            => TextAreas.Add((width, height));
        public override void BeginTransition(in EntityQuery query, TimeSpan duration, NsRational source, NsRational destination, NsRational feather, NsEaseFunction ease, string mask, TimeSpan wait)
        {
            Assert.Equal("legacy_stage/*", query.ToString());
            Transition = (duration, source, destination, feather, mask, wait);
        }
        public override void CreateFrameAnimation(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y,
            ReadOnlySpan<string> frames, int ticksPerFrame, string finalFrame, bool removeOnCompletion, bool loop, bool wait)
        {
            Animations.Add(new(path.ToString(), priority, x.Value.pos, y.Value.pos, frames.ToArray(), ticksPerFrame, finalFrame, removeOnCompletion, loop, wait));
            AnimationEvents.Add("animation");
        }
        public override void LoadAudio(in EntityPath path, NsAudioKind kind, string fileName) => AnimationEvents.Add("sound");
        public override void WaitFrameAnimation(in EntityQuery query) => AnimationEvents.Add("wait");
    }

    [Fact]
    public void LegacyTextSubstitutionStopsAtRuntimeWithoutCorruptingGeneratedModule()
    {
        var result = LegacyNpsTranslator.Translate("<BOX TYPE=9>Later {legacy_name} page<k>", "later.nps");
        Assert.Contains("Legacy text substitution", result.Source);
        Assert.DoesNotContain("{legacy_name}", result.Source);
        Assert.Empty(NitroSharp.NsScript.Syntax.SyntaxTree.ParseText(SourceText.From(result.Source)).Diagnostics);
    }

    [Fact]
    public void OptionalClickSoundFailureKeepsSelectionAndJump()
    {
        var result = LegacyNpsTranslator.Translate("<CHOICE HREF=#END FILE=a.png PUSHSE=click><SELECT><MARKER NAME=#END><END OPTION=CLOSE>", "menu.nps",
            resolveAsset: (kind, path) => path == "click" ? throw new FormatException("Missing sound") : path);
        Assert.Contains("case legacy_choice_0", result.Source);
        Assert.Contains("Exit();", result.Source);
        Assert.DoesNotContain("LegacyUnsupported", result.Source);
        Assert.Contains(result.Diagnostics, note => note.Contains("unavailable optional CHOICE UI sound"));
    }

    [Fact]
    public void FailedCommandRollsBackItsPartialOutput()
    {
        var result = LegacyNpsTranslator.Translate("<CHOICE HREF=#END FILE='one.png;missing.png'><SELECT><MARKER NAME=#END>", "menu.nps",
            resolveAsset: (kind, path) => path == "missing.png" ? throw new FormatException("Missing image") : path);
        Assert.DoesNotContain("CreateChoice", result.Source);
        Assert.DoesNotContain("MouseUsual/image", result.Source);
        Assert.Contains("LegacyUnsupported(\"menu.nps\", 1, \"Missing image\")", result.Source);
        Assert.Empty(NitroSharp.NsScript.Syntax.SyntaxTree.ParseText(NitroSharp.NsScript.SourceText.From(result.Source)).Diagnostics);
    }

    [Fact]
    public void GeneratedMenuRunsAChoiceThenLoadsStoryAudioAndDialogue()
    {
        string root = Path.Combine(Path.GetTempPath(), "legacy-translator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string menu = LegacyNpsTranslator.Translate("<BACKGROUND SRC=#000000><MARKER NAME=#LOOP><CHOICE HREF=#START X=10 Y=20 FILE='a.png;b.png;c.png'><SELECT><MARKER NAME=#START><A HREF=story.nps>", "menu.nps").Source;
            string story = LegacyNpsTranslator.Translate("<BOX TYPE=9><VOICE SRC='speaker/001'>Hello world<k>", "story.nps").Source;
            File.WriteAllText(Path.Combine(root, "menu.nss"), menu, Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "story.nss"), story, Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("menu.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            var thread = vm.CreateThread("menu", "main")!.Value;
            var builtins = new PlaybackBuiltins();
            for (int i = 0; i < 300 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
            Assert.True(builtins.ChoiceSelected);
            Assert.Equal("voice/speaker/001.ogg", builtins.Audio);
            Assert.Equal("page2", builtins.Dialogue);
            Assert.Equal(NsColor.Black, builtins.Background);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class PlaybackBuiltins : BuiltInFunctions
    {
        protected override void NotImplemented(string functionName = "") { }
        public bool ChoiceSelected;
        public string? Audio, Dialogue;
        public NsColor Background;
        public override bool HandleInputEvents(in EntityQuery query)
        {
            bool chosen = query.ToString() == "legacy_choice_2";
            ChoiceSelected |= chosen;
            return chosen;
        }
        public override void LoadAudio(in EntityPath path, NsAudioKind kind, string fileName) => Audio = fileName;
        public override void LoadDialogueBlock(in DialogueBlockToken token, uint width, uint height, int letter, int line)
            => Dialogue = token.BlockName;
        public override void CreateRectangle(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y, uint width, uint height, NsColor color)
            => Background = color;
    }

    [Fact]
    public void MenuBranchesUseNumericProgramCounterAndThreeChoiceStates()
    {
        var translated = LegacyNpsTranslator.Translate("<BACKGROUND SRC=title><MARKER NAME=#LOOP><CHOICE HREF=#START X=10 Y=20 FILE='a.png;b.png;c.png'><SELECT><MARKER NAME=#START><A HREF=story.nps>", "menu.nps");
        Assert.Contains("case legacy_choice_2", translated.Source);
        Assert.Contains("MouseUsual/image", translated.Source);
        Assert.Contains("MouseOver/image", translated.Source);
        Assert.Contains("MouseClick/image", translated.Source);
        Assert.Contains("story.nss->legacy_entry", translated.Source);
        Assert.DoesNotContain("LegacyUnsupported", translated.Source);
    }

    [Fact]
    public void StoryTextUsesActualDialogueBlocksAndAudioBeforeInput()
    {
        var translated = LegacyNpsTranslator.Translate("<BOX TYPE=9><VOICE SRC='speaker/001'>Hello world<k><UNSUPPORTED>", "story.nps");
        Assert.Contains("CreateSound(\"legacy_voice\", Voice, \"voice/speaker/001.ogg\")", translated.Source);
        Assert.Contains("<PRE legacy_box>\n[page2]\nHello world", translated.Source.Replace("\r\n", "\n"));
        Assert.Contains("WaitText(\"legacy_box/page2\", 0)", translated.Source);
        Assert.Contains("LegacyUnsupported(\"story.nps\", 1, \"UNSUPPORTED\")", translated.Source);
    }

    [Fact]
    public void MissingStandardHeaderAndRuntimeFailuresAreReportedSeparately()
    {
        var translated = LegacyNpsTranslator.Translate("<INCLUDE SRC=include.h><SYSTEM CMD=config><UNKNOWN>", "menu.nps");
        Assert.Contains(translated.Diagnostics, value => value.Contains("standard include.h"));
        Assert.Contains("LegacyUnsupported(\"menu.nps\", 1, \"SYSTEM config\")", translated.Source);
        Assert.Contains("LegacyUnsupported(\"menu.nps\", 1, \"UNKNOWN\")", translated.Source);
    }

    [Fact]
    public void LargeScriptsUseBoundedFunctionsRatherThanRecursiveMarkerCalls()
    {
        string source = string.Concat(System.Linq.Enumerable.Repeat("<WAIT TIME=1>", 2000));
        string generated = LegacyNpsTranslator.Translate(source, "large.nps").Source;
        Assert.Contains("step62()", generated);
        Assert.DoesNotContain("call_scene", generated[generated.IndexOf("scene legacy_entry", StringComparison.Ordinal)..]);
    }
}
