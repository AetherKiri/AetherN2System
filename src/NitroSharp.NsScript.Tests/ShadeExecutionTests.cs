using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class ShadeExecutionTests
{
    [Fact]
    public void AppendedShadeConstantsDispatchWithoutChangingExistingTokens()
    {
        Assert.Equal((int)BuiltInConstant.Passive + 1, (int)BuiltInConstant.SemiHeavy);
        Assert.Equal((int)BuiltInConstant.SemiHeavy + 1, (int)BuiltInConstant.Light);
        string root = Path.Combine(Path.GetTempPath(), "ns-shade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), """
                chapter main {
                    SetShade("snapshot", HEAVY);
                    SetShade("snapshot", SEMIHEAVY);
                    SetShade("snapshot", MEDIUM);
                    SetShade("snapshot", LIGHT);
                    SetShade("snapshot", NORMAL);
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
            Assert.Equal(new uint[] { 16, 12, 8, 4, 0 }, builtins.Passes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public List<uint> Passes { get; } = [];
        public override void BoxBlur(in EntityQuery query, uint nbPasses) => Passes.Add(nbPasses);
    }
}
