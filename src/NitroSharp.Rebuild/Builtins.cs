using System;
using System.Diagnostics;
using System.Numerics;
using NitroSharp.Common;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.VM;
using NitroSharp.Text;
using Veldrid;
using ZeroLog;

namespace NitroSharp;

internal sealed class Builtins : BuiltInFunctions
{
    private readonly GameContext _ctx;
    private readonly World _world;
    private readonly Log _log;

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

    public override void Exit() => _ctx.ShutdownSignal.Cancel();

    public override int GetPlatformId() => 100;

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
            DefaultTextColor = color.ToRgbaFloat().ToVector4(),
            DefaultOutlineColor = outlineOffset != NsOutlineOffset.Unspecified
                ? outlineColor.ToRgbaFloat().ToVector4() : null
        };
    }

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

    public override int GetSoundAmplitude(string characterName)
    {
        return 0;
    }

    public override void CreateThread(in EntityPath entityPath, string target, NsCoordinate x, NsCoordinate y)
    {
        if (ResolveEntityPath(entityPath, out EntityName name, out Entity? parent))
        {
            parent = _world.CurrentProcess;
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
        return false
            ? TimeSpan.FromSeconds(duration.TotalSeconds / 10.0d)
            : duration;
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
            _world.DestroyEntity(entity);
        }
    }
}
