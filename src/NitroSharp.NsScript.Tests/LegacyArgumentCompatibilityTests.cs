using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class LegacyArgumentCompatibilityTests
{
    [Theory]
    [InlineData("name", "name")]
    [InlineData("@name", "name")]
    [InlineData("@絵背景101_がし", "絵背景101_がし")]
    public void AliasAcceptsOneOptionalQueryPrefix(string input, string expected)
        => Assert.Equal(expected, EntityAlias.Parse(input).Value);

    [Theory]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("@@name")]
    [InlineData("name@other")]
    [InlineData("@name*")]
    public void AliasStillRejectsMalformedNames(string input) => Assert.Null(EntityAlias.TryParse(input));

    [Theory]
    [InlineData("@@OnBG01", "@OnBG01", EntityQueryScope.CurrentAliases)]
    [InlineData("@@@OnBG01", "@@OnBG01", EntityQueryScope.CurrentAliases)]
    [InlineData("<@@OnBG01", "@OnBG01", EntityQueryScope.AllAliases)]
    public void AliasQueryRemovesOnlyItsScopePrefix(string input, string alias, EntityQueryScope scope)
    {
        EntityQuery query = EntityQuery.Parse(input);
        Assert.Equal(input, query.ToString());
        Assert.Equal(alias, query.Parts[0].Pattern.Value);
        Assert.Equal(scope, query.Parts[0].Scope);
    }

    [Theory]
    [InlineData("name@other")]
    [InlineData("@name@other")]
    [InlineData("<name@other")]
    public void QueryStillRejectsEmbeddedAliasPrefixes(string input) => Assert.Null(EntityQuery.TryParse(input));

    [Fact]
    public void WaitActionRecognizesOnlyTheShippedNullTypoAndAliasPrefixNormalizes()
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-legacy-arguments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test.nss"), """
                chapter main {
                    WaitAction("panel", nulll);
                    WaitAction("panel", null);
                    WaitAction("panel", 250);
                    SetAlias("sprite", "@background");
                    Fade("@@OnBG01", 0, 0, null, false);
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
            Assert.Equal(new TimeSpan?[] { null, null, TimeSpan.FromMilliseconds(250) }, builtins.Timeouts);
            Assert.Equal("background", Assert.Single(builtins.Aliases).Value);
            Assert.Equal("@@OnBG01", Assert.Single(builtins.Fades).ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public List<TimeSpan?> Timeouts { get; } = [];
        public List<EntityAlias> Aliases { get; } = [];
        public List<EntityQuery> Fades { get; } = [];
        public override void Fade(in EntityQuery query, TimeSpan duration, NsRational dstOpacity, NsEaseFunction easeFunction, TimeSpan waitTimeout) => Fades.Add(query);
        public override void WaitAction(in EntityQuery query, TimeSpan? timeout) => Timeouts.Add(timeout);
        public override void SetAlias(in EntityQuery query, in EntityAlias alias) => Aliases.Add(alias);
    }
}
