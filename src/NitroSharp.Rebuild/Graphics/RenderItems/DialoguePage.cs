using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using NitroSharp.Input;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;
using NitroSharp.Text;

namespace NitroSharp.Graphics;

internal sealed partial class DialoguePage : RenderItem
{
    private readonly DialogueBlockToken _token;
    private readonly string _readKey;
    private readonly CompiledDialogueBlock _block;
    private TextLayout _layout;
    private bool _verticalGlyphs;
    private readonly Queue<DialogueSegment> _segments = new();
    private readonly Vector4 _margin = new(0, 10, 0, 0);
    private int _partIndex;
    private bool _waitingForInput;
    private bool _unreadText;
    private bool _clearBeforeMarkup;
    private bool _disableTextAnimation;
    private bool _entered;
    private bool _destroyWhenIdle;
    private bool _noBacklog;
    private bool _noWaitIcon;
    private bool _lineWait;
    private VoiceSegment? _backlogVoice;
    private DialogueTextAnimation? _textAnimation;
    private int _charactersSinceInput;
    private float _waitElapsed;
    private Thread? _codeThread;
    internal Entity? ScriptScope { get; set; }

    public DialoguePage(EntityName name, Entity parent, int priority,
        in DialogueBlockToken token, uint maxWidth, uint maxHeight, int lineSpacing, float fontScale)
        : base(name, parent, priority)
    {
        // Loading prepares a page; the script enters it after its window is ready.
        base.Request(NsEntityAction.Disable);
        SaveLineSpacing = lineSpacing;
        _token = token;
        _readKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{token.Module.Name}\0{token.Module.GetSubroutineName(token.SubroutineIndex)}\0{token.BlockName}"));
        _verticalGlyphs = parent.Process.FontSettings.VerticalGlyphs;
        _block = token.Module.GetSubroutine(token.SubroutineIndex).DialogueBlocks[token.Offset];
        _layout = new TextLayout(maxWidth, maxHeight,
            fixedLineHeight: lineSpacing > 0 ? lineSpacing : null, fontScale: fontScale);
    }

    public bool IsIdle { get; private set; } = true;
    internal bool IsRevealing => _textAnimation is { IsActive: true };
    public override DesignSize GetSize(RenderContext ctx) => TextBlock.GetTextSize(_layout, _margin);

    public override bool Request(NsEntityAction action)
    {
        if (action == NsEntityAction.Enable && !_entered)
        {
            _entered = true;
            IsIdle = false;
        }
        else if (action == NsEntityAction.DestroyWhenIdle)
        {
            _destroyWhenIdle = true;
            if (IsIdle) World.DestroyEntity(this);
            return true;
        }
        if (action == NsEntityAction.NoTextAnimation)
        {
            _disableTextAnimation = true;
            _textAnimation?.Complete();
            return true;
        }
        if (action == NsEntityAction.NoBacklog)
        {
            _noBacklog = true;
            return true;
        }
        if (action == NsEntityAction.NoWaitIcon)
        {
            _noWaitIcon = true;
            return true;
        }
        return base.Request(action);
    }

