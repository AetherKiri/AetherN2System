using System.Runtime.InteropServices;
using NitroSharp.Graphics;
using Xunit;

namespace NitroSharp.Tests;

public sealed class LegacyTransitionCoverageTests
{
    [Theory]
    [InlineData(1000, 896)]
    [InlineData(2000, 1920)]
    [InlineData(500, 384)]
    [InlineData(1024, 1024)]
    [InlineData(127, 0)]
    public void DurationUsesTheNativeIntegerStepClock(int authored, int actual)
        => Assert.Equal(System.TimeSpan.FromMilliseconds(actual),
            LegacyTransitionCoverage.QuantizeDuration(System.TimeSpan.FromMilliseconds(authored)));

    [Fact]
    public void IncrementalCoverageCountsEachNativeStepOnce()
    {
        float first = LegacyTransitionCoverage.Incremental(-1, 2);
        float second = LegacyTransitionCoverage.Incremental(2, 4);
        Assert.InRange(System.MathF.Abs(LegacyTransitionCoverage.Calculate(4f / 128, 0, false)
            - (1 - (1 - first) * (1 - second))), 0, 0.000001f);
        Assert.Equal(0, LegacyTransitionCoverage.Incremental(2, 2));
        Assert.Equal(0, LegacyTransitionCoverage.Incremental(2, 1));
        Assert.Equal(6f / 256, LegacyTransitionCoverage.Incremental(2, 3));
    }

    [Fact]
    public void NativeFirstStepsRetainCumulativeUniformAndIndexedMaskWeights()
    {
        Assert.Equal(0, LegacyTransitionCoverage.Calculate(0, 0, false));
        Assert.Equal(2f / 256, LegacyTransitionCoverage.Calculate(1f / 128, 0, false));
        Assert.Equal(1 - (254f / 256) * (252f / 256),
            LegacyTransitionCoverage.Calculate(2f / 128, 0, false), 6);
        Assert.Equal(8f / 256, LegacyTransitionCoverage.Calculate(1f / 128, 0, true));
        Assert.Equal(0, LegacyTransitionCoverage.Calculate(16f / 128, 128, true));
        Assert.Equal(8f / 256, LegacyTransitionCoverage.Calculate(17f / 128, 128, true));
        Assert.Equal(1, LegacyTransitionCoverage.Calculate(1, 255, true));
    }

    [Fact]
    public void UniformBufferMatchesPackedGlslCoverageAndMaskedValues()
    {
        Assert.Equal(32 + 64 * 16, Marshal.SizeOf<TransitionParameters>());
        var parameters = new TransitionParameters(new(17f / 128, 0, 1, 2), default);
        Assert.Equal(8f / 256, parameters.Coverage[128]);
        Assert.Equal(0, parameters.Coverage[255]);
        Assert.True(parameters.Coverage[0] > parameters.Coverage[128]);
    }
}
