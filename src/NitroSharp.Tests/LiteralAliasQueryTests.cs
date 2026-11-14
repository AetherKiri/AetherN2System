using System;
using NitroSharp.NsScript;
using Xunit;

namespace NitroSharp.Tests;

public sealed class LiteralAliasQueryTests
{
    [Fact]
    public void FadeWithRepeatedPrefixLeavesTheBareAliasUnchanged()
    {
        var context = new TestContext();
        var world = new World(context.MainProcess, context.MainThread);
        var background = world.AddEntity(new FadeTarget(context.MainThread));
        background.SetAlias(EntityAlias.Parse("OnBG01"));
        world.BeginFrame();
        // Fade with no wait only needs the world; no graphics or audio device is accessed.
        var game = new GameContext
        {
            World = world, Window = null!, Profile = null!, RenderContext = null!, Content = null!,
            GlyphRasterizer = null!, AudioContext = null!, InputContext = null!, VM = null!, RootLog = null!
        };
        var builtins = new Builtins(game);
        builtins.Fade(EntityQuery.Parse("@@OnBG01"), TimeSpan.Zero, new NsRational(0, 1000), default, TimeSpan.Zero);
        Assert.Equal(0, background.Fades);
        builtins.Fade(EntityQuery.Parse("@OnBG01"), TimeSpan.Zero, new NsRational(0, 1000), default, TimeSpan.Zero);
        Assert.Equal(1, background.Fades);
        world.DisposeAll();
    }

    private sealed class FadeTarget(Entity parent) : Entity(EntityName.Parse("background"), parent)
    {
        public int Fades;
        public override void Fade(float dstOpacity, TimeSpan duration, NsEaseFunction easeFunction) => Fades++;
    }

    [Theory]
    [InlineData("@@OnBG01")]
    [InlineData("@@@OnBG01")]
    [InlineData("<@@OnBG01")]
    public void RepeatedPrefixDoesNotSelectTheBareAlias(string text)
    {
        var context = new TestContext();
        var world = new World(context.MainProcess, context.MainThread);
        var background = world.AddEntity(new BlankEntity(EntityName.Parse("background"), context.MainThread));
        background.SetAlias(EntityAlias.Parse("OnBG01"));
        world.BeginFrame();
        Assert.Same(background, Assert.Single(world.Query(EntityQuery.Parse("@OnBG01")).AsSpan().ToArray()));
        Assert.Empty(world.Query(EntityQuery.Parse(text)).AsSpan().ToArray());
        world.DisposeAll();
    }
}
