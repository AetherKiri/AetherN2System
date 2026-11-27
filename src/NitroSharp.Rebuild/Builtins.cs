using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using NitroSharp.Common;
using NitroSharp.Content;
using NitroSharp.Graphics;
using NitroSharp.Input;
using NitroSharp.Media;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.VM;
using NitroSharp.Text;
using Veldrid;
using ZeroLog;

namespace NitroSharp;

internal sealed partial class Builtins : BuiltInFunctions
{
    private static readonly string[] s_mouseStates = ["MouseUsual", "MouseClick", "MouseOver", "MouseLeave"];

    private readonly GameContext _ctx;
    private readonly World _world;
    private readonly Log _log;
    private readonly Dictionary<Thread, ChoiceInputState> _choiceInputs = [];
    private Scrollbar? _focusedScrollbar;
    private Scrollbar? _draggedScrollbar;
    private bool _twitterUnavailableReported;

    private sealed class ChoiceInputState
    {
        public ChoiceEntity? Focused;
        public readonly Dictionary<ChoiceEntity, long> LastSeen = [];
    }

    public Builtins(GameContext gameContext)
    {
        _ctx = gameContext;
        _world = gameContext.World;
        _log = gameContext.RootLog;
    }

    private Thread CurrentThread => _world.CurrentProcess.CurrentThread;
    private Stopwatch Clock => _ctx.Clock;

    private SmallList<Entity> Query(in EntityQuery query) => _world.Query(query);

    private bool ResolveEntityPath(in EntityPath path, out EntityName name, out Entity? parent)
    {
        if (_world.TryResolvePath(path, out name, out parent)) { return true; }

        _log.Warn($"Failed to resolve entity path: \"{path.ToString()}\"");
        return false;
    }

    protected override void NotImplemented(string functionName = "")
    {
        _log.Warn($"Thread '{CurrentThread.Name.ToString()}' | {functionName} is not implemented");
    }

    protected override T NotImplemented<T>(string functionName = "")
    {
        NotImplemented(functionName);
        return default!;
    }

    public override void ReportInvalidArgument(string functionName, string argumentName, in ConstantValue value)
        => _log.Warn($"Thread '{CurrentThread.Name.ToString()}' | {functionName}: invalid {argumentName} argument {value.ToString()}; retaining the current value");

    public override void EnterCallScope() => CurrentThread.EnterCallScope();
    public override void LeaveCallScope() => CurrentThread.LeaveCallScope();

    public override void Exit() => _ctx.ShutdownSignal.Cancel();

    public override void Reset()
    {
        NsScriptThreadState startup = VM.CreateThread(_ctx.Profile.SysScripts.Startup, "main")
            ?? throw new InvalidOperationException("The startup script has no main entry point.");
        _choiceInputs.Clear();
        _focusedScrollbar = null;
        _draggedScrollbar = null;
        _world.RequestReset(startup);
    }

    public override int GetPlatformId() => 100;

    public override string GetCurrentModuleName()
        => CurrentModule.Name.EndsWith(".nss", StringComparison.OrdinalIgnoreCase)
            ? CurrentModule.Name : CurrentModule.Name + ".nss";

    public override int GetSecondsElapsed() => (int)Clock.Elapsed.TotalSeconds;

    public override void InitializeTwitter(string writablePath)
    {
        if (_twitterUnavailableReported) return;
        _twitterUnavailableReported = true;
        _log.Info("Twitter integration is unavailable; account and tweet operations stay offline.");
    }

    public override void CreateEntity(in EntityPath entityPath)
    {
        if (ResolveEntityPath(entityPath, out EntityName name, out Entity? parent))
        {
            _world.AddEntity(new BlankEntity(name, parent));
        }
    }

    public override void LoadImage(in EntityPath entityPath, string source)
    {
        if (ResolveEntityPath(entityPath, out EntityName name, out Entity? parent))
        {
            _world.AddEntity(new Image(name, parent, GetSpriteTexture(source)));
        }
    }

    public override void CreateSprite(in EntityPath entityPath, int priority, NsCoordinate x, NsCoordinate y, string source)
    {
        if (ResolveEntityPath(entityPath, out EntityName name, out Entity? parent))
        {
            _world.AddEntity(new Sprite(name, parent, priority, GetSpriteTexture(source)))
                .WithPosition(_ctx.RenderContext, x, y);
        }
    }

    public override void CreateEffect(in EntityPath entityPath, int priority, NsCoordinate x, NsCoordinate y,
        uint width, uint height, string effectName)
    {
        if (!effectName.Equals("Plain", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"Thread '{CurrentThread.Name.Value}' | CreateEffect '{effectName}' is not implemented");
            return;
        }
        if (!ResolveEntityPath(entityPath, out EntityName name, out Entity? parent)) return;
        RenderContext render = _ctx.RenderContext;
        var effect = new PlainEffect(name, parent, priority, new DesignSizeU(width, height),
            rectangle => render.CapturePresentedFrame(rectangle),
            texture => GraphicsUtils.DisposeAfterIdle(render.GraphicsDevice, texture))
            .WithPosition(render, x, y);
        effect.RefreshSnapshot();
        _world.AddEntity(effect);
    }

