using System;
using System.Collections.Generic;
using System.Numerics;
using NitroSharp.NsScript;
using NitroSharp.Text;
using Veldrid;

namespace NitroSharp.Graphics;

internal readonly record struct BacklogEntry(string Text, VoiceSegment? Voice);

internal sealed class BacklogView(EntityName name, Entity? parent, int priority, BacklogEntry[] entries)
    : RenderItem(name, parent, priority)
{
    private readonly List<(TextLayout Layout, VoiceSegment? Voice)> _rows = [];
    private bool _laidOut;
    private int _scroll;
    private int _pageSize = 17;
    private float _lineHeight = 30;
    private Vector2 _position;
    private float _voiceX;

    public override DesignSize GetSize(RenderContext ctx) => ctx.DesignResolution.ToFloatSize();

    public bool Scroll(NsEntityAction action)
    {
        int delta = action switch
        {
            NsEntityAction.BacklogPreviousPage => _pageSize,
            NsEntityAction.BacklogNextPage => -_pageSize,
            NsEntityAction.BacklogPreviousLine => 1,
            NsEntityAction.BacklogNextLine => -1,
            _ => 0
        };
        if (delta == 0) return false;
        ScrollRows(delta);
        return true;
    }

    private void ScrollRows(int delta) => _scroll = Math.Clamp(_scroll + delta, 0, Math.Max(0, _rows.Count - 1));

    public override void Update(GameContext ctx)
    {
        base.Update(ctx);
        RenderContext render = ctx.RenderContext;
        if (!_laidOut)
        {
            var system = ctx.VM.SystemVariables;
            _pageSize = (int)Math.Clamp(system.BacklogRowMax.AsNumber() ?? 17, 1, 100);
            _lineHeight = Math.Max(12, system.BacklogRowInterval.AsNumber() ?? 30);
            _position = new Vector2(system.BacklogPositionX.AsNumber() ?? 50, system.BacklogPositionY.AsNumber() ?? 50);
            _voiceX = ctx.VM.GetHostVariable("SYSTEM_backlog_voice_icon_x").AsNumber() ?? Math.Max(0, _position.X - 30);
            float width = Math.Max(40, render.DesignResolution.Width - _position.X - 110);
            var font = Process.FontSettings;
            foreach (BacklogEntry entry in entries)
            {
                var layout = new TextLayout((uint)width, uint.MaxValue, fixedLineHeight: _lineHeight);
                layout.Append(render.GlyphRasterizer, TextRun.Regular(entry.Text.AsMemory(), font.DefaultFont,
                    font.DefaultFontSize, new RgbaFloat(font.DefaultTextColor), null));
                _rows.Add((layout, entry.Voice));
            }
            _laidOut = true;
        }
        if (!ctx.Builtins.IsBacklogOpeningFrame && ctx.InputContext.WheelDelta != 0)
            ScrollRows(Math.Sign(ctx.InputContext.WheelDelta));

        Icon? speaker = render.Icons.BacklogVoice;
        Vector2 pointer = Scrollbar.ToDesignPosition(render, ctx.InputContext.MousePosition);
        foreach (var (layout, voice, y) in VisibleRows())
        {
            render.Text.RequestGlyphs(layout);
            if (voice is not null && speaker is not null && ctx.InputContext.ChoicePointerPressed
                && pointer.X >= _voiceX && pointer.X < _voiceX + speaker.Size.Width
                && pointer.Y >= y && pointer.Y < y + speaker.Size.Height
                && ctx.InputContext.ConsumeChoicePress(pointer: true))
                ctx.Builtins.ApplyDialogueVoice(Process, voice, skipping: false);
        }
    }

    private IEnumerable<(TextLayout Layout, VoiceSegment? Voice, float Y)> VisibleRows()
    {
        int end = _rows.Count - _scroll;
        float remaining = _pageSize * _lineHeight;
        int start = end;
        while (start > 0 && remaining > 0)
            remaining -= RowHeight(_rows[--start].Layout);
        float y = _position.Y;
        for (int i = start; i < end; i++)
        {
            var (layout, voice) = _rows[i];
            yield return (layout, voice, y);
            y += RowHeight(layout);
        }
    }

    private float RowHeight(TextLayout layout) => Math.Max(1, layout.Lines.Length) * _lineHeight;

    public override void Render(GameContext ctx)
    {
        RenderContext render = ctx.RenderContext;
        var clip = new ScreenRectU(PointU<ScreenPixel>.Zero, render.RenderResolution);
        foreach (var (layout, voice, y) in VisibleRows())
        {
            Transform transform = Transform;
            transform.Position += new Vector3(_position.X, y, 0);
            Matrix4x4 matrix = render.GetTransformMatrix(transform, default, useScaling: true, aligned: false);
            render.Text.Render(render, render.MainBatch, layout, matrix, Vector2.Zero, clip, Color.A);
            if (voice is not null && render.Icons.BacklogVoice is { } icon)
                icon.Render(render, Matrix4x4.CreateTranslation(_voiceX - _position.X, 0, 0) * matrix, 0, Color.A);
        }
    }
}
