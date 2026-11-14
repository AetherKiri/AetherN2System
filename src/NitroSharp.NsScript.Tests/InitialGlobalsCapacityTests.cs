using System;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class InitialGlobalsCapacityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5100)]
    public void InitialLargeTablesSupportTrailingSystemDefaultsAndOutputReferences(int variables)
    {
        var source = new StringBuilder("chapter main {\n");
        for (int i = 0; i < 2100; i++) source.AppendLine($"#padding_flag_{i} = {i};");
        for (int i = 0; i < variables; i++) source.AppendLine($"$padding_variable_{i} = {i};");
        foreach (string channel in new[] { "bgm", "se", "voice" })
        {
            source.AppendLine($"assert(#SYSTEM_sound_{channel} == true);");
            source.AppendLine($"assert(#SYSTEM_sound_volume_{channel} == 1000);");
            source.AppendLine($"assert(#SYSTEM_sound_volume_{channel}_default == 1000);");
        }
        source.AppendLine("""
            DateTime(#result_year, #result_month, #result_day, $result_hour, $result_minute, $result_second);
            assert(#result_year == 2026);
            assert(#result_month == 11);
            assert(#result_day == 15);
            assert($result_hour == 4);
            assert($result_minute == 5);
            assert($result_second == 6);
            assert(#padding_flag_2099 == 2099);
            """);
        if (variables > 0) source.AppendLine("assert($padding_variable_5099 == 5099);");
        source.AppendLine("}");
        string root = Path.Combine(Path.GetTempPath(), "initial-globals-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), source.ToString(), Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            // These defaults are initialized by the constructor before any script
            // or later module can trigger UpdateGlobals' resizing path.
            foreach (var kind in new[] { NsAudioKind.BackgroundMusic, NsAudioKind.SoundEffect, NsAudioKind.Voice })
            {
                var settings = vm.SystemVariables.GetAudioSettings(kind);
                Assert.True(settings.Enabled!.Value.AsBool());
                Assert.Equal(1000, settings.Volume!.Value.AsNumber());
            }
            var state = vm.CreateThread("test", "main")!.Value;
            var builtins = new TestBuiltins();
            for (int i = 0; i < 100 && !state.DoneExecuting; i++) vm.Tick(ref state, builtins);
            Assert.True(state.DoneExecuting);
            Assert.Equal(1, builtins.DateReads);
            Assert.Equal(16 + (variables > 0 ? 1 : 0), builtins.Assertions);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public int Assertions, DateReads;
        public override void AssertTrue(bool value) { Assert.True(value); Assertions++; }
        public override DateTime GetDateTime() { DateReads++; return new DateTime(2026, 11, 15, 4, 5, 6); }
    }
}
