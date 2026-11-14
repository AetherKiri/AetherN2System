using System;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class StringExecutionTests
{
    [Theory]
    [InlineData("assets/bg.jpg", "bg", true)]
    [InlineData("prefix", "pre", true)]
    [InlineData("prefix", "fix", true)]
    [InlineData("prefix", "missing", false)]
    [InlineData("BG", "bg", false)]
    [InlineData("场景/背景.png", "背景", true)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    [InlineData("abc", "", true)]
    public void StrstrReturnsContainmentBooleanUsableInConditions(string text, string needle, bool expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-string-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string boolean = expected ? "true" : "false";
            File.WriteAllText(Path.Combine(root, "test.nss"), $$"""
                chapter main {
                    $text = "{{text}}";
                    $needle = "{{needle}}";
                    $found = Strstr($text, $needle);
                    assert($found == {{boolean}});
                    assert(!$found == !{{boolean}});
                    if (Strstr($text, $needle) && !Strstr("list", "absent")) { assert({{boolean}}); }
                    else { assert(!{{boolean}}); }
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
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public override void AssertTrue(bool value) => Assert.True(value);
    }
}
