using System;
using System.Collections.Generic;
using System.Numerics;
using NitroSharp.Input;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;
using NitroSharp.Text;

namespace NitroSharp.Graphics;

internal sealed class DialoguePage : RenderItem
{
    private readonly DialogueBlockToken _token;
    private readonly CompiledDialogueBlock _block;
    private readonly TextLayout _layout;
    private readonly bool _verticalGlyphs;
    private readonly Queue<DialogueSegment> _segments = new();
    private readonly Vector4 _margin = new(0, 10, 0, 0);
    private int _partIndex;
    private bool _waitingForInput;
    private bool _unreadText;
    private bool _clearBeforeMarkup;
    private bool _disableTextAnimation;
    private DialogueTextAnimation? _textAnimation;
    private int _charactersSinceInput;
    private float _waitElapsed;
    private Thread? _codeThread;

    public DialoguePage(EntityName name, Entity parent, int priority,
        in DialogueBlockToken token, uint maxWidth, uint maxHeight, int lineSpacing)
        : base(name, parent, priority)
    {
        _token = token;
        _verticalGlyphs = parent.Process.FontSettings.VerticalGlyphs;
        _block = token.Module.GetSubroutine(token.SubroutineIndex).DialogueBlocks[token.Offset];
        _layout = new TextLayout(maxWidth, maxHeight,
            fixedLineHeight: lineSpacing > 0 ? lineSpacing : null);
    }

    public bool IsIdle { get; private set; }
    internal bool IsRevealing => _textAnimation is { IsActive: true };
    public override DesignSize GetSize(RenderContext ctx) => TextBlock.GetTextSize(_layout, _margin);

    public override bool Request(NsEntityAction action)
    {
        if (action == NsEntityAction.NoTextAnimation)
        {
            _disableTextAnimation = true;
            _textAnimation?.Complete();
            return true;
        }
        return base.Request(action);
    }

    public override void Update(GameContext ctx)
    {
        base.Update(ctx);
        ctx.RenderContext.Text.RequestGlyphs(_layout);
        var settings = ctx.VM.SystemVariables.GetTextSettings();
        bool skip = (ctx.VM.SystemVariables.Skip.AsBool() ?? false)
            || ctx.InputContext.IsActionHeld(InputAction.Skip);
        if (IsRevealing)
        {
            if (skip || _disableTextAnimation || ctx.InputContext.Consume(InputAction.Advance))
            {
                _textAnimation!.Complete();
                // The press that reveals a sentence must not also pass its halt.
                if (!skip) return;
            }
            else
            {
                _textAnimation!.Update(ctx.DeltaTime);
                if (IsRevealing) return;
            }
        }
        if (_codeThread is { } thread)
        {
            if (!thread.State.DoneExecuting) { return; }
        }
        if (_waitingForInput)
        {
            _waitElapsed += ctx.DeltaTime;
            bool auto = ctx.VM.SystemVariables.TextAuto.AsBool() ?? false;
            float autoWait = MathF.Max(0, settings.AutoWaitPerCharacter?.AsNumber() ?? 100)
                * Math.Max(1, _charactersSinceInput);
            if (!skip && (settings.WaitKey?.AsBool() ?? true)
                && !(auto && _waitElapsed >= autoWait)
                && !ctx.InputContext.Consume(InputAction.Advance)) return;
            _waitingForInput = false;
            _waitElapsed = 0;
            _charactersSinceInput = 0;
        }

        while (!IsIdle)
        {
            while (_segments.TryDequeue(out DialogueSegment? segment))
            {
                if (segment is TextSegment text)
                {
                    int start = _layout.Glyphs.Length;
                    _layout.Append(ctx.RenderContext.Text.GlyphRasterizer, text.TextRuns.AsSpan());
                    float duration = settings.Speed?.AsNumber() ?? 200;
                    bool animate = !_disableTextAnimation && !skip && duration > 0;
                    _textAnimation = new DialogueTextAnimation(_layout, start,
                        animate ? settings.Interval?.AsNumber() ?? 48 : 0, animate ? duration : 0);
                    _charactersSinceInput += _textAnimation.CharacterCount;
                    _unreadText = true;
                    if (IsRevealing) goto done;
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
            new ScreenRectU(PointU<ScreenPixel>.Zero, render.RenderResolution), Color.A,
            verticalGlyphs: _verticalGlyphs);
    }
}
