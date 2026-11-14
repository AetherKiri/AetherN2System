using System;
using System.Collections.Generic;
using System.Numerics;
using DesignSizeU = NitroSharp.SizeU<NitroSharp.DesignPixel>;
using DesignRectU = NitroSharp.RectangleU<NitroSharp.DesignPixel>;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using Veldrid;
using Xunit;

namespace NitroSharp.Tests;

public sealed class PlainEffectTests
{
    [Fact]
    public void PassiveFreezesTheLatestCaptureAndRetainsOuterEntityActions()
    {
        int captures = 0, releases = 0;
        var rectangles = new List<DesignRectU>();
        var effect = new PlainEffect(EntityName.Parse("snapshot"), new TestContext().MainThread, 30,
            new DesignSizeU(512, 576), rectangle =>
            {
                rectangles.Add(rectangle);
                captures++;
                return SpriteTexture.SolidColor(captures < 3 ? RgbaFloat.Red : RgbaFloat.Blue, new DesignSizeU(512, 576));
            }, texture => { texture.Dispose(); releases++; });
        effect.Transform.Position = new Vector3(512, 0, 0);
        effect.RefreshSnapshot();
        effect.RefreshSnapshot();
        Assert.Equal(2, captures);
        Assert.Equal(1, releases);
        Assert.True(effect.Request(NsEntityAction.Passive));
        Assert.True(effect.IsPassive);
        Assert.Equal(RgbaFloat.Blue, effect.Snapshot!.Value.Color);
        effect.Transform.Position = new Vector3(10, 20, 0);
        effect.Fade(.25f, TimeSpan.Zero, NsEaseFunction.Linear);
        effect.RefreshSnapshot();
        effect.Request(NsEntityAction.Passive);
        Assert.Equal(3, captures);
        Assert.Equal(2, releases);
        Assert.All(rectangles, rectangle => Assert.Equal(new DesignRectU(512, 0, 512, 576), rectangle));
        Assert.Equal(new Vector3(10, 20, 0), effect.Transform.Position);
        Assert.Equal(.25f, effect.Color.A);
        Assert.True(effect.Request(NsEntityAction.Lock));
        Assert.True(effect.IsLocked);
        Assert.True(effect.Request(NsEntityAction.Unlock));
        Assert.False(effect.IsLocked);
        effect.Dispose();
        effect.Dispose();
        Assert.Equal(3, releases);
    }

    [Fact]
    public void CaptureFailureRetainsThePreviouslyOwnedSnapshot()
    {
        bool fail = false;
        int releases = 0;
        using var effect = new PlainEffect(EntityName.Parse("snapshot"), new TestContext().MainThread, 1,
            new DesignSizeU(100, 100), _ => fail ? throw new InvalidOperationException("capture failed")
                : SpriteTexture.SolidColor(RgbaFloat.Red, new DesignSizeU(100, 100)),
            texture => { texture.Dispose(); releases++; });
        effect.RefreshSnapshot();
        fail = true;
        Assert.Throws<InvalidOperationException>(() => effect.Request(NsEntityAction.Passive));
        Assert.False(effect.IsPassive);
        Assert.Equal(0, releases);
        Assert.Equal(RgbaFloat.Red, effect.Snapshot!.Value.Color);
    }
}
