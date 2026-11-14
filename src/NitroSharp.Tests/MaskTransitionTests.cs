using System;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using Xunit;

namespace NitroSharp.Tests;

public sealed class MaskTransitionTests
{
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(1f, 0f)]
    [InlineData(.2f, .7f)]
    public void AnimationKeepsExactFinalCoverage(float source, float destination)
    {
        using var transition = new MaskTransition(null, source, destination, .25f,
            TimeSpan.FromMilliseconds(100), NsEaseFunction.Linear);
        Assert.Equal(source, transition.ProgressValue);
        Assert.True(transition.Update(0));
        Assert.True(transition.Update(50));
        Assert.Equal((source + destination) / 2, transition.ProgressValue, 5);
        Assert.False(transition.Update(50));
        Assert.Equal(destination, transition.ProgressValue);
        Assert.False(transition.Update(100));
        Assert.Equal(destination, transition.ProgressValue);
    }

    [Fact]
    public void EasingAndInstantTransitionsPreserveParameters()
    {
        using var transition = new MaskTransition(null, 0, 1, .4f,
            TimeSpan.FromMilliseconds(100), NsEaseFunction.QuadraticEaseIn);
        transition.Update(0);
        transition.Update(50);
        Assert.Equal(.25f, transition.ProgressValue);
        Assert.Equal(.4f, transition.Feather);
        using var instant = new MaskTransition(null, 1, 0, 0, TimeSpan.Zero, NsEaseFunction.Linear);
        Assert.Equal(0, instant.ProgressValue);
        Assert.Null(instant.Mask);
    }
}
