using System;
using System.Numerics;
using NitroSharp.Text;

namespace NitroSharp.Graphics;

internal sealed class TextBlock : RenderItem
{
    private readonly TextLayout _layout;
    private readonly Vector4 _margin;
    private readonly bool _verticalGlyphs;

    public TextBlock(EntityName name, Entity? parent, int priority, string markup,
        TextRenderContext ctx, DesignSizeU maxBounds, FontSettings fontSettings, Vector4 margin)
        : base(name, parent, priority)
    {
        _margin = margin;
        _verticalGlyphs = fontSettings.VerticalGlyphs;
        _layout = new TextLayout(maxBounds.Width, maxBounds.Height,
            rubyFontSizeMultiplier: fontSettings.RubyFontSizeMultiplier);
        foreach (DialogueSegment segment in Dialogue.Parse(markup, fontSettings).Segments)
        {
            if (segment is TextSegment text)
            {
                _layout.Append(ctx.GlyphRasterizer, text.TextRuns.AsSpan());
            }
        }
        ctx.RequestGlyphs(_layout);
    }

    public override DesignSize GetSize(RenderContext ctx)
        => GetTextSize(_layout, _margin);

    internal static DesignSize GetTextSize(TextLayout layout, Vector4 margin)
    {
        if (layout.Glyphs.IsEmpty) { return new DesignSize(0, 0); }
        DesignRect bb = layout.BoundingBox;
        return new DesignSize(MathF.Max(0, margin.X + bb.Right + margin.Z),
            MathF.Max(0, margin.Y + bb.Bottom + margin.W)).Constrain(layout.MaxBounds);
    }

    public override void Update(GameContext ctx)
    {
        base.Update(ctx);
        ctx.RenderContext.Text.RequestGlyphs(_layout);
    }

    public override void Render(GameContext ctx)
    {
        RenderContext render = ctx.RenderContext;
        render.Text.Render(render, render.MainBatch, _layout,
            render.GetTransformMatrix(Transform, GetSize(render), useScaling: true, aligned: false),
            new Vector2(_margin.X, _margin.Y),
            new ScreenRectU(PointU<ScreenPixel>.Zero, render.RenderResolution), Color.A,
            verticalGlyphs: _verticalGlyphs);
    }
}
