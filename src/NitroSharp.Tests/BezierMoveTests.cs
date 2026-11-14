using System;
using System.Collections.Immutable;
using System.Numerics;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using Veldrid;
using Xunit;
using DesignSize = NitroSharp.Size<NitroSharp.DesignPixel>;

namespace NitroSharp.Tests;

public sealed class BezierMoveTests
{
    [Fact]
    public void CompositeMotionTraversesEachSegmentAndFinishesAtTheEndpoint()
    {
        var visual = new SolidColorRect(EntityName.Parse("moving"), null, 1,
            RgbaFloat.White, new DesignSize(10, 10));
        visual.Transform.Position = new Vector3(0, 0, 7);
        var curve = new ProcessedBezierCurve(ImmutableArray.Create(
            Line(0, 12), Line(12, 24)));
        var animation = new BezierMoveAnimation(visual, curve, TimeSpan.FromSeconds(1), NsEaseFunction.Linear);
        Assert.True(animation.Update(0));
        Assert.True(animation.Update(250));
        Assert.Equal(new Vector3(6, 0, 7), visual.Transform.Position);
        Assert.True(animation.Update(250));
        Assert.Equal(new Vector3(12, 0, 7), visual.Transform.Position);
        Assert.False(animation.Update(500));
        Assert.Equal(new Vector3(24, 0, 7), visual.Transform.Position);
    }

    [Fact]
    public void ImmediateMotionCompletesAnActiveCurve()
    {
        var visual = new SolidColorRect(EntityName.Parse("moving"), null, 1,
            RgbaFloat.White, new DesignSize(10, 10));
        var curve = new ProcessedBezierCurve(ImmutableArray.Create(Line(0, 12)));
        visual.BezierMove(curve, TimeSpan.FromSeconds(1), NsEaseFunction.Linear);
        Assert.True(visual.IsAnimationActive(AnimationKind.BezierMove));
        visual.BezierMove(curve, TimeSpan.Zero, NsEaseFunction.Linear);
        Assert.False(visual.IsAnimationActive(AnimationKind.BezierMove));
        Assert.Equal(new Vector3(12, 0, 0), visual.Transform.Position);
    }

    [Fact]
    public void RotationCombinesRelativeAxesAndAbsoluteDestination()
    {
        var visual = new SolidColorRect(EntityName.Parse("turning"), null, 1,
            RgbaFloat.White, new DesignSize(10, 10));
        visual.Transform.Rotation = new Vector3(10, 20, 30);
        visual.Rotate(new NsNumeric(0, true), new NsNumeric(-5, true), new NsNumeric(90, false),
            TimeSpan.Zero, NsEaseFunction.Linear);
        Assert.Equal(new Vector3(10, 15, 90), visual.Transform.Rotation);
        Assert.False(visual.IsAnimationActive(AnimationKind.Rotate));
        var animation = new RotationAnimation(visual, visual.Transform.Rotation,
            new Vector3(10, 15, 180), TimeSpan.FromSeconds(1), NsEaseFunction.Linear);
        Assert.True(animation.Update(0));
        Assert.True(animation.Update(500));
        Assert.Equal(new Vector3(10, 15, 135), visual.Transform.Rotation);
        Assert.False(animation.Update(500));
        Assert.Equal(new Vector3(10, 15, 180), visual.Transform.Rotation);
    }

    [Theory]
    [InlineData(.5f)]
    [InlineData(1f)]
    [InlineData(2f)]
    public void RotationKeepsTheChosenPivotFixedAtEveryRenderScale(float renderScale)
    {
        var transform = Transform.Default;
        transform.Pivot = new Vector2(50, 25);
        transform.Position = new Vector3(10, 20, 0);
        transform.Rotation.Z = 90;
        var viewport = new Vector2(7, 9);
        Matrix4x4 matrix = RenderContext.CreateTransformMatrix(transform, new DesignSize(100, 50),
            renderScale, viewport, useScaling: true, aligned: false);
        Vector3 actual = Vector3.Transform(new Vector3(transform.Pivot.Value, 0), matrix);
        Vector3 expected = new Vector3((transform.Pivot.Value + new Vector2(10, 20)) * renderScale + viewport, 0);
        Assert.True(Vector3.Distance(actual, expected) < .001f);
    }

    private static ProcessedBezierSegment Line(float from, float to)
    {
        float third = (to - from) / 3;
        return new ProcessedBezierSegment(new Vector2(from, 0), new Vector2(from + third, 0),
            new Vector2(from + 2 * third, 0), new Vector2(to, 0));
    }
}
