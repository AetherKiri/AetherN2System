using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class OfflineTwitterTests
{
    [Fact]
    public void OfflineStatusTakesTheGamesAccountAndCanceledInputBranches()
    {
        var builtins = Execute("""
            chapter main {
                Twitter_Init("unused-private-account-directory");
                #twitter起動時呟き画面OFF = false;
                if (Twitter_CheckAcount() < 0 && !#twitter起動時呟き画面OFF) {
                    CreateChoice("offline-account-prompt");
                }
                if (Twitter_CheckAcount() == 0) { CreateChoice("authenticated"); }
                if (Twitter_InputAcount()) { CreateChoice("account-dialog-accepted"); }
                if (Twitter_SetAcount() < 0) { CreateChoice("account-unavailable"); }
                if (Twitter_InputTweet()) { CreateChoice("tweet-dialog-accepted"); }
                if (!Twitter_Tweet()) { CreateChoice("send-failed"); }
                if (!Twitter_Tweet("A private draft that must never be sent")) { CreateChoice("explicit-send-failed"); }
                if (Twitter_CheckAcount() < 0) { CreateChoice("still-offline"); }
            }
            """);
        Assert.Equal(new[] { "offline-account-prompt", "account-unavailable", "send-failed", "explicit-send-failed", "still-offline" }, builtins.Choices);
    }

    [Fact]
    public void DispatcherPreservesNumericBooleanAndOptionalTextContracts()
    {
        var builtins = Execute("""
            chapter main {
                Twitter_Init("account-data");
                assert(Twitter_CheckAcount() == -1);
                assert(!Twitter_InputAcount());
                assert(Twitter_SetAcount() == -1);
                assert(!Twitter_InputTweet());
                assert(!Twitter_Tweet());
                assert(!Twitter_Tweet("draft"));
                Count(Twitter_CheckAcount());
                Count(Twitter_SetAcount());
                Count(Twitter_InputAcount());
                Count(Twitter_InputTweet());
                Count(Twitter_Tweet());
                assert(Time() == 321);
            }
            """);
        Assert.Equal(1, builtins.ClockReads);
        Assert.Empty(builtins.Choices);
        Assert.Equal(new[] { BuiltInType.Numeric, BuiltInType.Numeric, BuiltInType.Boolean, BuiltInType.Boolean, BuiltInType.Boolean },
            builtins.Results.ConvertAll(value => value.Type));
        Assert.Equal((int)BuiltInFunction.LegacyUnsupported + 1, (int)BuiltInFunction.Twitter_Init);
    }

    private static OfflineBuiltins Execute(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "offline-twitter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), source, Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            var state = vm.CreateThread("test", "main")!.Value;
            var builtins = new OfflineBuiltins();
            for (int i = 0; i < 1000 && !state.DoneExecuting; i++) vm.Tick(ref state, builtins);
            Assert.True(state.DoneExecuting);
            Assert.False(Directory.Exists(Path.Combine(root, "unused-private-account-directory")));
            Assert.False(Directory.Exists(Path.Combine(root, "account-data")));
            return builtins;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class OfflineBuiltins : BuiltInFunctions
    {
        public readonly List<string> Choices = [];
        public readonly List<ConstantValue> Results = [];
        public int ClockReads;
        public override int Count(in ConstantValue value) { Results.Add(value); return 0; }
        public override int GetSecondsElapsed() { ClockReads++; return 321; }
        public override void CreateChoice(in EntityPath path) => Choices.Add(path.ToString());
        public override void AssertTrue(bool value) => Assert.True(value);
    }
}
