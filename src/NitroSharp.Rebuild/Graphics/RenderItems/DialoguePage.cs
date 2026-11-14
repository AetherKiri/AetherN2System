using System.Collections.Generic;
using System.Numerics;
using NitroSharp.Input;
using NitroSharp.NsScript.VM;
using NitroSharp.Text;

namespace NitroSharp.Graphics;

internal sealed class DialoguePage : RenderItem
{
    private readonly DialogueBlockToken _token;
    private readonly CompiledDialogueBlock _block;
    private readonly TextLayout _layout;
    private readonly Queue<DialogueSegment> _segments = new();
    private readonly Vector4 _margin = new(0, 10, 0, 0);
    private int _partIndex;
    private bool _waitingForInput;
    private bool _unreadText;
    private bool _clearBeforeMarkup;
    private Thread? _codeThread;

    public DialoguePage(EntityName name, Entity parent, int priority,
        in DialogueBlockToken token, uint maxWidth, uint maxHeight, int lineSpacing)
        : base(name, parent, priority)
    {
        _token = token;
        _block = token.Module.GetSubroutine(token.SubroutineIndex).DialogueBlocks[token.Offset];
        _layout = new TextLayout(maxWidth, maxHeight,
            fixedLineHeight: lineSpacing > 0 ? lineSpacing : null);
    }

    public bool IsIdle { get; private set; }
    public override DesignSize GetSize(RenderContext ctx) => TextBlock.GetTextSize(_layout, _margin);

    public override void Update(GameContext ctx)
    {
        base.Update(ctx);
        ctx.RenderContext.Text.RequestGlyphs(_layout);
        if (_codeThread is { } thread)
        {
            if (!thread.State.DoneExecuting) { return; }
        }
        if (_waitingForInput)
        {
            if (!ctx.InputContext.Consume(InputAction.Advance)) { return; }
            _waitingForInput = false;
        }

        while (!IsIdle)
        {
            while (_segments.TryDequeue(out DialogueSegment? segment))
            {
                if (segment is TextSegment text)
                {
                    _layout.Append(ctx.RenderContext.Text.GlyphRasterizer, text.TextRuns.AsSpan());
                    _unreadText = true;
                }
                else if (segment is MarkerSegment { MarkerKind: MarkerKind.Halt })
                {
                    _waitingForInput = true;
                    _unreadText = false;
                    goto done;
                }
            }
            if (_partIndex == _block.Parts.Length)
            {
                if (_unreadText)
                {
                    _waitingForInput = true;
                    _unreadText = false;
                    goto done;
                }
                IsIdle = true;
                break;
            }
            switch (_block.Parts[_partIndex++])
            {
                case CompiledDialogueBlockPart.Markup markup:
                    if (_clearBeforeMarkup)
                    {
                        _layout.Clear();
                        _clearBeforeMarkup = false;
                    }
                    Dialogue dialogue = Dialogue.Parse(markup.GetText(_token.Module), Process.FontSettings);
                    foreach (DialogueSegment segment in dialogue.Segments)
                    {
                        _segments.Enqueue(segment);
                    }
                    break;
                case CompiledDialogueBlockPart.BlankLine:
                    // Inline code belongs to the same page; only a blank line ends it.
                    _clearBeforeMarkup = true;
                    if (_unreadText)
                    {
                        _waitingForInput = true;
                        _unreadText = false;
                        goto done;
                    }
                    break;
                case CompiledDialogueBlockPart.CodeBlock code:
                    NsScriptThreadState state = ctx.VM.CreateDialogueCodeThread(_token, code);
                    if (_codeThread is null)
                    {
                        _codeThread = World.AddEntity(new Thread(EntityName.Parse("dialogue-code"), this,
                            state, isMain: false));
                    }
                    else
                    {
                        // Preserve entities created by previous inline blocks until the page is deleted.
                        _codeThread.Restart(state);
                    }
                    goto done;
            }
        }
    done:
        ctx.RenderContext.Text.RequestGlyphs(_layout);
    }

    public override void Render(GameContext ctx)
    {
        RenderContext render = ctx.RenderContext;
        render.Text.Render(render, render.MainBatch, _layout,
            render.GetTransformMatrix(Transform, GetSize(render), useScaling: true, aligned: false),
            new Vector2(_margin.X, _margin.Y),
            new ScreenRectU(PointU<ScreenPixel>.Zero, render.RenderResolution), Color.A);
    }
}