    public override void CreateSurface(in EntityPath path, int priority, uint width, uint height, bool transparent)
    {
        if (transparent) throw new NotSupportedException("A transparent drawing surface is not supported.");
        RenderContext render = _ctx.RenderContext;
        if (width == 0) width = render.DesignResolution.Width;
        if (height == 0) height = render.DesignResolution.Height;
        if (width > render.DesignResolution.Width || height > render.DesignResolution.Height)
            throw new ArgumentOutOfRangeException(nameof(width), "The drawing surface exceeds the design resolution.");
        if (ResolveEntityPath(path, out EntityName name, out Entity? parent))
        {
            var texture = SpriteTexture.SolidColor(RgbaFloat.Clear, new DesignSizeU(width, height));
            _world.AddEntity(new Sprite(name, parent, priority, texture));
        }
    }

    public override void SetSurface(in EntityQuery effect, string target)
    {
        Sprite? surface = null;
        if (target.Length != 0 && !target.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            var targets = Query(EntityQuery.Parse(target));
            if (targets.AsSpan() is not [Sprite sprite])
                throw new InvalidOperationException($"Drawing surface '{target}' was not found or is ambiguous.");
            surface = sprite;
        }
        foreach (Entity entity in Query(effect))
        {
            if (entity is PlainEffect plain) plain.Surface = surface;
        }
    }

