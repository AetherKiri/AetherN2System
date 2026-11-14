using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Syntax;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class DynamicValueExecutionTests
{
    [Fact]
    public void PrintfFormattingUsesInvariantWidthPrecisionAndEscapes()
    {
        var builtins = Execute("""
            chapter main {
                CreateChoice(String("item%02d", 3));
                CreateChoice(String("%s-%04d-%.2f-%%-%X", "label", -7, 1.25, 255));
                CreateChoice(String("%d/%d", 1.9, -1.9));
                CreateChoice(String("item%02", 3));
                CreateChoice(String("missing%02"));
                CreateChoice(String("trailing%", 8));
                CreateChoice(String("unsupported%q", 8));
            }
            """);
        Assert.Equal(new[] { "item03", "label--007-1.25-%-FF", "1/-1", "item03", "missing%02", "trailing%", "unsupported%q" }, builtins.Choices);
        Assert.Equal(new[] { "missing%02", "trailing%", "unsupported%q" }, builtins.InvalidFormats);
    }

    [Fact]
    public void VariableValueReadsAndWritesTypedGlobalsAndIndexedNodes()
    {
        Execute("""
            chapter main {
                $local = 12;
                #local = "flag";
                assert(VariableValue($, "local") == 12);
                assert(VariableValue(#, "local") == "flag");
                VariableValue($, "local", 15);
                assert($local == 15);
                Array(#rows, "alpha", "beta");
                AssocArray(#rows, "alpha", "beta");
                Array(#rows[alpha], "value", true);
                assert(VariableValue(#, "rows[0][0]") == "value");
                VariableValue(#, "rows[alpha][1]", false);
                assert(#rows[0][1] == false);
                assert(!VariableValue(#, "rows[0][1]"));
                assert(VariableValue(#, "rows[alpha]") == "alpha");
                assert(Count(VariableValue(#, "rows")) == 2);
                assert(VariableValue($, "missing") == 0);
                VariableValue($, "new" + "Value", "typed");
                assert(VariableValue($, "newValue") == "typed");
                VariableValue($, "newValue", 23);
                assert(VariableValue($, "newValue") == 23);
                VariableValue(#, "newValue", true);
                assert(VariableValue(#, "newValue") == true);
                VariableValue(#, "created[named][2]", 17);
                assert(VariableValue(#, "created[named][2]") == 17);
                $target = "child.nss->entry";
                call_scene $target;
            }
            """, """
            scene entry {
                assert($newValue == 23);
                assert(#newValue == true);
                assert(#created[named][2] == 17);
                $newValue += 2;
                assert(VariableValue($, "newValue") == 25);
            }
            """);
    }

    [Fact]
    public void DynamicSystemGlobalsRefreshRetainedLookupAndAudioSettings()
    {
        TestBuiltins builtins = Execute("""
            chapter main {
                VariableValue($, "SYSTEM_" + "text_auto", 1);
                VariableValue(#, "SYSTEM_sound_" + "bgm", false);
                VariableValue(#, "SYSTEM_sound_volume_" + "bgm", 375);
                VariableValue(#, "SYSTEM_sound_" + "voice", true);
                VariableValue(#, "SYSTEM_sound_volume_" + "voice", 800);
            }
            """);
        Assert.Equal(1, builtins.SystemVariables.TextAuto.AsNumber());
        var bgm = builtins.SystemVariables.GetAudioSettings(NsAudioKind.BackgroundMusic);
        Assert.False(bgm.Enabled!.Value.AsBool());
        Assert.Equal(375, bgm.Volume!.Value.AsNumber());
        var voice = builtins.SystemVariables.GetAudioSettings(NsAudioKind.Voice);
        Assert.True(voice.Enabled!.Value.AsBool());
        Assert.Equal(800, voice.Volume!.Value.AsNumber());
        var soundEffects = builtins.SystemVariables.GetAudioSettings(NsAudioKind.SoundEffect);
        Assert.Null(soundEffects.Enabled);
        Assert.Null(soundEffects.Volume);
    }

    [Fact]
    public void TextSettingsUseTypedCachedSlotsAndRefreshAfterDynamicGlobals()
    {
        TestBuiltins builtins = Execute("""
            chapter main {
                #SYSTEM_text_interval = 999;
                $SYSTEM_text_speed = 999;
                #SYSTEM_text_waitkey = true;
                $SYSTEM_auto_wait_per_character = 999;
                VariableValue($, "SYSTEM_text_" + "interval", 48);
                VariableValue(#, "SYSTEM_text_" + "speed", 200);
                VariableValue($, "SYSTEM_text_" + "waitkey", false);
                VariableValue(#, "SYSTEM_auto_wait_" + "per_character", 100);
            }
            """, beforeStartup: vm =>
            {
                var missing = vm.SystemVariables.GetTextSettings();
                Assert.Null(missing.Interval);
                Assert.Null(missing.Speed);
                Assert.Null(missing.WaitKey);
                Assert.Null(missing.AutoWaitPerCharacter);
            });
        var settings = builtins.SystemVariables.GetTextSettings();
        Assert.Equal(48, settings.Interval!.Value.AsNumber());
        Assert.Equal(200, settings.Speed!.Value.AsNumber());
        Assert.False(settings.WaitKey!.Value.AsBool());
        Assert.Equal(100, settings.AutoWaitPerCharacter!.Value.AsNumber());
    }

    [Fact]
    public void AudioFlagsHaveDefaultsBeforeStartupAndKeepExplicitMuteAcrossNewGlobals()
    {
        TestBuiltins builtins = Execute("""
            chapter main {
                assert(#SYSTEM_sound_bgm == true);
                assert(#SYSTEM_sound_volume_bgm == 1000);
                assert(#SYSTEM_sound_volume_bgm_default == 1000);
                #SYSTEM_sound_bgm = false;
                #SYSTEM_sound_volume_bgm = 0;
                $target = "child.nss->entry";
                call_scene $target;
                assert(#SYSTEM_sound_bgm == false);
                assert(#SYSTEM_sound_volume_bgm == 0);
            }
            """, """
            scene entry {
                assert(#SYSTEM_sound_se == true);
                assert(#SYSTEM_sound_volume_se == 1000);
                assert(#SYSTEM_sound_volume_se_default == 1000);
                assert(#SYSTEM_sound_voice == true);
                assert(#SYSTEM_sound_volume_voice == 1000);
                assert(#SYSTEM_sound_volume_voice_default == 1000);
            }
            """, beforeStartup: vm =>
            {
                var settings = vm.SystemVariables.GetAudioSettings(NsAudioKind.BackgroundMusic);
                Assert.True(settings.Enabled!.Value.AsBool());
                Assert.Equal(1000, settings.Volume!.Value.AsNumber());
            });
        var bgm = builtins.SystemVariables.GetAudioSettings(NsAudioKind.BackgroundMusic);
        Assert.False(bgm.Enabled!.Value.AsBool());
        Assert.Equal(0, bgm.Volume!.Value.AsNumber());
        foreach (NsAudioKind kind in new[] { NsAudioKind.SoundEffect, NsAudioKind.Voice })
        {
            var settings = builtins.SystemVariables.GetAudioSettings(kind);
            Assert.True(settings.Enabled!.Value.AsBool());
            Assert.Equal(1000, settings.Volume!.Value.AsNumber());
        }
    }

    [Fact]
    public void BareSigilsRemainInvalidOutsideVariableValueFirstArgument()
    {
        Assert.NotEmpty(SyntaxTree.ParseStatement("CreateChoice($);").Diagnostics);
        Assert.NotEmpty(SyntaxTree.ParseStatement("VariableValue(\"$\", #);").Diagnostics);
        Assert.Empty(SyntaxTree.ParseStatement("VariableValue(#, \"name\", true);").Diagnostics);
    }

    private static TestBuiltins Execute(string source, string? child = null, Action<NsScriptVM>? beforeStartup = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-dynamic-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), source, Encoding.UTF8);
            if (child is not null) File.WriteAllText(Path.Combine(root, "child.nss"), child, Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            beforeStartup?.Invoke(vm);
            var thread = vm.CreateThread("test", "main")!.Value;
            var builtins = new TestBuiltins { SystemVariables = vm.SystemVariables };
            for (int i = 0; i < 1000 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
            return builtins;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public required SystemVariableLookup SystemVariables { get; init; }
        public List<string> Choices { get; } = [];
        public List<string> InvalidFormats { get; } = [];
        public override void AssertTrue(bool value) => Assert.True(value);
        public override void CreateChoice(in EntityPath path) => Choices.Add(path.ToString());
        public override void ReportInvalidArgument(string functionName, string argumentName, in ConstantValue value)
        {
            Assert.Equal("String", functionName);
            InvalidFormats.Add(value.ConvertToString());
        }
    }
}
