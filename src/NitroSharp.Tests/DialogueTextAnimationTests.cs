using System;
using System.Linq;
using NitroSharp.Text;
using Veldrid;
using Xunit;
using TextLayout = NitroSharp.Text.TextLayout<NitroSharp.DesignPixel>;

namespace NitroSharp.Tests;

public sealed class DialogueTextAnimationTests(FontContext fonts) : IClassFixture<FontContext>
{
    private TextRun Text(string text) => TextRun.Regular(text.AsMemory(), fonts.DefaultFont,
        new PtFontSize(20), RgbaFloat.White, null);

    [Fact]
    public void CharactersFadeInWithStaggeredStartWithoutChangingLayout()
    {
        var layout = new TextLayout(fonts.Rasterizer, new[] { Text("ABC") });
        var glyphs = layout.Glyphs.ToArray();
        var bounds = layout.BoundingBox;
        var animation = new DialogueTextAnimation(layout, 0, 50, 200);
        Assert.Equal(new float[] { 0, 0, 0 }, layout.OpacityValues.ToArray());
        animation.Update(100);
        Assert.Equal(new float[] { .5f, .25f, 0 }, layout.OpacityValues.ToArray());
        Assert.True(animation.IsActive);
        animation.Update(200);
        Assert.Equal(new float[] { 1, 1, 1 }, layout.OpacityValues.ToArray());
        Assert.False(animation.IsActive);
        Assert.Equal(glyphs, layout.Glyphs.ToArray());
        Assert.Equal(bounds, layout.BoundingBox);
    }

    [Fact]
    public void AppendedTextKeepsPreviouslyDisplayedCharactersVisible()
    {
        var layout = new TextLayout(fonts.Rasterizer, new[] { Text("AB") });
        layout.Append(fonts.Rasterizer, Text("CD"));
        var animation = new DialogueTextAnimation(layout, 2, 48, 200);
        Assert.Equal(new float[] { 1, 1, 0, 0 }, layout.OpacityValues.ToArray());
        animation.Update(48);
        Assert.Equal(1, layout.OpacityValues[0]);
        Assert.Equal(.24f, layout.OpacityValues[2], 4);
        animation.Complete();
        animation.Complete();
        Assert.All(layout.OpacityValues.ToArray(), value => Assert.Equal(1, value));
        Assert.False(animation.IsActive);
    }

    [Fact]
    public void RubyAppearsWithItsBaseInsteadOfDelayingFollowingCharacters()
    {
        var ruby = TextRun.WithRubyText("村正".AsMemory(), "むらまさ".AsMemory(),
            fonts.DefaultFont, new PtFontSize(20), RgbaFloat.White, null);
        var layout = new TextLayout(fonts.Rasterizer, new[] { ruby, Text("後") });
        var animation = new DialogueTextAnimation(layout, 0, 100, 100);
        Assert.True(animation.CharacterCount == 3,
            string.Join("; ", layout.GlyphRuns.ToArray().Select(r => $"{r.GlyphSpan} {r.Flags}")));
        animation.Update(50);
        foreach (ref readonly var run in layout.GlyphRuns)
        {
            var values = layout.GetOpacityValues(run.GlyphSpan);
            if (run.IsRubyText)
            {
                Assert.Equal(.5f, values[0]);
                Assert.Equal(0, values[^1]);
            }
        }
        animation.Update(250);
        Assert.All(layout.OpacityValues.ToArray(), value => Assert.Equal(1, value));
        Assert.False(animation.IsActive);
    }

    [Fact]
    public void InstantDisplayEmptySegmentsAndNegativeDeltaAreStable()
    {
        var layout = new TextLayout(fonts.Rasterizer, new[] { Text("AB") });
        var instant = new DialogueTextAnimation(layout, 0, 0, 0);
        Assert.False(instant.IsActive);
        Assert.All(layout.OpacityValues.ToArray(), value => Assert.Equal(1, value));
        var empty = new DialogueTextAnimation(layout, 2, 48, 200);
        Assert.False(empty.IsActive);
        Assert.Equal(0, empty.CharacterCount);
        var animation = new DialogueTextAnimation(layout, 0, 48, 200);
        animation.Update(-100);
        Assert.Equal(new float[] { 0, 0 }, layout.OpacityValues.ToArray());
    }
}
