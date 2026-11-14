using System;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class LegacyUnsupportedTests
{
    [Fact]
    public void AppendedBuiltinPreservesExistingTokensAndDispatchesSourceLocation()
    {
        Assert.Equal((int)BuiltInFunction.Strstr + 1, (int)BuiltInFunction.LegacyUnsupported);
        string root = Path.Combine(Path.GetTempPath(), "ns-legacy-unsupported-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), "chapter main { LegacyUnsupported(\"script/menu.nps\", 27, \"SYSTEM config\"); }", Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(root, root, Encoding.UTF8), globals);
            var thread = vm.CreateThread("test", "main")!.Value;
            var error = Assert.Throws<NotSupportedException>(() =>
            {
                var builtins = new DefaultBuiltins();
                for (int i = 0; i < 100 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            });
            Assert.Contains("script/menu.nps:27", error.Message);
            Assert.Contains("SYSTEM config", error.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class DefaultBuiltins : BuiltInFunctions;
}
