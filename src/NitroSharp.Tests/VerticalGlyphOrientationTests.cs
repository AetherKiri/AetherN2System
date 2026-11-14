using System;
using System.Numerics;
using System.Text;
using NitroSharp.Text;
using Xunit;

namespace NitroSharp.Tests;

public sealed class VerticalGlyphOrientationTests
{
    [Theory]
    [InlineData("村", true)]
    [InlineData("あ", true)]
    [InlineData("１", true)]
    [InlineData("。", true)]
    [InlineData("𠮷", true)]
    [InlineData("A", false)]
    [InlineData("1", false)]
    [InlineData("「", false)]
    [InlineData("」", false)]
    [InlineData("ー", false)]
    [InlineData("…", false)]
    public void MixedTextUsesItsVerticalOrientation(string text, bool upright)
        => Assert.Equal(upright, VerticalGlyphOrientation.IsUpright(Rune.GetRuneAt(text, 0)));

    [Fact]
    public void CjkStaysUprightAndCenteredAfterPageRotation()
    {
        var position = new Vector2(14, 31);
        var size = new Vector2(20, 24);
        Vector2 center = position + size / 2;
        Matrix4x4 page = Matrix4x4.CreateRotationZ(MathF.PI / 2)
            * Matrix4x4.CreateTranslation(575, 55, 0);
        Matrix4x4 local = VerticalGlyphOrientation.GetLocalTransform(new Rune('村'), position, size, 24);
        Matrix4x4 combined = local * page;

        Near(Vector2.UnitX, Vector2.TransformNormal(Vector2.UnitX, combined));
        Near(Vector2.UnitY, Vector2.TransformNormal(Vector2.UnitY, combined));
        Near(Vector2.Transform(center, page), Vector2.Transform(center, combined));
    }

    [Fact]
    public void LatinAndBracketsFollowThePageRotation()
    {
        Matrix4x4 page = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        foreach (char character in "ABC12「」ー")
        {
            Matrix4x4 local = VerticalGlyphOrientation.GetLocalTransform(new Rune(character),
                Vector2.Zero, new Vector2(12, 24), 24);
            Assert.Equal(Matrix4x4.Identity, local);
            Near(Vector2.UnitY, Vector2.TransformNormal(Vector2.UnitX, local * page));
        }
    }

    [Theory]
    [InlineData('、')]
    [InlineData('。')]
    public void SmallPunctuationMovesToUpperRightOfVerticalCell(char character)
    {
        var position = new Vector2(10, 20);
        var size = new Vector2(6, 6);
        Vector2 center = position + size / 2;
        Matrix4x4 page = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        Matrix4x4 local = VerticalGlyphOrientation.GetLocalTransform(new Rune(character), position, size, 20);
        Vector2 shift = Vector2.Transform(center, local * page) - Vector2.Transform(center, page);
        Assert.True(shift.X > 0);
        Assert.True(shift.Y < 0);
    }

    private static void Near(Vector2 expected, Vector2 actual)
        => Assert.True(Vector2.Distance(expected, actual) < .001f, $"Expected {expected}, got {actual}.");
}
