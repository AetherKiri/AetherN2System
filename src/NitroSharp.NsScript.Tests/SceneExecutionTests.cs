using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Syntax;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class SceneExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DynamicSceneCanUseBytecodeWithoutCallerSource(bool keepEpisode)
    {
        using var script = new ScriptFiles("""
            chapter main {
                if (false) { call_scene episode.nss->entry; }
                $target = "nss/episode.nss->entry";
                call_scene $target;
                CreateChoice("returned");
            }
            """, "scene entry { CreateChoice(\"compiled-child\"); }");
        script.RemoveSources(keepEpisode);
        Assert.Equal(new[] { "compiled-child", "returned" }, script.Run().Choices);
    }

    [Fact]
    public void IncludeFreeSceneInheritsHelpersAndLoadsItsOwnDialogue()
    {
        using var script = new ScriptFiles("""
            #include "helpers.nss"
            chapter main {
                $shared = 3;
                #shared = 5;
                InheritedHelper();
                $target = "episode.nss->entry";
                call_scene $target;
                assert($shared == 9);
                assert(#shared == 8);
                CreateChoice("caller-returned");
            }
            """, """
            scene entry {
                <PRE box>
                [leaf]
                Original sample dialogue.
                </PRE>
                InheritedHelper();
                LoadLeafText();
                $target = "@->nested";
                call_scene $target;
                CreateChoice("child-returned");
            }
            scene nested { InheritedHelper(); }
            """, additionalSources: new Dictionary<string, string>
        {
            ["helpers.nss"] = """
                function InheritedHelper() { $shared += 2; #shared++; CreateChoice("inherited"); }
                function LoadLeafText() { InnerLoader(); }
                function InnerLoader() { LoadText("entry", "box", "@leaf", 100, 100, 0, 0); }
                """
        });
        var builtins = script.Run();
        Assert.Equal(new[] { "inherited", "inherited", "inherited", "child-returned", "caller-returned" }, builtins.Choices);
        var dialogue = Assert.Single(builtins.Dialogues);
        Assert.Equal("episode", dialogue.Module.Name);
        Assert.Equal("entry", dialogue.Module.GetSubroutineName(dialogue.SubroutineIndex));
        Assert.Equal("leaf", dialogue.BlockName);
    }

    [Fact]
    public void NestedDynamicCompilationKeepsTheExplicitScopeChain()
    {
        using var script = new ScriptFiles("""
            #include "helpers.nss"
            chapter main {
                $shared = 10;
                SharedHelper();
                $target = "episode.nss->entry";
                call_scene $target;
                assert($shared == 16);
            }
            """, """
            function ChildHelper() { $shared += 3; CreateChoice("child-helper"); }
            scene entry { $next = "nested.nss->entry"; call_scene $next; CreateChoice("child-returned"); }
            """, additionalSources: new Dictionary<string, string>
        {
            ["helpers.nss"] = "function SharedHelper() { $shared++; CreateChoice(\"root-helper\"); }",
            ["nested.nss"] = "scene entry { SharedHelper(); ChildHelper(); SharedHelper(); }"
        });
        Assert.Equal(new[] { "root-helper", "root-helper", "child-helper", "root-helper", "child-returned" }, script.Run().Choices);
    }

    [Fact]
    public void SameLeafUsesItsCallersScopeRegardlessOfEarlierLoads()
    {
        using var script = new ScriptFiles("""
            #include "first.nss"
            chapter main {
                $target = "episode.nss->entry";
                call_scene $target;
                $target = "alternate.nss->entry";
                call_scene $target;
                $target = "episode.nss->entry";
                call_scene $target;
            }
            """, "scene entry { SharedHelper(); }", additionalSources: new Dictionary<string, string>
        {
            ["first.nss"] = "function SharedHelper() { CreateChoice(\"first\"); }",
            ["second.nss"] = "function SharedHelper() { CreateChoice(\"second\"); }",
            ["alternate.nss"] = "#include \"second.nss\"\nscene entry { $next = \"episode.nss->entry\"; call_scene $next; }"
        });
        Assert.Equal(new[] { "first", "second", "first" }, script.Run().Choices);
    }

    [Fact]
    public void DynamicScenesCompileOnDemandAndReturnToTheirCallers()
    {
        using var script = new ScriptFiles("""
            chapter main {
                $shared = 11;
                #shared = 9;
                $target = "nss/episode.nss->entry";
                CreateChoice("caller-before");
                call_scene $target;
                assert($shared == 13);
                assert(#shared == 10);
                $module = "nss/episode.nss";
                $scene = "second";
                call_scene $module->$scene;
                $scene = "third";
                call_scene nss/episode.nss->$scene;
                $local = "finish";
                call_scene @->$local;
                CreateChoice("caller-after");
            }
            scene finish { CreateChoice("local-finish"); }
            """, """
            scene entry {
                CreateChoice("entry");
                $local = "nested";
                call_scene @->$local;
                CreateChoice("entry-returned");
                return;
                CreateChoice("unreachable");
            }
            scene nested {
                $newVariable = 42;
                #newFlag = true;
                $shared += 2;
                #shared++;
                CreateChoice("nested");
            }
            scene second {
                assert($newVariable == 42);
                assert(#newFlag == true);
                CreateChoice("second");
            }
            scene third { CreateChoice("third"); }
            """);
        Assert.False(File.Exists(Path.Combine(script.Bytecode, "episode.nsx")));
        Assert.Equal(new[] { "caller-before", "entry", "nested", "entry-returned", "second", "third", "local-finish", "caller-after" }, script.Run().Choices);
        // Runtime modules and their expanded global table belong to this VM.
        Assert.False(File.Exists(Path.Combine(script.Bytecode, "episode.nsx")));
        Assert.Equal(new[] { "caller-before", "entry", "nested", "entry-returned", "second", "third", "local-finish", "caller-after" }, script.Run().Choices);
    }

    [Fact]
    public void StaticScenesAndChaptersRunSynchronouslyAcrossNestedReturns()
    {
        using var script = new ScriptFiles("""
            chapter main {
                CreateChoice("before");
                call_scene episode.nss->entry;
                CreateChoice("scene-returned");
                call_chapter episode.nss;
                CreateChoice("chapter-returned");
            }
            """, """
            chapter main { CreateChoice("chapter"); }
            scene entry { CreateChoice("entry"); call_scene @->one; CreateChoice("entry-returned"); }
            scene one { call_scene @->two; CreateChoice("one-returned"); }
            scene two { call_scene @->three; CreateChoice("two-returned"); }
            scene three { call_scene @->four; CreateChoice("three-returned"); }
            scene four { CreateChoice("four"); return; CreateChoice("unreachable"); }
            """);
        Assert.Equal(new[] { "before", "entry", "four", "three-returned", "two-returned", "one-returned", "entry-returned", "scene-returned", "chapter", "chapter-returned" }, script.Run().Choices);
    }

    [Fact]
    public void CallScopesWrapChaptersAndStaticOrDynamicScenesButNotFunctions()
    {
        using var script = new ScriptFiles("""
            #include "helpers.nss"
            function LocalHelper() { CreateChoice("local-helper"); }
            chapter main {
                LocalHelper();
                ExternalHelper();
                call_scene episode.nss->entry;
                CreateChoice("scene-returned");
                call_chapter episode.nss;
                CreateChoice("chapter-returned");
            }
            """, """
            #include "helpers.nss"
            chapter main { CreateChoice("chapter"); return; }
            scene entry {
                ExternalHelper();
                $target = "@->nested";
                call_scene $target;
                CreateChoice("nested-returned");
            }
            scene nested { CreateChoice("nested"); }
            """, additionalSources: new Dictionary<string, string>
        {
            ["helpers.nss"] = "function ExternalHelper() { CreateChoice(\"external-helper\"); }"
        });
        TestBuiltins builtins = script.Run();
        Assert.Equal(new[] { "local-helper", "external-helper", "external-helper", "nested", "nested-returned", "scene-returned", "chapter", "chapter-returned" }, builtins.Choices);
        Assert.Equal(new[] { 0, 0, 1, 2, 1, 0, 1, 0 }, builtins.ChoiceScopeDepths);
        Assert.Equal(new[] { "enter", "enter", "leave", "leave", "enter", "leave" }, builtins.CallScopeEvents);
        Assert.Equal(0, builtins.CallScopeDepth);
    }

    [Fact]
    public void IncrementalGlobalsKeepTheirSlotsWhenRuntimeTablesGrow()
    {
        var episode = new StringBuilder("scene entry {\n");
        for (int i = 0; i < 5050; i++) episode.Append("$extra").Append(i).Append(" = 1;\n");
        for (int i = 0; i < 2050; i++) episode.Append("#extra").Append(i).Append(" = true;\n");
        episode.Append("CursorPosition($x, #y); assert($x == 12); assert(#y == 34); $shared += 2; #shared++; }");
        using var script = new ScriptFiles("""
            chapter main {
                $shared = 17;
                #shared = 3;
                $target = "episode.nss->entry";
                call_scene $target;
                assert($shared == 19);
                assert(#shared == 4);
                CreateChoice("returned");
            }
            """, episode.ToString());
        Assert.Equal(new[] { "returned" }, script.Run().Choices);
    }

    [Fact]
    public void MissingDynamicSceneReportsTheResolvedTarget()
    {
        using var script = new ScriptFiles("chapter main { $target = \"episode.nss->missing\"; call_scene $target; }", "scene entry { }");
        var error = Assert.Throws<InvalidOperationException>(() => script.Run());
        Assert.Contains("Scene 'missing'", error.Message);
        Assert.Contains("episode", error.Message);
    }

    [Fact]
    public void RetainedSystemVariableLookupRefreshesAfterDynamicCompilation()
    {
        using var script = new ScriptFiles("chapter main { $target = \"episode.nss->entry\"; call_scene $target; }", """
            scene entry {
                $SYSTEM_position_x_text_icon = 91;
                $SYSTEM_present_preprocess = "box";
                $SYSTEM_skip = true;
            }
            chapter verify { assert($SYSTEM_position_x_text_icon == 13); assert($SYSTEM_present_preprocess == "updated"); }
            """);
        SystemVariableLookup? retained = null;
        script.Run(vm => retained = vm.SystemVariables, (vm, builtins) =>
        {
            Assert.Same(retained, vm.SystemVariables);
            Assert.Equal(91f, retained!.PositionXTextIcon.AsNumber());
            Assert.Equal("box", retained.CurrentDialogueBox.AsString());
            Assert.True(retained.Skip.AsBool());
            retained.PositionXTextIcon = ConstantValue.Number(13);
            retained.CurrentDialogueBox = ConstantValue.String("updated");
            var thread = vm.CreateThread("episode", "verify")!.Value;
            for (int i = 0; i < 20 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
        });
    }

    [Fact]
    public void DynamicModulesHonorTheConfiguredSourceEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var script = new ScriptFiles("chapter main { $target = \"episode.nss->開始\"; call_scene $target; }", "scene 開始 { CreateChoice(\"欢迎\"); }", Encoding.GetEncoding("gbk"));
        Assert.Equal(new[] { "欢迎" }, script.Run().Choices);
    }

    [Theory]
    [InlineData("call_scene $target;")]
    [InlineData("call_scene @->$name;")]
    [InlineData("call_scene $module->$name;")]
    [InlineData("call_scene nss/episode.nss->$name;")]
    [InlineData("call_scene \"episode.nss->\" + $name;")]
    public void DynamicSceneSyntaxRoundtrips(string source)
    {
        var tree = SyntaxTree.ParseStatement(source);
        Assert.Empty(tree.Diagnostics);
        Assert.NotNull(Assert.IsType<CallSceneStatement>(tree.Root).DynamicTarget);
        using var text = new StringWriter();
        tree.Root.Dump(text, SyntaxDumpFormat.RoundtripText);
        var reparsed = SyntaxTree.ParseStatement(text.ToString());
        Assert.Empty(reparsed.Diagnostics);
        Assert.NotNull(Assert.IsType<CallSceneStatement>(reparsed.Root).DynamicTarget);
    }

    private sealed class ScriptFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ns-scene-test-" + Guid.NewGuid().ToString("N"));
        public string Bytecode { get; }
        private readonly string _source;
        private readonly string _globals;
        private readonly Encoding _encoding;

        public ScriptFiles(string startup, string episode, Encoding? encoding = null, IReadOnlyDictionary<string, string>? additionalSources = null)
        {
            _encoding = encoding ?? Encoding.UTF8;
            _source = Path.Combine(_root, "nss");
            Bytecode = Path.Combine(_root, "nsx");
            Directory.CreateDirectory(_source);
            Directory.CreateDirectory(Bytecode);
            File.WriteAllText(Path.Combine(_source, "boot.nss"), startup, _encoding);
            File.WriteAllText(Path.Combine(_source, "episode.nss"), episode, _encoding);
            if (additionalSources is not null)
                foreach ((string name, string source) in additionalSources)
                    File.WriteAllText(Path.Combine(_source, name), source, _encoding);
            var compilation = new Compilation(_source, _encoding);
            var emitted = compilation.Emit([compilation.GetSourceModule("boot.nss")], Bytecode, "globals");
            Assert.Empty(emitted.Diagnostics);
            _globals = Path.Combine(Bytecode, "globals");
        }

        public TestBuiltins Run(Action<NsScriptVM>? before = null, Action<NsScriptVM, TestBuiltins>? after = null)
        {
            using var globals = File.OpenRead(_globals);
            var vm = new NsScriptVM(new FileSystemNsxModuleLocator(Bytecode, _source, _encoding), globals);
            before?.Invoke(vm);
            var thread = vm.CreateThread("boot", "main")!.Value;
            var builtins = new TestBuiltins();
            for (int i = 0; i < 1000 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting, "Scene execution did not terminate.");
            after?.Invoke(vm, builtins);
            return builtins;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        public void RemoveSources(bool keepEpisode)
        {
            foreach (string path in Directory.EnumerateFiles(_source))
                if (!keepEpisode || Path.GetFileName(path) != "episode.nss") File.Delete(path);
        }
    }

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public List<string> Choices { get; } = [];
        public List<int> ChoiceScopeDepths { get; } = [];
        public List<string> CallScopeEvents { get; } = [];
        public int CallScopeDepth { get; private set; }
        public List<DialogueBlockToken> Dialogues { get; } = [];
        public override void AssertTrue(bool value) => Assert.True(value);
        public override void CreateChoice(in EntityPath path)
        {
            Choices.Add(path.ToString());
            ChoiceScopeDepths.Add(CallScopeDepth);
        }
        public override void EnterCallScope()
        {
            CallScopeDepth++;
            CallScopeEvents.Add("enter");
        }
        public override void LeaveCallScope()
        {
            Assert.True(CallScopeDepth > 0);
            CallScopeDepth--;
            CallScopeEvents.Add("leave");
        }
        public override Vector2 GetCursorPosition() => new(12, 34);
        public override void LoadDialogueBlock(in DialogueBlockToken blockToken, uint maxWidth, uint maxHeight, int letterSpacing, int lineSpacing)
            => Dialogues.Add(blockToken);
    }
}
