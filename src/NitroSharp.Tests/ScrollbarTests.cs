using System.Numerics;
using DesignSizeU = NitroSharp.SizeU<NitroSharp.DesignPixel>;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using Veldrid;
using Xunit;

namespace NitroSharp.Tests;

public sealed class ScrollbarTests
{
    private static Scrollbar Vertical(float value = 0)
        => new(EntityName.Parse("volume"), new TestContext().MainThread, 1,
            SpriteTexture.SolidColor(RgbaFloat.White, new DesignSizeU(12, 20)),
            new Vector2(862, 67), new Vector2(862, 207), NsScrollDirection.Vertical, value);

    [Fact]
    public void VolumeInitialValueAndPositionUseNormalizedRatio()
    {
        using var bar = Vertical(new NsRational(750, 1000));
        Assert.Equal(.75f, bar.Value);
        Assert.Equal(172, bar.Transform.Position.Y);
        Assert.Equal(862, bar.Transform.Position.X);
    }

    [Fact]
    public void HiddenKnobRetainsTrackHitGeometryAndDragsOutsideTrack()
    {
        using var bar = Vertical(.5f);
        bar.Request(NsEntityAction.Disable);
        Assert.True(bar.IsHidden);
        Assert.True(bar.ContainsTrack(new Vector2(867, 140), new Vector2(12, 20)));
        Assert.False(bar.ContainsTrack(new Vector2(850, 140), new Vector2(12, 20)));
        bar.BeginDrag(new Vector2(867, 140), new Vector2(12, 20));
        bar.Drag(new Vector2(1200, 500));
        Assert.Equal(1, bar.Value);
        bar.Drag(new Vector2(1200, -100));
        Assert.Equal(0, bar.Value);
        bar.EndDrag();
        bar.Drag(new Vector2(867, 200));
        Assert.Equal(0, bar.Value);
    }

    [Fact]
    public void GrabInsideKnobPreservesOffsetWithoutJump()
    {
        using var bar = Vertical(.5f);
        float startY = bar.Transform.Position.Y;
        bar.BeginDrag(new Vector2(866, startY + 8), new Vector2(12, 20));
        Assert.Equal(startY, bar.Transform.Position.Y);
        bar.Drag(new Vector2(866, startY + 28));
        Assert.Equal(startY + 20, bar.Transform.Position.Y);
    }

    [Fact]
    public void TrackClickJumpsToPointerAndKeyboardUsesPixelStep()
    {
        using var bar = Vertical();
        bar.BeginDrag(new Vector2(866, 147), new Vector2(12, 20));
        Assert.Equal(.5f, bar.Value);
        bar.EndDrag();
        bar.ScrollSpeed = 3;
        bar.Step(1);
        Assert.Equal(140, bar.Transform.Position.Y);
        bar.Step(-1);
        Assert.Equal(137, bar.Transform.Position.Y);
    }

    [Fact]
    public void ReversedHorizontalTrackClampsAndPreservesAxis()
    {
        using var bar = new Scrollbar(EntityName.Parse("reverse"), new TestContext().MainThread, 1,
            SpriteTexture.SolidColor(RgbaFloat.White, new DesignSizeU(10, 10)),
            new Vector2(200, 30), new Vector2(100, 70), NsScrollDirection.Horizontal, .25f);
        Assert.Equal(175, bar.Transform.Position.X);
        Assert.Equal(30, bar.Transform.Position.Y);
        bar.SetValue(2);
        Assert.Equal(1, bar.Value);
        Assert.Equal(100, bar.Transform.Position.X);
    }

    [Fact]
    public void DegenerateTrackAndNonfiniteInputStayAtStart()
    {
        using var bar = new Scrollbar(EntityName.Parse("zero"), new TestContext().MainThread, 1,
            SpriteTexture.SolidColor(RgbaFloat.White, new DesignSizeU(10, 10)),
            new Vector2(20, 30), new Vector2(20, 30), NsScrollDirection.Vertical, float.NaN);
        bar.Step(1);
        Assert.Equal(0, bar.Value);
        Assert.Equal(30, bar.Transform.Position.Y);
    }
}