    public override void Update(GameContext ctx)
    {
        base.Update(ctx);
        if (!_entered || IsPendingDestruction) return;
        ctx.RenderContext.Text.RequestGlyphs(_layout);
        if (ctx.VM.SystemVariables.IsTextErased)
        {
            if (ctx.InputContext.Consume(InputAction.Advance))
                ctx.VM.SystemVariables.TextErase = ConstantValue.False;
            return;
        }
        var settings = ctx.VM.SystemVariables.GetTextSettings();
        bool skip = ctx.StorySkipping;
        if (skip && !IsIdle && !(ctx.VM.GetHostFlag("SYSTEM_skip_absolute").AsBool() ?? false)
            && !ctx.Saves.HasRead(_readKey))
        {
            ctx.VM.SystemVariables.CancelSkip();
            skip = false;
        }
        if (IsRevealing)
        {
            if (skip || _disableTextAnimation
                || (settings.WaitKey?.AsBool() ?? true) && ctx.InputContext.Consume(InputAction.Advance))
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
            if (!skip && !(auto && _waitElapsed >= autoWait)
                && (!(settings.WaitKey?.AsBool() ?? true)
                    || !ctx.InputContext.Consume(InputAction.Advance))) return;
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
                    float interval = settings.Interval?.AsNumber() ?? 48;
                    if (ctx.Profile.DefaultTextSpeed.HasValue)
                    {
                        // Early NSS exposes a 0–1000 speed slider. Zero reveals instantly.
                        interval = settings.Interval?.AsNumber() is > 0 and var explicitInterval
                            ? explicitInterval : duration * 0.05f;
                        duration = Math.Min(duration, 120);
                    }
                    bool animate = !_disableTextAnimation && !skip && duration > 0;
                    _textAnimation = new DialogueTextAnimation(_layout, start,
                        animate ? interval : 0, animate ? duration : 0);
                    if (!_noBacklog && !ctx.World.IsSystemMenu)
                    {
                        var content = new StringBuilder();
                        foreach (TextRun run in text.TextRuns) content.Append(run.Text.Span);
                        ctx.Builtins.AddBacklogText(content.ToString(), _backlogVoice);
                    }
                    _backlogVoice = null;
                    _charactersSinceInput += _textAnimation.CharacterCount;
                    _unreadText = true;
                    if (IsRevealing) goto done;
                }
                else if (segment is VoiceSegment voice)
                {
                    _backlogVoice = voice.Action == NsVoiceAction.Play ? voice : null;
                    ctx.Builtins.ApplyDialogueVoice(Process, voice, skip);
                }
                else if (segment is MarkerSegment { MarkerKind: MarkerKind.Halt })
                {
                    _waitingForInput = true;
                    _lineWait = true;
                    _unreadText = false;
                    goto done;
                }
            }
            if (_partIndex == _block.Parts.Length)
            {
                if (_unreadText)
                {
                    _waitingForInput = true;
                    _lineWait = false;
                    _unreadText = false;
                    goto done;
                }
                if (!ctx.World.IsSystemMenu) ctx.Saves.MarkRead(_readKey);
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
                    string dialogueMarkup = markup.GetText(_token.Module);
                    ctx.VM.SystemVariables.LastText = ConstantValue.String(dialogueMarkup);
                    Dialogue dialogue = Dialogue.Parse(dialogueMarkup, Process.FontSettings);
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
                        _lineWait = false;
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
        if (IsIdle && _destroyWhenIdle) World.DestroyEntity(this);
        ctx.RenderContext.Text.RequestGlyphs(_layout);
    }

    public override void Render(GameContext ctx)
    {
        if (!_entered || IsPendingDestruction || ctx.VM.SystemVariables.IsTextErased) return;
        RenderContext render = ctx.RenderContext;
        Matrix4x4 transform = render.GetTransformMatrix(Transform, GetSize(render), useScaling: true, aligned: false);
        render.Text.Render(render, render.MainBatch, _layout,
            transform,
            new Vector2(_margin.X, _margin.Y),
            new ScreenRectU(PointU<ScreenPixel>.Zero, render.RenderResolution), Color.A,
            verticalGlyphs: _verticalGlyphs);
        if (!_waitingForInput || IsRevealing || _noWaitIcon || _layout.Glyphs.IsEmpty
            || ctx.StorySkipping || ctx.VM.GetHostVariable("SYSTEM_text_icon_disable").AsBool() == true)
            return;

        var icons = render.Icons;
        Icon? icon = (ctx.VM.SystemVariables.TextAuto.AsBool() == true ? icons.WaitAuto : null)
            ?? (_lineWait ? icons.WaitLine : icons.WaitPage) ?? icons.WaitLine;
        if (icon is null) return;
        Vector2 end = _layout.EndOfLastLine;
        end += new Vector2(_margin.X + 6, _margin.Y - icon.Size.Height * 0.55f);
        icon.Render(render, Matrix4x4.CreateTranslation(new Vector3(end, 0)) * transform, _waitElapsed, Color.A);
    }
}
