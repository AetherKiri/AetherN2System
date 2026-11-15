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
    public void ConfigIsModalAppliesLiveSettingsAndReturnsToTheCallingScript()
    {
        var actions = Enumerable.Repeat("legacy_config_bgm_minus", 20)
            .Concat(Enumerable.Repeat("legacy_config_se_plus", 20))
            .Concat(new[] { "legacy_config_voice_mute", "legacy_config_text_plus", "legacy_config_auto_plus", "legacy_config_close", "legacy_config_close" });
        var builtins = new ConfigBuiltins(actions);
        var translated = LegacyNpsTranslator.Translate("<SYSTEM CMD=config><CALC WORKNAME=after OPERATOR=7><SYSTEM CMD=config>", "config.nps");
        var vm = Execute(translated, builtins);
        Assert.Empty(translated.Diagnostics);
        Assert.DoesNotContain("LegacyUnsupported", translated.Source);
        Assert.Equal(0, vm.SystemVariables.GetAudioSettings(NsAudioKind.BackgroundMusic).Volume!.Value.AsNumber());
        Assert.Equal(1000, vm.SystemVariables.GetAudioSettings(NsAudioKind.SoundEffect).Volume!.Value.AsNumber());
        Assert.False(vm.SystemVariables.GetAudioSettings(NsAudioKind.Voice).Enabled!.Value.AsBool());
        Assert.Equal(40, vm.SystemVariables.GetTextSettings().Interval!.Value.AsNumber());
        Assert.Equal(125, vm.SystemVariables.GetTextSettings().AutoWaitPerCharacter!.Value.AsNumber());
        Assert.Equal(7, vm.DumpVariables().Globals.Single(pair => pair.Item1 == "legacy_work_local_0_AFTER").Item2.AsNumber());
        Assert.Equal(2, builtins.Opened);
        Assert.Equal(2, builtins.Closed);
        Assert.Empty(builtins.Pending);
    }

    [Theory]
    [InlineData("SYSTEM_keydown_esc")]
    [InlineData("SYSTEM_r_button_down")]
    public void ConfigEscapeReturnsWithoutChangingTheSceneOrAudio(string input)
    {
        var translated = LegacyNpsTranslator.Translate("<SYSTEM CMD=config><CALC WORKNAME=after OPERATOR=1>", "escape.nps");
        translated = translated with { Source = translated.Source.Replace("scene legacy_entry {", "scene legacy_entry { $" + input + " = true;") };
        var builtins = new ConfigBuiltins([]);
        var vm = Execute(translated, builtins);
        Assert.Equal(1, builtins.Closed);
        Assert.DoesNotContain(builtins.Deleted, path => path == "legacy_front" || path == "legacy_stage");
        Assert.Equal(1000, vm.SystemVariables.GetAudioSettings(NsAudioKind.BackgroundMusic).Volume!.Value.AsNumber());
        Assert.Equal(1, vm.DumpVariables().Globals.Single(pair => pair.Item1 == "legacy_work_local_0_AFTER").Item2.AsNumber());
    }

    private sealed class ConfigBuiltins(IEnumerable<string> actions) : BuiltInFunctions
    {
        public readonly Queue<string> Pending = new(actions);
        public readonly List<string> Deleted = [];
        public int Opened, Closed;
        protected override void NotImplemented(string functionName = "") { }
        public override void CreateEntity(in EntityPath path) { if (path.ToString() == "legacy_config") Opened++; }
        public override bool HandleInputEvents(in EntityQuery query)
        {
            if (!Pending.TryPeek(out string? next) || query.ToString() != next) return false;
            Pending.Dequeue(); return true;
        }
        public override void DestroyEntities(in EntityQuery query)
        {
            Deleted.Add(query.ToString());
            if (query.ToString() == "legacy_config") Closed++;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextChoiceUsesCountableConfiguredSlotsAndJumpsToEitherBranch(bool second)
    {
        var boxes = new Dictionary<int, LegacyNpsChoiceBox>
        {
            [5] = new(180, 200, 440, 55, Normal: "bar1.png", Over: "bar2.png", Down: "bar3.png"),
            [6] = new(180, 300, 440, 55, Normal: "bar1.png", Over: "bar2.png", Down: "bar3.png")
        };
        string placeholders = string.Concat(Enumerable.Repeat("<CHOICE HREF='' TEXT='' countable=true></A>", 5));
        string source = placeholders + "<CHOICE HREF=#first TEXT='第一个选项'></A><CHOICE HREF=ending.nps TEXT='第二个选项'></A>"
            + "<MARKER NAME=#first><CALC WORKNAME=chosen OPERATOR=1><A HREF=#done><MARKER NAME=#done>";
        var builtins = new TextChoiceBuiltins(second ? "legacy_choice_6" : "legacy_choice_5");
        var presentation = new LegacyNpsPresentation(new Dictionary<int, LegacyNpsTextBox>(), new Dictionary<string, int>(), ChoiceBoxes: boxes);
        var translated = LegacyNpsTranslator.Translate(source, "choices.nps", presentation: presentation);
        var ending = LegacyNpsTranslator.Translate("<CALC WORKNAME=chosen OPERATOR=2>", "ending.nps");
        var vm = Execute(translated, builtins, new Dictionary<string, string> { ["ending.nss"] = ending.Source });
        Assert.Equal(new[] { "legacy_choice_5", "legacy_choice_6" }, builtins.Choices);
        Assert.Equal(6, builtins.Text.Count);
        Assert.Equal(4, builtins.Text.Count(item => item.Contains("color='#FFFF00'")));
        Assert.Contains(builtins.HitBoxes, item => item == ("legacy_choice_5/MouseUsual/hit", 180f, 200f, 440u, 55u));
        Assert.Contains(builtins.HitBoxes, item => item == ("legacy_choice_6/MouseUsual/hit", 180f, 300f, 440u, 55u));
        Assert.Contains(builtins.Text, item => item.Contains("第一个选项"));
        Assert.Contains(builtins.Text, item => item.Contains("第二个选项"));
        Assert.Equal(second ? 2f : 1f, vm.DumpVariables().Globals.Single(pair => pair.Item1 == "legacy_work_local_0_CHOSEN").Item2.AsNumber());
        Assert.Empty(translated.Diagnostics);
    }

    [Fact]
    public void TextChoiceConditionSkipsHiddenButtonAndExplicitSelectDoesNotSelectTwice()
    {
        var presentation = new LegacyNpsPresentation(new Dictionary<int, LegacyNpsTextBox>(), new Dictionary<string, int>(),
            ChoiceBoxes: new Dictionary<int, LegacyNpsChoiceBox> { [0] = new(0, 10, 400, 50) });
        var translation = LegacyNpsTranslator.Translate("<CALC WORKNAME=hidden OPERATOR=0>"
            + "<CHOICE TEXT=hidden HREF=#end OPERATOR='hidden=1'><CHOICE TEXT=visible HREF=#end DISABLE=FALSE><SELECT>"
            + "<MARKER NAME=#end><CALC WORKNAME=done OPERATOR=1>", "conditionchoice.nps", presentation: presentation);
        var builtins = new TextChoiceBuiltins("legacy_choice_2");
        var vm = Execute(translation, builtins);
        Assert.Equal(new[] { "legacy_choice_2" }, builtins.Choices);
        Assert.Equal(3, builtins.Text.Count);
        Assert.Equal(1f, vm.DumpVariables().Globals.Single(pair => pair.Item1 == "legacy_work_local_0_DONE").Item2.AsNumber());
    }

    private sealed class TextChoiceBuiltins(string selected) : BuiltInFunctions
    {
        public readonly List<string> Choices = [], Text = [];
        public readonly List<(string, float, float, uint, uint)> HitBoxes = [];
        protected override void NotImplemented(string functionName = "") { }
        public override void CreateChoice(in EntityPath path, bool preserveOpacity = false) { Assert.True(preserveOpacity); Choices.Add(path.ToString()); }
        public override bool HandleInputEvents(in EntityQuery query) => query.ToString() == selected;
        public override void CreateTextBlock(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y, NsTextDimension width, NsTextDimension height, string markup) => Text.Add(markup);
        public override void CreateRectangle(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y, uint width, uint height, NsColor color)
            => HitBoxes.Add((path.ToString(), x.Value.pos, y.Value.pos, width, height));
    }

    [Theory]
    [InlineData("ANIM", "3", "cg/fx_003.png")]
    [InlineData("BGANIM", "-1", "cg/fx_001.png")]
    public void AnimationStillSurvivesIncrementalWipesAndGlassReusesItsNumber(string command, string still, string image)
    {
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate($"<{command} SRC=fx_ NUM=0 END=1 STILL={still} X=5 Y=8><WIPE EFFECT=NORMAL><K>"
            + "<BACKGROUND SRC=room><WIPE EFFECT=NORMAL><K><GLASS SRC=overlay NUM=0><WIPE EFFECT=NORMAL><K>"
            + "<CLEAR><WIPE EFFECT=NORMAL><K>", "still.nps"), builtins);
        Assert.Equal(image, builtins.Scenes[0]["legacy_front/layer0"].Image);
        Assert.Equal(builtins.Scenes[0]["legacy_front/layer0"], builtins.Scenes[1]["legacy_front/layer0"]);
        Assert.Equal((5f, 8f, 20), (builtins.Scenes[0]["legacy_front/layer0"].X, builtins.Scenes[0]["legacy_front/layer0"].Y, builtins.Scenes[0]["legacy_front/layer0"].Priority));
        Assert.Equal("cg/overlay.png", builtins.Scenes[2]["legacy_front/layer0"].Image);
        Assert.DoesNotContain("legacy_front/layer0", builtins.Scenes[3].Keys);
        Assert.DoesNotContain(builtins.Visuals.Keys, path => path.Contains("/anim"));
    }

    [Fact]
    public void ClearOrGlassDoesNotResurrectAnActiveBackgroundAnimation()
    {
        foreach (string replacement in new[] { "<CLEAR>", "<GLASS NUM=8 MODE=OFF>" })
        {
            var builtins = new SceneBuiltins();
            Execute(LegacyNpsTranslator.Translate("<BGANIM SRC=fx_ NUM=8 END=1 STILL=1>" + replacement + "<WIPE EFFECT=NORMAL><K>", "clear.nps"), builtins);
            Assert.DoesNotContain("legacy_front/layer0", builtins.Scenes.Single().Keys);
        }
    }

    [Fact]
    public void DelayedScrollWaitsForWipeThenMovesStageWithFixedPointAndPersistsEndpoint()
    {
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<GLASS SRC=pan NUM=0><WIPE EFFECT=NORMAL>"
            + "<SCROLL NUM=0 X=@0 Y=@-6 TIME=0 COUNT=0 SILENT=TRUE><SCROLL NUM=0 X=@0 Y=@6 COUNT=3 DELAY=TRUE>"
            + "<WIPE EFFECT=delayfade TIME=20><K><BACKGROUND SRC=room><WIPE EFFECT=NORMAL><K>", "delay.nps"), builtins);
        Assert.Equal(1, builtins.DynamicTransitions);
        Assert.Equal("__legacy_transition_*", builtins.WaitingAction);
        Assert.Equal(new[] { (0f, -6f), (0f, -4f), (0f, -2f), (0f, 0f) }, builtins.AbsoluteMoves.Select(move => (move.X, move.Y)));
        Assert.All(builtins.AbsoluteMoves, move => Assert.Equal("legacy_stage/layer0", move.Query));
        Assert.Equal(315d, builtins.Delays.Sum(value => value.TotalMilliseconds));
        Assert.Equal(0f, builtins.Scenes[0]["legacy_front/layer0"].Y);
        Assert.Equal(builtins.Scenes[0]["legacy_front/layer0"], builtins.Scenes[1]["legacy_front/layer0"]);
    }

    [Fact]
    public void OrdinaryWipeCommitsQueuedScrollTargetWithoutAnimatingIt()
    {
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<GLASS SRC=pan NUM=0><WIPE EFFECT=NORMAL>"
            + "<SCROLL NUM=0 X=@5 COUNT=10 DELAY=TRUE><WIPE EFFECT=NORMAL><K>", "queued.nps"), builtins);
        Assert.Empty(builtins.AbsoluteMoves);
        Assert.Equal(5f, builtins.Scenes.Single()["legacy_front/layer0"].X);
    }

    [Fact]
    public void ScrollAdvanceInputBreaksRemainingStepsAndSnapsExactEndpoint()
    {
        var builtins = new SceneBuiltins();
        var translation = LegacyNpsTranslator.Translate("<GLASS SRC=pan NUM=0><WIPE EFFECT=NORMAL><SCROLL NUM=0 X=-5 COUNT=10 TIME=1><WIPE EFFECT=NORMAL><K>", "skipscroll.nps");
        // Input variable is already down when the synthetic timed wait resumes.
        var withInput = translation with { Source = translation.Source.Replace("scene legacy_entry {", "scene legacy_entry { $SYSTEM_l_button_down = true;") };
        Execute(withInput, builtins);
        Assert.Equal(new[] { (0f, 0f), (-5f, 0f) }, builtins.AbsoluteMoves.Select(move => (move.X, move.Y)));
        Assert.Single(builtins.Delays);
    }

    [Fact]
    public void ColumnarBoxSwapsInnerDimensionsAndRotatesPageAroundConfiguredUpperRight()
    {
        var boxes = new Dictionary<int, LegacyNpsTextBox>
        {
            [2] = new(40, 50, 720, 500, 20, 6, 10, 6, "data/box01.png", 80, true),
            [9] = new(40, 50, 720, 500, 20, 6, 10, 6, "data/box01.png", 80)
        };
        var presentation = new LegacyNpsPresentation(boxes, new Dictionary<string, int>());
        var builtins = new SceneBuiltins();
        var translation = LegacyNpsTranslator.Translate("<BOX TYPE=2>Vertical<k><BOX TYPE=9>Horizontal<k>", "vertical.nps", presentation: presentation);
        Execute(translation, builtins);
        Assert.Equal(new[] { true, false }, builtins.Orientations);
        Assert.Equal(new[] { (488u, 690u), (690u, 488u) }, builtins.TextAreas);
        Assert.Equal(new[] { (760f, 56f, 488u, 700u), (60f, 46f, 690u, 498u) }, builtins.Windows);
        Assert.Equal(("legacy_box/page1", 90f), builtins.PageRotation);
        Assert.Equal(new[] { "legacy_box/page1", "legacy_box/page4" }, builtins.EnteredPages);
        Assert.True(translation.Source.IndexOf("Rotate(\"legacy_box/page1\"", StringComparison.Ordinal) < translation.Source.IndexOf("Request(\"legacy_box/page1\", Enter)", StringComparison.Ordinal));
        Assert.DoesNotContain("LegacyUnsupported", translation.Source);
    }

    [Fact]
    public void OptionalSystemCapabilitiesAreDiagnosedWithoutBlockingDialogue()
    {
        var translation = LegacyNpsTranslator.Translate("<SYSTEM MENU=#006><SYSTEM CMD=savemode MODE=TRUE><SYSTEM CMD=timerclear><BOX TYPE=9>Continued<k>", "system.nps");
        var vm = Execute(translation, new SceneBuiltins());
        Assert.Equal(3, translation.Diagnostics.Count(value => value.Contains("optional SYSTEM")));
        Assert.Contains(translation.Diagnostics, value => value.Contains("host menu controls are not connected"));
        Assert.Contains(translation.Diagnostics, value => value.Contains("host storage is not connected"));
        Assert.Contains(translation.Diagnostics, value => value.Contains("host play-time tracking is not connected"));
        Assert.True(vm.DumpVariables().Globals.Single(pair => pair.Item1 == "legacy_system_suppress_choice_autosave").Item2.AsBool());
        Assert.DoesNotContain("legacy_system_save_enabled", translation.Source);
        Assert.DoesNotContain("LegacyUnsupported", translation.Source);
    }

    [Theory]
    [InlineData(false, false, 10, 10)]
    [InlineData(true, false, 10, 10)]
    [InlineData(true, true, 40, 26)]
    public void HorizontalEndingRollUsesNativePixelStepsMusicIntervalAndSkipFade(bool allowSkip, bool input, int width, int expectedMoves)
    {
        var builtins = new SceneBuiltins { MediaDuration = width * 20, ImageWidth = width };
        string skip = allowSkip ? "true" : "false";
        var translation = LegacyNpsTranslator.Translate("<SYSTEM CMD=ending TURN=true ROLL=credits SKIP=" + skip + ">", "ending.nps");
        if (input) translation = translation with { Source = translation.Source.Replace("scene legacy_entry {", "scene legacy_entry { $SYSTEM_l_button_down = true;") };
        Execute(translation, builtins);
        Assert.Equal(expectedMoves, builtins.AbsoluteMoves.Count);
        Assert.Equal((0f - width, 0f), (builtins.AbsoluteMoves[0].X, builtins.AbsoluteMoves[0].Y));
        Assert.All(builtins.AbsoluteMoves, move => Assert.Equal("legacy_credits", move.Query));
        Assert.All(builtins.Delays.Where(delay => delay.TotalMilliseconds < 5000), delay => Assert.Equal(20d, delay.TotalMilliseconds));
        Assert.Equal(allowSkip && input ? 0 : 1, builtins.Delays.Count(delay => delay.TotalMilliseconds == 5000));
        Assert.DoesNotContain("legacy_credits", builtins.Visuals.Keys);
    }

    [Fact]
    public void EndingRollWithoutMusicFallsBackToTwentyMilliseconds()
    {
        var builtins = new SceneBuiltins { ImageWidth = 2 };
        Execute(LegacyNpsTranslator.Translate("<SYSTEM CMD=ending TURN=TRUE ROLL=credits>", "ending.nps"), builtins);
        Assert.Equal(new[] { 20d, 20d, 5000d }, builtins.Delays.Select(delay => delay.TotalMilliseconds));
    }

    [Fact]
    public void MissingWipeMaskKeepsPreviousSceneForQuantizedNativeWaitThenCommits()
    {
        var builtins = new SceneBuiltins();
        var translated = LegacyNpsTranslator.Translate("<BACKGROUND SRC=old><WIPE EFFECT=NORMAL>"
            + "<BACKGROUND SRC=new><WIPE EFFECT=missing TIME=10><K>", "missingmask.nps",
            resolveAsset: (kind, path) => kind == "wipe" ? "" : "cg/" + path + ".png");
        Execute(translated, builtins);
        Assert.Equal(TimeSpan.FromMilliseconds(896), builtins.Delays.Single());
        Assert.Empty(builtins.LegacyTransitions);
        Assert.Equal("cg/old.png", builtins.DelayScenes.Single()["legacy_front/background"].Image);
        Assert.Equal("cg/new.png", builtins.Scenes.Single()["legacy_front/background"].Image);
        Assert.Contains(translated.Diagnostics, value => value.Contains("missing authored WIPE mask 'missing'"));
    }

    [Fact]
    public void WorkVariablesCrossModulesAndInitClearsLocalsWhilePreservingGlobals()
    {
        var first = LegacyNpsTranslator.Translate("<CALC WORKNAME=shared OPERATOR=7><CALC WORKNAME=score OPERATOR=9 MODE=global><A HREF=next.nps>", "first.nps");
        var next = LegacyNpsTranslator.Translate("<A HREF=#shared OPERATOR='shared=7'><BAD_SHARED><MARKER NAME=#shared>"
            + "<SYSTEM INIT=TRUE><A HREF=#cleared OPERATOR='shared=0'><BAD_INIT><MARKER NAME=#cleared>"
            + "<CALC WORKNAME=SCORE OPERATOR=+1><A HREF=#global OPERATOR='score=10'><BAD_GLOBAL><MARKER NAME=#global>", "next.nps");
        var vm = Execute(first, new SceneBuiltins(), new Dictionary<string, string> { ["next.nss"] = next.Source });
        Assert.Equal(10f, vm.DumpFlags().Globals.Single(pair => pair.Item1 == "legacy_work_global_SCORE").Item2.AsNumber());
    }

    [Fact]
    public void CalculationUsesOriginalLeftToRightIntegerArithmeticAndSingleBangComparison()
    {
        var translated = LegacyNpsTranslator.Translate("<CALC WORKNAME=points OPERATOR=10><CALC WORKNAME=points OPERATOR='points+2*3/5'>"
            + "<CALC WORKNAME=points OPERATOR=-1><A HREF=#ok OPERATOR='points!5'><BAD_VALUE><MARKER NAME=#ok>"
            + "<A HREF=#done OPERATOR='points=6'><MARKER NAME=#done>", "calc.nps");
        // ((10+2)*3)/5 truncates to7, then the leading-1 subtracts from
        // the target's current value, leaving6. A!B is native inequality.
        var vm = Execute(translated, new SceneBuiltins());
        Assert.Equal(6f, vm.DumpVariables().Globals.Single(pair => pair.Item1 == "legacy_work_local_0_POINTS").Item2.AsNumber());
    }

    [Fact]
    public void ScrollUsesFixedPointStepsAndPersistsExactEndpointForLaterCommit()
    {
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<GLASS SRC=pan NUM=0><WIPE EFFECT=NORMAL>"
            + "<SCROLL NUM=0 X=-5 Y=4 COUNT=3 TIME=1><SCROLL NUM=0 X=@2 Y=@-1 COUNT=0>"
            + "<WIPE EFFECT=NORMAL><K>", "scroll.nps"), builtins);
        Assert.Equal(new[] { (0f, 0f), (-2f, 1f), (-4f, 2f), (-5f, 4f), (-3f, 3f) }, builtins.AbsoluteMoves.Select(move => (move.X, move.Y)));
        Assert.Equal(new[] { 33d, 33d, 33d }, builtins.Delays.Where(value => value > TimeSpan.Zero).Select(value => value.TotalMilliseconds));
        Assert.Equal((-3f, 3f), (builtins.Scenes.Single()["legacy_front/layer0"].X, builtins.Scenes.Single()["legacy_front/layer0"].Y));
    }

    [Fact]
    public void SilentScrollUpdatesPendingDescriptorWithoutMovingDisplayedLayer()
    {
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<GLASS SRC=pan NUM=0><WIPE EFFECT=NORMAL>"
            + "<SCROLL NUM=0 X=11 Y=7 COUNT=0 TIME=0 SILENT=TRUE><WIPE EFFECT=NORMAL><K>", "silent.nps"), builtins);
        Assert.Empty(builtins.AbsoluteMoves);
        Assert.Equal((11f, 7f), (builtins.Scenes.Single()["legacy_front/layer0"].X, builtins.Scenes.Single()["legacy_front/layer0"].Y));
    }

    [Fact]
    public void SceneChangeStopsSkipAndFinalizesAnimationsWithoutClearingTheScene()
    {
        var builtins = new MediaBuiltins();
        var translated = LegacyNpsTranslator.Translate("<BACKGROUND SRC=room><WIPE EFFECT=NORMAL><SYSTEM CMD=sceneChange>", "boundary.nps");
        Execute(translated, builtins);
        Assert.Contains("$SYSTEM_skip = false", translated.Source);
        Assert.Contains("finish_animations();", translated.Source);
        Assert.DoesNotContain("LegacyUnsupported", translated.Source);
    }

    [Fact]
    public void MissingVoiceStopsPreviousChannelAndContinuesWithoutCreatingEmptyAudio()
    {
        var translated = LegacyNpsTranslator.Translate("<VOICE SRC=existing><VOICE SRC=absent><BOX TYPE=9>Continued<k>", "voice.nps",
            resolveAsset: (_, path) => path == "absent" ? "" : "voice/existing.ogg");
        string source = translated.Source;
        Assert.Equal(2, source.Split("Request(\"legacy_voice\", Stop)").Length - 1);
        Assert.Equal(1, source.Split("CreateSound(").Length - 1);
        Assert.Contains("voice/existing.ogg", source);
        Assert.Contains("Continued", source);
        Assert.DoesNotContain("LegacyUnsupported", source);
        Assert.Contains(translated.Diagnostics, value => value.Contains("voice.nps:1: missing authored VOICE 'absent'"));
        var builtins = new MediaBuiltins();
        Execute(translated, builtins);
        Assert.Equal(new[] { "stop:legacy_voice", "audio:voice/existing.ogg", "play:legacy_voice", "stop:legacy_voice", "enable:legacy_box/page3" }, builtins.Events);
    }

    [Fact]
    public void NonMissingVoiceResolutionFailureRemainsExplicit()
    {
        var translated = LegacyNpsTranslator.Translate("<VOICE SRC=broken>", "voice.nps",
            resolveAsset: (_, _) => throw new FormatException("Malformed voice resource"));
        Assert.Contains("LegacyUnsupported", translated.Source);
        Assert.Contains("Malformed voice resource", translated.Source);
        Assert.DoesNotContain(translated.Diagnostics, value => value.Contains("continues silently"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(" SKIP=off", false)]
    [InlineData(" SKIP=ON", true)]
    public void MovieCentersNativeFramesAndWaitsWithAuthoredSkipPolicy(string attributes, bool allowSkip)
    {
        var builtins = new MediaBuiltins();
        var translated = LegacyNpsTranslator.Translate("<MOVIE SRC=opening SENTERX=12 SENTERY=-6" + attributes + "><WAIT TIME=1>", "movie.nps");
        Execute(translated, builtins);
        Assert.Equal(("legacy_movie", 11000, NsAlignment.Center, NsAlignment.Center, false, false, "movie/opening.mpg"), builtins.Movie);
        Assert.Equal(("legacy_movie", 12f, -6f), builtins.MovieMove);
        Assert.Equal(("legacy_movie", allowSkip), builtins.MovieWait);
        Assert.Equal(new[] { "movie", "move", "wait", "delete:legacy_movie" }, builtins.Events);
        Assert.DoesNotContain("LegacyUnsupported", translated.Source);
    }

    private sealed class MediaBuiltins : BuiltInFunctions
    {
        public readonly List<string> Events = [];
        public (string, int, NsAlignment, NsAlignment, bool, bool, string) Movie;
        public (string, float, float) MovieMove;
        public (string, bool) MovieWait;
        protected override void NotImplemented(string functionName = "") { }
        public override void LoadAudio(in EntityPath path, NsAudioKind kind, string fileName) => Events.Add("audio:" + fileName);
        public override void Request(in EntityQuery query, NsEntityAction action) => Events.Add(action.ToString().ToLowerInvariant() + ":" + query.ToString());
        public override void PlayVideo(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y, bool loop, bool alpha, string source)
        {
            Assert.Equal((NsCoordinateKind.Alignment, NsCoordinateKind.Alignment), (x.Kind, y.Kind));
            Movie = (path.ToString(), priority, x.Alignment, y.Alignment, loop, alpha, source);
            Events.Add("movie");
        }
        public override void Move(in EntityQuery query, TimeSpan duration, NsCoordinate x, NsCoordinate y, NsEaseFunction ease, TimeSpan wait)
        {
            Assert.Equal(TimeSpan.Zero, duration);
            Assert.True(x.Value.isRelative && y.Value.isRelative);
            MovieMove = (query.ToString(), x.Value.pos, y.Value.pos);
            Events.Add("move");
        }
        public override void WaitPlay(in EntityQuery query, bool allowSkip)
        {
            MovieWait = (query.ToString(), allowSkip);
            Events.Add("wait");
        }
        public override void DestroyEntities(in EntityQuery query)
        {
            if (query.ToString() == "legacy_movie") Events.Add("delete:legacy_movie");
        }
    }

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
    public void AuthoredWipeUsesLegacyCumulativeMaskBeforeCommitting()
    {
        var presentation = new LegacyNpsPresentation(new Dictionary<int, LegacyNpsTextBox>(),
            new Dictionary<string, int>(), new Dictionary<string, string> { ["RIGHT"] = "/tmp/cache/right.png" });
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<BACKGROUND SRC=room><WIPE EFFECT=RIGHT TIME=5><K>", "wipe.nps", presentation: presentation), builtins);
        Assert.Equal(("legacy_stage/*", TimeSpan.FromMilliseconds(500), "/tmp/cache/right.png", true, TimeSpan.FromMilliseconds(500)), builtins.LegacyTransitions.Single());
        Assert.Equal("cg/room.png", builtins.Scenes.Single()["legacy_front/background"].Image);
        Assert.DoesNotContain(builtins.Scenes.Single().Keys, key => key.StartsWith("legacy_stage/"));
    }

    [Theory]
    [InlineData("WHITEOUT")]
    [InlineData("BLACKOUT")]
    public void ColorWipeRunsBothFullDurationPhasesAndDeletesBridgeAfterCommit(string effect)
    {
        var builtins = new SceneBuiltins();
        var translated = LegacyNpsTranslator.Translate("<BACKGROUND SRC=room><WIPE EFFECT=" + effect + " TIME=20><K>", "colorwipe.nps");
        Execute(translated, builtins);
        Assert.Equal(2, builtins.LegacyTransitions.Count);
        Assert.Equal(("legacy_wipe_bridge", TimeSpan.FromMilliseconds(2000), "", false, TimeSpan.FromMilliseconds(2000)), builtins.LegacyTransitions[0]);
        Assert.Equal(("legacy_stage/*", TimeSpan.FromMilliseconds(2000), "", false, TimeSpan.FromMilliseconds(2000)), builtins.LegacyTransitions[1]);
        Assert.Equal("cg/room.png", builtins.Scenes.Single()["legacy_front/background"].Image);
        Assert.DoesNotContain("legacy_wipe_bridge", builtins.Scenes.Single().Keys);
        Assert.Contains(effect == "WHITEOUT" ? "#FFFFFF" : "#000000", translated.Source);
    }

    [Fact]
    public void FadeUsesCumulativeUniformBlendAndOriginalDefaultTime()
    {
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<WIPE EFFECT=FADE>", "fade.nps"), builtins);
        Assert.Equal(("legacy_stage/*", TimeSpan.FromMilliseconds(1600), "", false, TimeSpan.FromMilliseconds(1600)), builtins.LegacyTransitions.Single());
    }

    [Fact]
    public void ZeroTimeCommitsWithoutLoadingAnEffectMask()
    {
        var builtins = new SceneBuiltins();
        var translated = LegacyNpsTranslator.Translate("<WIPE EFFECT=absent TIME=0>", "instant.nps",
            resolveAsset: (_, _) => throw new FormatException("Missing mask"));
        Execute(translated, builtins);
        Assert.Empty(builtins.LegacyTransitions);
        Assert.DoesNotContain("LegacyUnsupported", translated.Source);
    }

    [Fact]
    public void QuakeMovesOnlyCommittedSceneInFourRelativePhasesAndReturnsToOrigin()
    {
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<QUAKE X='' Y=60 COUNT=4>", "quake.nps"), builtins);
        Assert.Equal(16, builtins.Moves.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(800), TimeSpan.FromTicks(builtins.Delays.Sum(delay => delay.Ticks)));
        Assert.All(builtins.Moves, move => Assert.Equal("legacy_front/*", move.Query));
        Assert.Equal(new[] { 60f, -60f, -60f, 60f }, builtins.Moves.Take(4).Select(move => move.Y));
        Assert.Equal(0, builtins.Moves.Sum(move => move.X));
        Assert.Equal(0, builtins.Moves.Sum(move => move.Y));
    }

    [Theory]
    [InlineData("X=NaN Y=2 COUNT=1")]
    [InlineData("X=1 Y=Infinity COUNT=1")]
    [InlineData("X=1 Y=2 COUNT=1025")]
    [InlineData("X=1 Y=2 COUNT=-1")]
    [InlineData("X=1 Y=2 COUNT=1 TIME=-1")]
    public void InvalidQuakeParametersRemainExplicitFailures(string attributes)
    {
        var translated = LegacyNpsTranslator.Translate("<QUAKE " + attributes + ">", "invalidquake.nps");
        Assert.Contains("LegacyUnsupported", translated.Source);
        Assert.DoesNotContain("Move(", translated.Source);
    }

    [Fact]
    public void GlassSlotsPersistPositionOpacityAndZoomAcrossSceneCommitsAndRespectNativeOrder()
    {
        const string source = "<BACKGROUND SRC=room><BUSTUP NAME=A FACE=normal MODE=on>"
            + "<GLASS NUM=0 SRC=under X=10 Y=20 TRANSPARENT=50 ZOOM=50><WIPE EFFECT=NORMAL><K>"
            + "<BUSTUP NAME=A FACE=happy><GLASS NUM=1 SRC=over TRANSPARENT=25><WIPE EFFECT=FADE TIME=1><K>"
            + "<GLASS NUM=0 MODE=off><WIPE EFFECT=NORMAL><K><CLEAR><WIPE EFFECT=NORMAL><K>";
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate(source, "glass.nps"), builtins);
        Assert.Equal(4, builtins.Scenes.Count);
        SceneVisual under = builtins.Scenes[0]["legacy_front/layer0"];
        Assert.Equal(("cg/under.png", 10f, 20f, .5f, .5f), (under.Image, under.X, under.Y, under.Alpha, under.ScaleX));
        Assert.True(under.Priority > builtins.Scenes[0]["legacy_front/background"].Priority);
        Assert.True(under.Priority < builtins.Scenes[0]["legacy_front/actor0"].Priority);
        Assert.Equal(under, builtins.Scenes[1]["legacy_front/layer0"]);
        SceneVisual over = builtins.Scenes[1]["legacy_front/layer1"];
        Assert.Equal(.25f, over.Alpha);
        Assert.True(over.Priority > builtins.Scenes[1]["legacy_front/actor0"].Priority);
        Assert.DoesNotContain("legacy_front/layer0", builtins.Scenes[2].Keys);
        Assert.Contains("legacy_front/layer1", builtins.Scenes[2].Keys);
        Assert.DoesNotContain(builtins.Scenes[3].Values, visual => visual.Image.Length > 0);
    }

    [Fact]
    public void GlassOnUsesOriginalDefaultsAndLayerSharesItsNumberedSlot()
    {
        var builtins = new SceneBuiltins();
        Execute(LegacyNpsTranslator.Translate("<GLASS SRC=first NUM=1 X=7 Y=9 ZOOM=50 TRANSPARENT=20><WIPE EFFECT=NORMAL><K>"
            + "<LAYER SRC=second NUM=01 X='' Y=''><WIPE EFFECT=NORMAL><K>", "glassdefaults.nps"), builtins);
        SceneVisual second = builtins.Scenes[1]["legacy_front/layer0"];
        Assert.Equal(("cg/second.png", 0f, 0f, 1f, 1f), (second.Image, second.X, second.Y, second.Alpha, second.ScaleX));
        Assert.DoesNotContain("legacy_front/layer1", builtins.Scenes[1].Keys);
    }

    [Theory]
    [InlineData("NUM=20")]
    [InlineData("NUM=-1")]
    [InlineData("ZOOM=120")]
    [InlineData("ZOOMTYPE=2")]
    [InlineData("SHADE=1")]
    [InlineData("REVERSE=ON")]
    [InlineData("TRANSPARENT=101")]
    public void UnverifiedGlassVariantsRemainExplicitFailures(string attributes)
    {
        var translated = LegacyNpsTranslator.Translate("<GLASS SRC=image " + attributes + ">", "unsupportedglass.nps");
        Assert.Contains("LegacyUnsupported", translated.Source);
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
        Assert.Equal("legacy_front/layer0", first.Path);
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

    private static NsScriptVM Execute(LegacyNpsTranslation translation, BuiltInFunctions builtins,
        IReadOnlyDictionary<string, string>? additionalModules = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "legacy-scene-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "boot.nss"), translation.Source, Encoding.UTF8);
            if (additionalModules is not null)
                foreach (var (name, source) in additionalModules) File.WriteAllText(Path.Combine(root, name), source, Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit(Directory.GetFiles(root, "*.nss").Select(path => compilation.GetSourceModule(Path.GetFileName(path))).ToArray(), root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            var thread = vm.CreateThread("boot", "main")!.Value;
            for (int i = 0; i < 1000 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
            return vm;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed record SceneVisual(string Image, float X, float Y, int Priority, float Alpha = 1, float ScaleX = 1, float ScaleY = 1);
    private sealed record FrameCall(string Path, int Priority, float X, float Y, string[] Frames,
        int Ticks, string FinalFrame, bool Remove, bool Loop, bool Wait);

    private sealed class SceneBuiltins : BuiltInFunctions
    {
        public readonly List<string> EnteredPages = [];
        public override void Request(in EntityQuery query, NsEntityAction action) { if (action == NsEntityAction.Enable) EnteredPages.Add(query.ToString()); }
        public override void WaitText(in EntityQuery query, TimeSpan timeout) => Assert.Contains(query.ToString(), EnteredPages);
        public readonly Dictionary<string, SceneVisual> Visuals = new();
        public readonly List<Dictionary<string, SceneVisual>> Scenes = [];
        public readonly List<(uint, uint)> TextAreas = [];
        public readonly List<FrameCall> Animations = [];
        private readonly Dictionary<string, FrameCall> _activeAnimations = new();
        public readonly List<string> AnimationEvents = [];
        public readonly List<(string, TimeSpan, string, bool, TimeSpan)> LegacyTransitions = [];
        public readonly List<(string Query, float X, float Y)> Moves = [];
        public readonly List<(string Query, float X, float Y)> AbsoluteMoves = [];
        public readonly List<TimeSpan> Delays = [];
        public int DynamicTransitions;
        public int MediaDuration, ImageWidth;
        public override int GetMediaDuration(in EntityQuery query) => MediaDuration;
        public override int GetWidth(in EntityQuery query) => ImageWidth;
        public string? WaitingAction;
        public override void WaitForInput(TimeSpan timeout) { Wait(timeout); }
        public override void WaitAction(in EntityQuery query, TimeSpan? timeout) => WaitingAction = query.ToString();
        public readonly List<Dictionary<string, SceneVisual>> DelayScenes = [];
        public (float, float, uint, uint) Window;
        public readonly List<(float, float, uint, uint)> Windows = [];
        public readonly List<bool> Orientations = [];
        public (string, float) PageRotation;
        public override void SetTextOrientation(bool vertical) => Orientations.Add(vertical);
        public override void Rotate(in EntityQuery query, TimeSpan duration, NsNumeric x, NsNumeric y, NsNumeric z, NsEaseFunction ease, TimeSpan wait)
        {
            Assert.Equal(TimeSpan.Zero, duration);
            Assert.Equal((0f, 0f), (x.Value, y.Value));
            PageRotation = (query.ToString(), z.Value);
        }
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
            Delays.Add(delay);
            DelayScenes.Add(new(Visuals));
            if (Visuals.TryGetValue("legacy_stage/blank", out SceneVisual? stage))
                PendingPriorityCoveredFront |= Visuals.Where(pair => pair.Key.StartsWith("legacy_front/"))
                    .All(pair => pair.Value.Priority < stage.Priority);
        }
        public override void Move(in EntityQuery query, TimeSpan duration, NsCoordinate x, NsCoordinate y, NsEaseFunction ease, TimeSpan wait)
        {
            Assert.Equal(TimeSpan.Zero, duration);
            if (x.Value.isRelative && y.Value.isRelative) Moves.Add((query.ToString(), x.Value.pos, y.Value.pos));
            else
            {
                Assert.False(x.Value.isRelative || y.Value.isRelative);
                AbsoluteMoves.Add((query.ToString(), x.Value.pos, y.Value.pos));
                if (Visuals.TryGetValue(query.ToString(), out SceneVisual? item))
                    Visuals[query.ToString()] = item with { X = x.Value.pos, Y = y.Value.pos };
            }
        }
        public override void Zoom(in EntityQuery query, TimeSpan duration, NsRational x, NsRational y, NsEaseFunction ease, TimeSpan wait)
        {
            Assert.Equal(TimeSpan.Zero, duration);
            if (Visuals.TryGetValue(query.ToString(), out SceneVisual? item))
                Visuals[query.ToString()] = item with { ScaleX = x, ScaleY = y };
        }
        public override void SetVertex(in EntityQuery query, NsCoordinate x, NsCoordinate y)
            => Assert.Equal((0f, 0f), (x.Value.pos, y.Value.pos));
        public override void WaitForInput() => Scenes.Add(new(Visuals));
        public override void CreateDialogueBox(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y, uint width, uint height, bool inherit)
            { Window = (x.Value.pos, y.Value.pos, width, height); Windows.Add(Window); }
        public override void LoadDialogueBlock(in DialogueBlockToken token, uint width, uint height, int letter, int line)
            => TextAreas.Add((width, height));
        public override void BeginTransition(in EntityQuery query, TimeSpan duration, NsRational source, NsRational destination, NsRational feather, NsEaseFunction ease, string mask, TimeSpan wait)
        {
            Assert.Equal("legacy_stage/*", query.ToString());
            Transition = (duration, source, destination, feather, mask, wait);
        }
        public override void BeginLegacyTransition(in EntityQuery query, TimeSpan duration, string mask, bool masked, TimeSpan wait, bool dynamic = false)
        {
            LegacyTransitions.Add((query.ToString(), duration, mask, masked, wait));
            if (dynamic) DynamicTransitions++; else Wait(duration);
        }
        public override void CreateFrameAnimation(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y,
            ReadOnlySpan<string> frames, int ticksPerFrame, string finalFrame, bool removeOnCompletion, bool loop, bool wait)
        {
            Animations.Add(new(path.ToString(), priority, x.Value.pos, y.Value.pos, frames.ToArray(), ticksPerFrame, finalFrame, removeOnCompletion, loop, wait));
            _activeAnimations[path.ToString()] = Animations[^1];
            AnimationEvents.Add("animation");
        }
        public override void LoadAudio(in EntityPath path, NsAudioKind kind, string fileName) => AnimationEvents.Add("sound");
        public override void WaitFrameAnimation(in EntityQuery query) => AnimationEvents.Add("wait");
        public override string? FinishFrameAnimation(in EntityQuery query)
        {
            if (!_activeAnimations.Remove(query.ToString(), out FrameCall? animation)) return null;
            if (animation.Remove) { Visuals.Remove(query.ToString()); return ""; }
            string source = animation.FinalFrame.Length > 0 ? animation.FinalFrame : animation.Frames.Last(frame => frame.Length > 0);
            Visuals[query.ToString()] = new(source, animation.X, animation.Y, animation.Priority);
            return source;
        }
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
        Assert.Contains("Request(\"legacy_box/page2\", Enter); WaitText(\"legacy_box/page2\", 0)", translated.Source);
        Assert.Contains("LegacyUnsupported(\"story.nps\", 1, \"UNSUPPORTED\")", translated.Source);
    }

    [Fact]
    public void MissingStandardHeaderAndRuntimeFailuresAreReportedSeparately()
    {
        var translated = LegacyNpsTranslator.Translate("<INCLUDE SRC=include.h><SYSTEM CMD=config><UNKNOWN>", "menu.nps");
        Assert.Contains(translated.Diagnostics, value => value.Contains("standard include.h"));
        Assert.DoesNotContain("SYSTEM config\")", translated.Source);
        Assert.Contains("Settings</FONT>", translated.Source);
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
