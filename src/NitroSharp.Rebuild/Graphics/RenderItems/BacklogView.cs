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
    private readonly List<(TextLayout Layout, VoiceSegment? Voice, int FirstLine)> _rows = [];
    private bool _laidOut;
    private int _lineCount;
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

    private void ScrollRows(int delta) => _scroll = Math.Clamp(_scroll + delta, 0, Math.Max(0, _lineCount - _pageSize));

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
                if (layout.Lines.IsEmpty) continue;
                _rows.Add((layout, entry.Voice, _lineCount));
                _lineCount += layout.Lines.Length;
            }
            _laidOut = true;
        }
        if (!ctx.Builtins.IsBacklogOpeningFrame && ctx.InputContext.WheelDelta != 0)
            ScrollRows(Math.Sign(ctx.InputContext.WheelDelta));

        Icon? speaker = render.Icons.BacklogVoice;
        Vector2 pointer = Scrollbar.ToDesignPosition(render, ctx.InputContext.MousePosition);
        foreach (var (layout, voice, y, _, _) in VisibleRows())
        {
            render.Text.RequestGlyphs(layout);
            if (voice is not null && speaker is not null && ctx.InputContext.ChoicePointerPressed
                && pointer.X >= _voiceX && pointer.X < _voiceX + speaker.Size.Width
                && pointer.Y >= y && pointer.Y < y + speaker.Size.Height
                && ctx.InputContext.ConsumeChoicePress(pointer: true))
                ctx.Builtins.ReplayBacklogVoice(Process, voice);
        }
    }

    private IEnumerable<(TextLayout Layout, VoiceSegment? Voice, float Y, int FirstLine, int EndLine)> VisibleRows()
    {
        int end = _lineCount - _scroll;
        int start = Math.Max(0, end - _pageSize);
        foreach (var (layout, voice, firstLine) in _rows)
        {
            if (firstLine >= end) yield break;
            int firstVisible = Math.Max(firstLine, start);
            int endVisible = Math.Min(firstLine + layout.Lines.Length, end);
            if (firstVisible >= endVisible) continue;
            float y = _position.Y + (firstVisible - start) * _lineHeight;
            yield return (layout, firstLine == firstVisible ? voice : null, y,
                firstVisible - firstLine, endVisible - firstLine);
        }
    }

    public override void Render(GameContext ctx)
    {
        RenderContext render = ctx.RenderContext;
        var clip = new ScreenRectU(PointU<ScreenPixel>.Zero, render.RenderResolution);
        foreach (var (layout, voice, y, firstLine, endLine) in VisibleRows())
        {
            Transform transform = Transform;
            transform.Position += new Vector3(_position.X, y, 0);
            Matrix4x4 matrix = render.GetTransformMatrix(transform, default, useScaling: true, aligned: false);
            int glyphStart = layout.Lines[firstLine].GlyphSpan.Start.Value;
            int glyphEnd = layout.Lines[endLine - 1].GlyphSpan.End.Value;
            var offset = new Vector2(0, -firstLine * _lineHeight);
            foreach (var run in layout.GlyphRuns)
            {
                int start = Math.Max(glyphStart, run.GlyphSpan.Start.Value);
                int end = Math.Min(glyphEnd, run.GlyphSpan.End.Value);
                if (start < end)
                    render.Text.Render(render, render.MainBatch, layout, run.WithSpan(start..end), matrix, offset, clip, Color.A);
            }
            if (voice is not null && render.Icons.BacklogVoice is { } icon)
                icon.Render(render, Matrix4x4.CreateTranslation(_voiceX - _position.X, 0, 0) * matrix, 0, Color.A);
        }
    }
}