    public override void Draw()
    {
        Process process = _world.CurrentProcess;
        RenderContext render = _ctx.RenderContext;
        foreach (Entity entity in process.DescendantsAndSelf())
        {
            if (entity is not PlainEffect { Surface: { IsPendingDestruction: false } surface } effect) continue;
            // Draw records the current composition before the script changes its layers.
            long started = Stopwatch.GetTimestamp();
            var wait = new System.Threading.SpinWait();
            while (!_ctx.Content.ResolveAssets())
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                    throw new TimeoutException("Textures for the drawing surface did not finish loading.");
                wait.SpinOnce();
            }
            Vector3 origin = effect.Transform.Position;
            DesignSize size = surface.GetSize(render);
            if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y)
                || origin.X < 0 || origin.Y < 0 || origin.X + size.Width > render.DesignResolution.Width
                || origin.Y + size.Height > render.DesignResolution.Height)
                throw new ArgumentOutOfRangeException(nameof(origin), "The effect capture is outside the design resolution.");
            var rectangle = new DesignRectU((uint)origin.X, (uint)origin.Y, (uint)size.Width, (uint)size.Height);
            surface.ReplaceTexture(render.GraphicsDevice, process.CaptureBelow(_ctx, effect, surface, rectangle));
        }
    }

    public override void CreateFrameAnimation(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y,
        ReadOnlySpan<string> frames, int ticksPerFrame, string finalFrame, bool removeOnCompletion, bool loop, bool wait)
    {
        if (!ResolveEntityPath(path, out EntityName name, out Entity? parent)) return;
        var assets = new List<AssetRef<Texture>>();
        var sequence = new FrameSequence(name, parent, priority, frames, ticksPerFrame, finalFrame,
            removeOnCompletion, loop,
            source =>
            {
                if (_ctx.Content.RequestTexture(source) is not { } asset) return null;
                assets.Add(asset);
                return SpriteTexture.FromAsset(asset);
            },
            completed => { if (completed.RemoveOnCompletion) _world.DestroyEntity(completed); },
            ready: () =>
            {
                foreach (AssetRef<Texture> asset in assets)
                    if (_ctx.Content.TryGet(asset) is null) return false;
                return true;
            });
        try { sequence.WithPosition(_ctx.RenderContext, x, y); }
        catch { sequence.Dispose(); throw; }
        _world.AddEntity(sequence);
        if (wait) WaitFrameAnimation(EntityQuery.Parse(path.ToString()));
    }

    public override void WaitFrameAnimation(in EntityQuery query)
        => CurrentThread.WaitForFrameSequence(query);

    public override string? FinishFrameAnimation(in EntityQuery query)
    {
        foreach (Entity entity in Query(query))
            if (entity is FrameSequence sequence)
            {
                sequence.Request(NsEntityAction.Stop);
                return sequence.CurrentSource;
            }
        return null;
    }

    public override void PlayVideo(in EntityPath path, int priority, NsCoordinate x, NsCoordinate y,
        bool loop, bool alpha, string source)
    {
        if (!ResolveEntityPath(path, out EntityName name, out Entity? parent)) return;
        Stream input = _ctx.Content.TryOpenStream(source)
            ?? throw new FileNotFoundException("Movie file could not be opened.", source);
        var video = new Video(name, parent, priority, input, _ctx.RenderContext.GraphicsDevice,
            _ctx.AudioContext, loop, alpha) { SourcePath = source };
        try { video.WithPosition(_ctx.RenderContext, x, y); video.Start(); }
        catch { video.Dispose(); throw; }
        _world.AddEntity(video);
    }

    public override void WaitPlay(in EntityQuery query, bool allowSkip = false) => CurrentThread.WaitForMedia(query, allowSkip);

    public override void WaitAction(in EntityQuery query, TimeSpan? timeout)
        => CurrentThread.WaitForEntityActions(query,
            timeout is { } duration ? Clock.Elapsed + AdjustDuration(duration) : null);

    public override void CreateSpriteEx(in EntityPath entityPath, int priority, NsCoordinate x, NsCoordinate y, uint srcX, uint srcY, uint width, uint height, string source)
    {
        if (ResolveEntityPath(entityPath, out EntityName name, out Entity? parent))
        {
            SpriteTexture texture = GetSpriteTexture(source, new DesignRectU(srcX, srcY, width, height));
            _world.AddEntity(new Sprite(name, parent, priority, texture))
                .WithPosition(_ctx.RenderContext, x, y);
        }
    }

    private SpriteTexture GetSpriteTexture(string source, DesignRectU? sourceRect = null)
    {
        if (source.Equals("SCREEN", StringComparison.OrdinalIgnoreCase)
            || source.Equals("VIDEO", StringComparison.OrdinalIgnoreCase))
        {
            return _ctx.RenderContext.CapturePresentedFrame(sourceRect);
        }
        if (Path.GetFileName(source).Equals("thum.npf", StringComparison.OrdinalIgnoreCase)
            && _ctx.Saves.OpenThumbnail(source) is { } thumbnail)
            return SpriteTexture.FromOwnedTexture(_ctx.Content.LoadTexture(thumbnail), sourceRectangle: sourceRect);
        if (_ctx.Content.RequestTexture(source) is { } assetRef)
        {
            return SpriteTexture.FromAsset(assetRef, sourceRect);
        }
        if (EntityPath.TryParse(source) is { } sourcePath && _world.Get(sourcePath) is Image sourceImage)
        {
            return sourceImage.Texture.WithSourceRectangle(sourceRect);
        }

        return SpriteTexture.SolidColor(RgbaFloat.CornflowerBlue, new DesignSizeU(50, 50));
    }

    public override void CreateTextBlock(
        in EntityPath entityPath,
        int priority,
        NsCoordinate x,
        NsCoordinate y,
        NsTextDimension width,
        NsTextDimension height,
        string markup)
    {
        if (ResolveEntityPath(entityPath, out EntityName name, out Entity? parent))
        {
            uint maxWidth = width.Value is { } w ? (uint)Math.Max(0, w) : uint.MaxValue;
            uint maxHeight = height.Value is { } h ? (uint)Math.Max(0, h) : uint.MaxValue;
            _world.AddEntity(new TextBlock(name, parent, priority, markup, _ctx.RenderContext.Text,
                new DesignSizeU(maxWidth, maxHeight), _world.CurrentProcess.FontSettings,
                new Vector4(0, 15, 34, 28)))
                .WithPosition(_ctx.RenderContext, x, y);
        }
    }

    public override void SetFont(string family, uint size, NsColor color, NsColor outlineColor,
        NsFontWeight weight, NsOutlineOffset outlineOffset)
    {
        // Keep the loaded profile font when the script names a platform-specific font.
        _world.CurrentProcess.FontSettings = _world.CurrentProcess.FontSettings with
        {
            DefaultFontSize = new PtFontSize(size),
            VerticalGlyphs = family.StartsWith('@'),
            DefaultTextColor = color.ToRgbaFloat().ToVector4(),
            DefaultOutlineColor = outlineOffset != NsOutlineOffset.Unspecified
                ? outlineColor.ToRgbaFloat().ToVector4() : null
        };
    }

    public override void SetTextOrientation(bool vertical)
        => _world.CurrentProcess.FontSettings = _world.CurrentProcess.FontSettings with { VerticalGlyphs = vertical };

    public override int GetMediaDuration(in EntityQuery query)
        => ToMediaMilliseconds(GetMediaTimes(query).Duration);

    public override int GetTimeElapsed(in EntityQuery query)
        => ToMediaMilliseconds(GetMediaTimes(query).Elapsed);

    public override int GetTimeRemaining(in EntityQuery query)
    {
        var (duration, elapsed) = GetMediaTimes(query);
        return ToMediaMilliseconds(duration - elapsed);
    }

    private (TimeSpan Duration, TimeSpan Elapsed) GetMediaTimes(in EntityQuery query)
    {
        foreach (Entity entity in Query(query))
        {
            if (entity is AudioEntity audio) return (audio.Duration, audio.Elapsed);
            if (entity is Video video) return (video.Duration, video.Elapsed);
        }
        return default;
    }

    private static int ToMediaMilliseconds(TimeSpan value)
        => (int)Math.Clamp(value.TotalMilliseconds, 0, int.MaxValue);

    public override void CreateDialogueBox(in EntityPath entityPath, int priority,
        NsCoordinate x, NsCoordinate y, uint width, uint height, bool inheritTransform)
    {
        if (ResolveEntityPath(entityPath, out EntityName name, out Entity? parent))
        {
            _world.AddEntity(new DialogueBox(name, parent, priority,
                new DesignSize(width, height), inheritTransform))
                .WithPosition(_ctx.RenderContext, x, y);
        }
    }

    public override void LoadDialogueBlock(in DialogueBlockToken blockToken,
        uint maxWidth, uint maxHeight, int letterSpacing, int lineSpacing)
    {
        EntityPath path = EntityPath.Parse($"{blockToken.BoxName}/{blockToken.BlockName}");
        if (ResolveEntityPath(path, out EntityName name, out Entity? parent) && parent is RenderItem box)
        {
            var page = _world.AddEntity(new DialoguePage(name, parent, box.Priority,
                blockToken, maxWidth, maxHeight, lineSpacing));
            page.ScriptScope = CurrentThread.EntityContext;
            page.Transform.Position = box.Transform.Position;
            page.SetAlias(EntityAlias.Parse(blockToken.BlockName));
        }
    }

    public override void WaitText(in EntityQuery query, TimeSpan timeout)
    {
        CurrentThread.Wait(new Thread.WaitOperation(Thread.WaitCondition.EntityIdle, query,
            timeout > TimeSpan.Zero ? Clock.Elapsed + timeout : null));
    }

    public override int GetWidth(in EntityQuery query)
    {
        return _world.Query(query) is [RenderItem renderItem, ..]
            ? (int)renderItem.GetSize(_ctx.RenderContext).Width
            : 0;
    }

    public override int GetHeight(in EntityQuery query)
    {
        return _world.Query(query) is [RenderItem renderItem, ..]
            ? (int)renderItem.GetSize(_ctx.RenderContext).Height
            : 0;
    }

    public override Vector2 GetPosition(in EntityQuery query)
    {
        foreach (Entity entity in Query(query))
            if (entity is RenderItem visual)
                return new Vector2(visual.Transform.Position.X, visual.Transform.Position.Y);
        return Vector2.Zero;
    }

    public override Vector2 GetCursorPosition()
        => Scrollbar.ToDesignPosition(_ctx.RenderContext, _ctx.InputContext.MousePosition);

    public override void CreateScrollbar(in EntityPath path, int priority, int x1, int y1,
        int x2, int y2, NsRational initialValue, NsScrollDirection scrollDirection, string knobImage)
    {
        if (ResolveEntityPath(path, out EntityName name, out Entity? parent))
            _world.AddEntity(new Scrollbar(name, parent, priority, GetSpriteTexture(knobImage),
                new Vector2(x1, y1), new Vector2(x2, y2), scrollDirection, initialValue.Rebase(1)));
    }

    public override float GetScrollbarValue(in EntityQuery scrollbarEntity)
    {
        foreach (Entity entity in Query(scrollbarEntity))
            if (entity is Scrollbar scrollbar) return scrollbar.Value;
        return 0;
    }

    public override void SetScrollbar(in EntityQuery scrollbar, in EntityQuery parent)
    {
        Entity? target = null;
        foreach (Entity entity in Query(parent)) { target = entity; break; }
        foreach (Entity entity in Query(scrollbar))
            if (entity is Scrollbar bar) bar.ScrollTarget = target;
    }

    public override void SetScrollSpeed(in EntityQuery query, float speed)
    {
        foreach (Entity entity in Query(query))
            if (entity is Scrollbar scrollbar)
                scrollbar.ScrollSpeed = float.IsFinite(speed) ? Math.Max(0, speed) : 0;
    }

    public override int GetSoundAmplitude(string characterName)
    {
        return 0;
    }

    public override void CreateChoice(in EntityPath entityPath)
        => CreateChoice(entityPath, preserveOpacity: false);

    public override void CreateChoice(in EntityPath entityPath, bool preserveOpacity = false)
    {
        if (!ResolveEntityPath(entityPath, out EntityName name, out Entity? parent))
            return;

        var choice = _world.AddEntity(new ChoiceEntity(name, parent, preserveOpacity));
        foreach (string mouseState in s_mouseStates)
            _world.AddEntity(new MouseStateEntity(EntityName.Parse(mouseState), choice));
    }

    public override void SetNextFocus(in EntityQuery first, in EntityQuery second, NsFocusDirection focusDirection)
    {
        ChoiceEntity? destination = ResolveChoice(second);
        if (destination is null) return;
        foreach (Entity entity in Query(first))
        {
            ChoiceEntity? source = ChoiceEntity.FindOwner(entity);
            if (source is null) continue;
            source.SetNextFocus(focusDirection, destination);
            if (source.IsClickable && (!_choiceInputs.TryGetValue(CurrentThread, out ChoiceInputState? input)
                || input.Focused is null))
            {
                _choiceInputs.TryAdd(CurrentThread, input = new ChoiceInputState());
                FocusChoice(input!, source);
            }
        }
    }

    public override void SelectEnd()
    {
        if (!_choiceInputs.TryGetValue(CurrentThread, out ChoiceInputState? input)) return;
        input.Focused?.ShowState(MouseState.Normal);
        input.Focused = null;
    }

    public override bool HandleInputEvents(in EntityQuery uiElementPath)
    {
        if (!_choiceInputs.TryGetValue(CurrentThread, out ChoiceInputState? input))
            _choiceInputs.Add(CurrentThread, input = new ChoiceInputState());

        long frame = _ctx.FrameStamp.FrameId;
        foreach (Entity entity in Query(uiElementPath))
        {
            if (entity is Scrollbar scrollbar)
            {
                if (scrollbar.ScrollTarget is not BacklogView && HandleScrollbar(scrollbar)) return true;
                continue;
            }
            ChoiceEntity? choice = ChoiceEntity.FindOwner(entity);
            if (choice is null || !choice.IsAttached || !choice.IsClickable
                || choice.GetHitTarget() is not { } hitTarget) continue;

            input.LastSeen[choice] = frame;
            // Nested menus share a script thread. Reconcile focus with the choices
            // being polled, instead of retaining a disabled or outer menu choice.
            if (input.Focused is not { IsAttached: true, IsClickable: true } focused
                || input.LastSeen.TryGetValue(focused, out long seen) && seen < frame - 1)
                FocusChoice(input, choice);

            bool hovered = HitTest(hitTarget, _ctx.InputContext.MousePosition);
            bool pointerEntered = hovered && !choice.PointerInside;
            choice.PointerInside = hovered;
            if (hovered && (pointerEntered || _ctx.InputContext.PointerMoved))
                FocusChoice(input, choice);

            bool pointerPress = hovered && _ctx.InputContext.ChoicePointerPressed;
            bool confirmPress = !_ctx.InputContext.ChoicePointerPressed
                && ReferenceEquals(input.Focused, choice)
                && _ctx.InputContext.WasActionTriggered(InputAction.Enter);
            if ((pointerPress || confirmPress)
                && _ctx.InputContext.ConsumeChoicePress(pointerPress))
            {
                _focusedScrollbar = null;
                FocusChoice(input, choice);
                choice.ShowState(MouseState.Down);
                return true;
            }

            if (ReferenceEquals(input.Focused, choice))
            {
                foreach ((InputAction action, NsFocusDirection direction) in s_focusDirections)
                {
                    if (_ctx.InputContext.WasActionTriggered(action)
                        && choice.GetNextFocus(direction) is { } next)
                    {
                        _ctx.InputContext.Consume(action);
                        FocusChoice(input, next);
                        break;
                    }
                }
            }
            choice.ShowState(ReferenceEquals(input.Focused, choice) ? MouseState.Over : MouseState.Normal);
        }
        return false;
    }

    private bool HandleScrollbar(Scrollbar scrollbar)
    {
        if (!scrollbar.IsAttached) return false;
        if (_draggedScrollbar is not { IsAttached: true, IsDragging: true })
            _draggedScrollbar = null;
        Vector2 pointer = Scrollbar.ToDesignPosition(_ctx.RenderContext, _ctx.InputContext.MousePosition);
        DesignSize size = scrollbar.GetSize(_ctx.RenderContext);
        bool hovered = scrollbar.ContainsTrack(pointer, size.ToVector2());
        for (Entity? parent = scrollbar.Parent; hovered && parent is not null; parent = parent.Parent)
            if (parent is ConstraintBox constraint && !ContainsPoint(constraint, _ctx.InputContext.MousePosition))
                hovered = false;
        if (hovered && _ctx.InputContext.ChoicePointerPressed && _draggedScrollbar is null
            && _ctx.InputContext.ConsumeChoicePress(pointer: true))
        {
            _focusedScrollbar = _draggedScrollbar = scrollbar;
            scrollbar.BeginDrag(pointer, size.ToVector2());
            if (!_ctx.InputContext.PointerHeld) scrollbar.EndDrag();
            return true;
        }
        if (ReferenceEquals(_draggedScrollbar, scrollbar))
        {
            scrollbar.Drag(pointer);
            return true;
        }
        if (hovered || ReferenceEquals(_focusedScrollbar, scrollbar))
        {
            int direction = 0;
            if (_ctx.InputContext.Consume(InputAction.Up) || _ctx.InputContext.Consume(InputAction.Left)) direction--;
            if (_ctx.InputContext.Consume(InputAction.Down) || _ctx.InputContext.Consume(InputAction.Right)) direction++;
            if (direction != 0)
            {
                _focusedScrollbar = scrollbar;
                scrollbar.Step(direction);
                return true;
            }
        }
        return false;
    }

    private static readonly (InputAction, NsFocusDirection)[] s_focusDirections =
    [
        (InputAction.Left, NsFocusDirection.Left),
        (InputAction.Up, NsFocusDirection.Up),
        (InputAction.Right, NsFocusDirection.Right),
        (InputAction.Down, NsFocusDirection.Down)
    ];

    private ChoiceEntity? ResolveChoice(in EntityQuery query)
    {
        foreach (Entity entity in Query(query))
            if (ChoiceEntity.FindOwner(entity) is { } choice) return choice;
        return null;
    }

    private static void FocusChoice(ChoiceInputState input, ChoiceEntity choice)
    {
        if (ReferenceEquals(input.Focused, choice)) return;
        input.Focused?.ShowState(MouseState.Normal);
        input.Focused = choice;
        choice.ShowState(MouseState.Over);
    }

    private bool HitTest(RenderItem target, Vector2 position)
    {
        if (!ContainsPoint(target, position)) return false;
        // A hidden usual sprite still supplies geometry. Enclosing windows,
        // rather than visual alpha, constrain which part can receive input.
        for (Entity? parent = target.Parent; parent is not null; parent = parent.Parent)
            if (parent is ConstraintBox constraint && !ContainsPoint(constraint, position)) return false;
        return true;
    }

    private bool ContainsPoint(RenderItem target, Vector2 position)
    {
        DesignSize size = target.GetSize(_ctx.RenderContext);
        Matrix4x4 matrix = _ctx.RenderContext.GetTransformMatrix(
            target.Transform, size, useScaling: true, aligned: false);
        if (!Matrix4x4.Invert(matrix, out Matrix4x4 inverse)) return false;
        Vector3 local = Vector3.Transform(new Vector3(position, 0), inverse);
        return local.X >= 0 && local.Y >= 0 && local.X < size.Width && local.Y < size.Height;
    }

    public override void LoadAudio(in EntityPath entityPath, NsAudioKind kind, string fileName)
    {
        if (!ResolveEntityPath(entityPath, out EntityName name, out Entity? parent))
            return;

        CreateAudio(name, parent, kind, fileName);
    }

    internal void ApplyDialogueVoice(Process process, VoiceSegment voice, bool skipping)
    {
        // All pages in a process share the dialogue channel and replace its previous line.
        EntityName name = EntityName.Parse("dialogue-voice");
        if (process.TryGetChild(name) is AudioEntity previous)
        {
            previous.Request(NsEntityAction.Stop);
            _world.DestroyEntity(previous);
        }
        if (voice.Action == NsVoiceAction.Play && !skipping)
            CreateAudio(name, process, NsAudioKind.Voice, voice.FileName)?.Request(NsEntityAction.Play);
    }

    private AudioEntity? CreateAudio(EntityName name, Entity? parent, NsAudioKind kind, string fileName)
    {
        string sourcePath = fileName;
        if (!Path.HasExtension(sourcePath))
        {
            using Stream? direct = _ctx.Content.TryOpenStream(sourcePath);
            if (direct is null)
            foreach (string extension in new[] { ".ogg", ".wav", ".mp3", ".flac", ".opus" })
            {
                using Stream? candidate = _ctx.Content.TryOpenStream(fileName + extension);
                if (candidate is null) continue;
                sourcePath = fileName + extension;
                break;
            }
        }
        using (Stream? stream = OpenAudioStream(sourcePath))
        {
            if (stream is null)
            {
                _log.Warn($"Audio file could not be opened: '{fileName}'");
                return null;
            }
        }

        return _world.AddEntity(new AudioEntity(
            name,
            parent,
            () => OpenAudioStream(sourcePath)
                ?? throw new FileNotFoundException("Audio file could not be opened.", sourcePath),
            _ctx.AudioContext,
            kind,
            _ctx.VM
        ) { SourcePath = sourcePath });
    }

    private Stream? OpenAudioStream(string fileName)
    {
        Stream? stream = _ctx.Content.TryOpenStream(fileName);
        if (stream is not null || Path.HasExtension(fileName))
            return stream;

        foreach (string extension in new[] { ".ogg", ".wav", ".mp3", ".flac", ".opus" })
        {
            stream = _ctx.Content.TryOpenStream(fileName + extension);
            if (stream is not null)
                return stream;
        }
        return null;
    }

    public override void ToggleLooping(in EntityQuery query, bool enable)
    {
        foreach (Entity entity in Query(query))
            if (entity is AudioEntity audio)
                audio.SetLooping(enable);
    }

    public override void SetLoopRegion(in EntityQuery query, TimeSpan loopStart, TimeSpan loopEnd)
    {
        foreach (Entity entity in Query(query))
            if (entity is AudioEntity audio)
                audio.SetLoopRegion(loopStart, loopEnd);
    }

    public override void SetVolume(in EntityQuery query, TimeSpan duration, NsRational volume)
    {
        foreach (Entity entity in Query(query))
            if (entity is AudioEntity audio)
                audio.SetVolume(volume.Rebase(1.0f), AdjustDuration(duration));
    }

    public override void CreateThread(in EntityPath entityPath, string target, NsCoordinate x, NsCoordinate y)
    {
        if (ResolveEntityPath(entityPath, out EntityName name, out Entity? parent))
        {
            NsScriptThreadState? vmState = VM.CreateThread(CurrentModule.Name, target);
            if (vmState.HasValue)
            {
                _world.AddEntity(new Thread(name, parent, vmState.Value, isMain: false));
            }
        }
    }

    public override void CreateRectangle(in EntityPath entityPath, int priority, NsCoordinate x, NsCoordinate y, uint width, uint height, NsColor color)
    {
        if (ResolveEntityPath(entityPath, out EntityName entityName, out Entity? parent))
        {
            var size = new DesignSize(width, height);
            _world.AddEntity(new SolidColorRect(entityName, parent, priority, color.ToRgbaFloat(), size))
                .WithPosition(_ctx.RenderContext, x, y);
        }
    }

    public override void Move(in EntityQuery query, TimeSpan duration, NsCoordinate dstX, NsCoordinate dstY, NsEaseFunction easeFunction, TimeSpan waitTimeout)
    {
        duration = AdjustDuration(duration);
        waitTimeout = AdjustDuration(waitTimeout);
        foreach (Entity entity in Query(query))
        {
            foreach (Entity node in entity.DescendantsAndSelf(selfFirst: false))
            {
                node.Move(_ctx.RenderContext, dstX, dstY, duration, easeFunction);
            }
        }

        WaitOpt(Thread.WaitCondition.MoveCompleted, query, waitTimeout);
    }

    public override void Fade(in EntityQuery query, TimeSpan duration, NsRational dstOpacity, NsEaseFunction easeFunction, TimeSpan waitTimeout)
    {
        duration = AdjustDuration(duration);
        waitTimeout = AdjustDuration(waitTimeout);
        foreach (Entity entity in Query(query))
        {
            // Choice roots own independent visuals (including direct, always-visible icons).
            if (entity is ChoiceEntity) continue;
            foreach (Entity node in entity.DescendantsAndSelf(selfFirst: false))
            {
                node.Fade(dstOpacity, duration, easeFunction);
            }
        }

        WaitOpt(Thread.WaitCondition.FadeCompleted, query, waitTimeout);
    }

    public override void Zoom(in EntityQuery query, TimeSpan duration, NsRational dstScaleX, NsRational dstScaleY, NsEaseFunction easeFunction, TimeSpan waitTimeout)
    {
        duration = AdjustDuration(duration);
        waitTimeout = AdjustDuration(waitTimeout);
        var dstScale = new Vector3(dstScaleX.Rebase(1.0f), dstScaleY.Rebase(1.0f), 1.0f);
        foreach (Entity entity in Query(query))
        {
            entity.Scale(dstScale, duration, easeFunction);
        }

        WaitOpt(Thread.WaitCondition.ZoomCompleted, query, waitTimeout);
    }

    public override void SetVertex(in EntityQuery query, NsCoordinate x, NsCoordinate y)
    {
        foreach (Entity entity in Query(query))
            if (entity is RenderItem visual)
                visual.SetPivot(_ctx.RenderContext, x, y);
    }

    public override void Rotate(in EntityQuery query, TimeSpan duration,
        NsNumeric dstRotationX, NsNumeric dstRotationY, NsNumeric dstRotationZ,
        NsEaseFunction easeFunction, TimeSpan waitTimeout)
    {
        duration = AdjustDuration(duration);
        waitTimeout = AdjustDuration(waitTimeout);
        foreach (Entity entity in Query(query))
            foreach (Entity node in entity.DescendantsAndSelf(selfFirst: false))
                node.Rotate(dstRotationX, dstRotationY, dstRotationZ, duration, easeFunction);
        WaitOpt(Thread.WaitCondition.RotateCompleted, query, waitTimeout);
    }

    public override void BezierMove(in EntityQuery query, TimeSpan duration,
        CompositeBezier curve, NsEaseFunction easeFunction, bool wait)
    {
        duration = AdjustDuration(duration);
        foreach (Entity entity in Query(query))
        {
            foreach (Entity child in entity.DescendantsAndSelf())
            {
                if (child is not RenderItem visual) continue;
                var segments = ImmutableArray.CreateBuilder<ProcessedBezierSegment>(curve.Segments.Length);
                foreach (CubicBezierSegment segment in curve.Segments)
                    segments.Add(new ProcessedBezierSegment(
                        Point(segment.P0), Point(segment.P1), Point(segment.P2), Point(segment.P3)));
                visual.BezierMove(new ProcessedBezierCurve(segments.MoveToImmutable()), duration, easeFunction);

                Vector2 Point(BezierControlPoint point)
                {
                    Vector3 position = visual.Point(_ctx.RenderContext, point.X, point.Y);
                    return new Vector2(position.X, position.Y);
                }
            }
        }
        WaitOpt(Thread.WaitCondition.BezierMoveCompleted, query, wait ? duration : TimeSpan.Zero);
    }

    public override void SetTone(in EntityQuery query, NsTone tone)
    {
        foreach (Entity entity in Query(query))
            foreach (Entity node in entity.DescendantsAndSelf())
                if (node is Sprite sprite) sprite.SetTone(tone);
    }

    public override void BoxBlur(in EntityQuery query, uint nbPasses)
    {
        if (nbPasses > 32) throw new ArgumentOutOfRangeException(nameof(nbPasses));
        foreach (Entity entity in Query(query))
            foreach (Entity node in entity.DescendantsAndSelf())
                if (node is PlainEffect plain) plain.SetBlur(nbPasses);
                else if (node is Sprite sprite) sprite.SetBlur(nbPasses);
                else if (node is RenderItem)
                    throw new NotSupportedException($"SetShade does not support {node.GetType().Name}.");
    }

    private int _legacyTransitionId;

    public override void BeginLegacyTransition(in EntityQuery query, TimeSpan duration,
        string maskFileName, bool masked, TimeSpan waitTimeout, bool dynamic = false)
    {
        duration = LegacyTransitionCoverage.QuantizeDuration(duration);
        if (duration <= TimeSpan.Zero) return;
        if (dynamic && masked) throw new ArgumentException("Dynamic legacy transitions require a uniform fade.");
        var sources = new HashSet<RenderItem>();
        foreach (Entity entity in Query(query))
            foreach (Entity node in entity.DescendantsAndSelf())
                if (node is RenderItem { IsHidden: false, IsPendingDestruction: false } visual)
                {
                    if (visual is LegacySceneTransition)
                        throw new InvalidOperationException("A scene already being transitioned cannot be captured again.");
                    sources.Add(visual);
                }
        if (sources.Count == 0) return;
        var ordered = sources.ToArray();
        Array.Sort(ordered);
        AssetRef<Texture>? mask = masked
            ? _ctx.Content.RequestTexture(maskFileName)
                ?? throw new FileNotFoundException("Legacy transition mask was not found.", maskFileName)
            : null;
        string name = "__legacy_transition_" + ++_legacyTransitionId;
        var transition = new LegacySceneTransition(EntityName.Parse(name), CurrentThread.EntityContext,
            checked(ordered[^1].Priority + 1), ordered, mask, AdjustDuration(duration), dynamic);
        _world.AddEntity(transition);
        // Wait for the captured scene's animation, including asynchronous asset
        // preparation, rather than expiring while its first frame is still loading.
        if (waitTimeout != TimeSpan.Zero)
            CurrentThread.Wait(new Thread.WaitOperation(Thread.WaitCondition.TransitionCompleted,
                EntityQuery.Parse(name), null));
        else if (dynamic)
            CurrentThread.Wait(new Thread.WaitOperation(Thread.WaitCondition.LegacyTransitionReady, null, null)
                { ActionTargets = [transition] });
    }

    public override void BeginTransition(in EntityQuery query, TimeSpan duration, NsRational srcFadeAmount,
        NsRational dstFadeAmount, NsRational feather, NsEaseFunction easeFunction, string maskFileName, TimeSpan waitTimeout)
    {
        float source = srcFadeAmount, destination = dstFadeAmount, softness = feather;
        if (!float.IsFinite(source) || !float.IsFinite(destination) || !float.IsFinite(softness)
            || source < 0 || source > 1 || destination < 0 || destination > 1 || softness < 0 || softness > 1)
            throw new ArgumentOutOfRangeException(nameof(srcFadeAmount), "Transition amounts and feather must be between zero and one.");
        var targets = new HashSet<RenderItem>();
        foreach (Entity entity in Query(query))
            foreach (Entity node in entity.DescendantsAndSelf())
                if (node is RenderItem visual)
                {
                    if (visual is not (Sprite or SolidColorRect or PlainEffect or FrameSequence)
                        || visual.BlendMode is not (BlendMode.Alpha or BlendMode.Additive or BlendMode.Multiplicative))
                        throw new NotSupportedException($"Mask transition does not support {visual.GetType().Name} with {visual.BlendMode} blending.");
                    targets.Add(visual);
                }
        if (targets.Count == 0)
        {
            _log.Warn($"Mask transition query '{query.ToString()}' matched no renderable entities.");
            return;
        }
        using var mask = _ctx.Content.RequestTexture(maskFileName)
            ?? throw new FileNotFoundException("Transition mask was not found.", maskFileName);
        foreach (RenderItem target in targets)
            target.BeginMaskTransition(mask.Clone(), source, destination, softness, AdjustDuration(duration), easeFunction);
        WaitOpt(Thread.WaitCondition.TransitionCompleted, query, AdjustDuration(waitTimeout));
    }

    private void WaitOpt(Thread.WaitCondition condition, in EntityQuery query, TimeSpan timeout)
    {
        if (timeout != TimeSpan.Zero)
        {
            TimeSpan deadline = Clock.Elapsed + timeout;
            CurrentThread.Wait(new Thread.WaitOperation(condition, query, deadline));
        }
    }

    private TimeSpan AdjustDuration(TimeSpan duration)
    {
        return _ctx.StorySkipping ? TimeSpan.Zero : duration;
    }

    public override void SetAlias(in EntityQuery query, in EntityAlias alias)
    {
        if (_world.IsValidAlias(alias))
        {
            foreach (Entity entity in Query(query))
            {
                entity.SetAlias(alias);
            }
        }
    }

    public override void Request(in EntityQuery query, NsEntityAction action)
    {
        foreach (Entity entity in Query(query))
        {
            if (!entity.Request(action))
            {
                _log.Warn(
                    $"Thread {CurrentThread.Name.ToString()} | " +
                    $"Action '{action.ToString()}' not implemented for entity '{entity.GetAbsolutePath()}'"
                );
            }
        }
    }

    public override void Wait(TimeSpan delay)
    {
        CurrentThread.Wait(Thread.WaitOperation.Suspend(Clock.Elapsed + delay));
    }

    public override void WaitForInput()
    {
        CurrentThread.Wait(Thread.WaitOperation.UserInput(deadline: null));
    }

    public override void WaitForInput(TimeSpan timeout)
    {
        CurrentThread.Wait(Thread.WaitOperation.UserInput(deadline: Clock.Elapsed + timeout));
    }

    public override void DestroyEntities(in EntityQuery query)
    {
        foreach (Entity entity in Query(query))
        {
            if (!entity.IsLocked)
                _world.DestroyEntity(entity);
        }
    }
}
