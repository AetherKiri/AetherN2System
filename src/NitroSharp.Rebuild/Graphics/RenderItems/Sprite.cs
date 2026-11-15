using System;
using System.Numerics;
using System.Threading;
using NitroSharp.Content;
using NitroSharp.Graphics.Core;
using Veldrid;

namespace NitroSharp.Graphics;

internal partial class Sprite : RenderItem
{
    private readonly SpriteTexture _texture;
    private bool _disposed;
    private uint _blurPasses;
    private bool _blurDirty;
    private SpriteTexture? _blurredTexture;

    public Sprite(EntityName name, Entity? parent, int priority, in SpriteTexture texture)
        : base(name, parent, priority)
    {
        _texture = texture;
    }

    public SpriteTexture Texture => _texture;

    public void SetBlur(uint passes)
    {
        if (_blurPasses == passes) return;
        _blurPasses = passes;
        _blurDirty = true;
    }

    public override DesignSize GetSize(RenderContext ctx)
        => _texture.GetSize(ctx);

    public override void Render(GameContext ctx)
    {
        RenderCore(ctx.RenderContext, ctx.RenderContext.MainBatch);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _texture.Dispose();
        _blurredTexture?.Dispose();
        _blurredTexture = null;
        base.Dispose();
    }

    private void RenderCore(RenderContext ctx, DrawBatch drawBatch)
    {
        Texture alphaMaskTex = ctx.WhiteTexture;
        Vector2 alphaMaskPos = Vector2.Zero;
        // if (TryGetAlphaMaskAscendant() is { } alphaMask)
        // {
        //     alphaMaskTex = ctx.Content.Get(alphaMask.Texture);
        //     alphaMaskPos = alphaMask.Transform.Position.XY();
        // }

        if (_blurDirty)
        {
            _blurredTexture?.Dispose();
            _blurredTexture = _blurPasses > 0 ? SnapshotBlur.Apply(ctx, _texture, _blurPasses) : null;
            _blurDirty = false;
        }
        SpriteTexture displayedTexture = _blurredTexture ?? _texture;
        DesignSize size = GetSize(ctx);
        (Vector2 uvTopLeft, Vector2 uvBottomRight) = displayedTexture.GetTexCoords(ctx);
        var quad = QuadPrimitive.Create(
            size,
            ctx.GetTransformMatrix(Transform, size, useScaling: true, aligned: false),
            uvTopLeft,
            uvBottomRight,
            Color.ToVector4()
        );

        PushQuad(ctx, drawBatch,
            quad,
            displayedTexture.Resolve(ctx),
            alphaMaskTex,
            alphaMaskPos,
            null
        );
    }
}

internal enum SpriteTextureKind
{
    SolidColor,
    Asset,
    Owned,
    Borrowed,
    Pooled
}

internal readonly partial struct SpriteTexture : IDisposable
{
    private readonly AssetRef<Texture>? _assetRef;
    private readonly Texture? _texture;
    private readonly TextureLease? _textureLease;
    private readonly DesignSizeU? _textureSize;
    private readonly Vector2 _uvOrigin;
    private readonly Vector2 _uvExtent;
    private readonly float _sourceInset;

    public readonly SpriteTextureKind Kind;
    public readonly RgbaFloat Color;
    public DesignRectU? SourceRectangle { get; private init; }

    private SpriteTexture(
        SpriteTextureKind kind,
        AssetRef<Texture>? assetRef,
        DesignRectU? sourceRectangle,
        Texture? texture,
        TextureLease? textureLease,
        in RgbaFloat color,
        DesignSizeU? textureSize = null,
        Vector2? uvOrigin = null, Vector2? uvExtent = null, float sourceInset = 0)
    {
        Kind = kind;
        _assetRef = assetRef;
        SourceRectangle = sourceRectangle;
        _texture = texture;
        _textureLease = textureLease;
        _textureSize = textureSize;
        _uvOrigin = uvOrigin ?? Vector2.Zero;
        _uvExtent = uvExtent ?? Vector2.One;
        _sourceInset = sourceInset;
        Color = color;
    }

    public static SpriteTexture FromAsset(AssetRef<Texture> assetRef, DesignRectU? srcRectangle = null)
        => new(SpriteTextureKind.Asset, assetRef, srcRectangle, null, null, RgbaFloat.White);

    public static SpriteTexture SolidColor(in RgbaFloat color, DesignSizeU size) => new(
        SpriteTextureKind.SolidColor,
        null,
        new DesignRectU(0, 0, size.Width, size.Height), null,
        null,
        color
    );

    public static SpriteTexture FromPooledTexture(PooledTexture texture) => new(
        SpriteTextureKind.Pooled,
        null,
        null,
        texture: null,
        textureLease: new TextureLease(texture.Get(), texture),
        color: RgbaFloat.White,
        textureSize: new DesignSizeU(texture.Get().Width, texture.Get().Height)
    );

    public static SpriteTexture FromOwnedTexture(Texture texture, DesignSizeU? size = null,
        DesignRectU? sourceRectangle = null, Vector2? uvOrigin = null, Vector2? uvExtent = null)
        => FromTexture(SpriteTextureKind.Owned, texture, size, sourceRectangle, uvOrigin, uvExtent);

    internal static SpriteTexture FromOwnedTarget(RenderTarget target, DesignSizeU size,
        Vector2 uvOrigin, Vector2 uvExtent)
        => new(SpriteTextureKind.Owned, null, null, null, new TextureLease(target.ColorTarget, target),
            RgbaFloat.White, size, uvOrigin, uvExtent);

    public static SpriteTexture FromBorrowedTexture(Texture texture)
        => FromTexture(SpriteTextureKind.Borrowed, texture);

    private static SpriteTexture FromTexture(SpriteTextureKind kind, Texture texture,
        DesignSizeU? size = null, DesignRectU? sourceRectangle = null,
        Vector2? uvOrigin = null, Vector2? uvExtent = null)
    {
        return new SpriteTexture(
            kind,
            null,
            sourceRectangle,
            kind == SpriteTextureKind.Borrowed ? texture : null,
            textureLease: kind == SpriteTextureKind.Owned ? new TextureLease(texture, texture) : null,
            color: RgbaFloat.White,
            textureSize: size ?? new DesignSizeU(texture.Width, texture.Height),
            uvOrigin: uvOrigin, uvExtent: uvExtent
        );
    }

    public Texture Resolve(RenderContext ctx)
        => this switch
        {
            { Kind: SpriteTextureKind.Asset, _assetRef: { } assetRef } => ctx.Content.Get(assetRef),
            { Kind: SpriteTextureKind.SolidColor } => ctx.WhiteTexture,
            { _textureLease: { } lease } => lease.Texture,
            { Kind: SpriteTextureKind.Borrowed, _texture: not null } => _texture,
            _ => throw ThrowHelper.Unreachable()
        };

    public DesignSize GetSize(RenderContext ctx)
        => this switch
        {
            { SourceRectangle: { } srcRect } => new DesignSize(srcRect.Width, srcRect.Height),
            { _textureSize: { } size } => size.ToFloatSize(),
            { _assetRef: { } assetRef } => ctx.Content.GetTextureSize(assetRef).Reinterpret<DesignPixel>().ToFloatSize(),
            _ => throw ThrowHelper.Unreachable()
        };

    public (Vector2, Vector2) GetTexCoords(RenderContext ctx)
    {
        if (SourceRectangle is { } srcRect && Kind != SpriteTextureKind.SolidColor)
        {
            float adjustment = Kind == SpriteTextureKind.Asset ? 0.2f : _sourceInset;
            var texSize = _assetRef is { } assetRef
                ? ctx.Content.GetTextureSize(assetRef).ToVector2()
                : _textureSize!.Value.ToVector2();
            var topLeft = new Vector2(srcRect.Left + adjustment, srcRect.Top + adjustment) / texSize;
            var bottomRight = new Vector2(srcRect.Right - adjustment, srcRect.Bottom - adjustment) / texSize;
            return (_uvOrigin + topLeft * _uvExtent, _uvOrigin + bottomRight * _uvExtent);
        }

        return (_uvOrigin, _uvOrigin + _uvExtent);
    }

    public SpriteTexture WithOwnedTexture(Texture texture)
        => new(SpriteTextureKind.Owned, null, SourceRectangle, null,
            new TextureLease(texture, texture), Color, _textureSize ?? new DesignSizeU(texture.Width, texture.Height),
            _uvOrigin, _uvExtent, Kind == SpriteTextureKind.Asset ? .2f : _sourceInset);

    public SpriteTexture WithSourceRectangle(DesignRectU? sourceRect)
        => Clone() with { SourceRectangle = sourceRect ?? SourceRectangle };

    private SpriteTexture Clone()
        => this switch
        {
            { _assetRef: { } assetRef } => FromAsset(assetRef.Clone(), SourceRectangle),
            { _textureLease: { } lease } => new SpriteTexture(Kind, null, SourceRectangle,
                null, lease.Clone(), Color, _textureSize, _uvOrigin, _uvExtent, _sourceInset),
            _ => this
        };

    public void Dispose()
    {
        switch (this)
        {
            case { Kind: SpriteTextureKind.Asset, _assetRef: { } assetRef }:
                assetRef.Dispose();
                break;
            case { _textureLease: { } lease }:
                lease.Dispose();
                break;
            case { Kind: SpriteTextureKind.Borrowed }:
                break;
        }
    }

    // Struct copies share a lease, while explicit image clones receive their own lease.
    // This keeps snapshots alive after their source image is deleted and returns pooled
    // textures only after the final sprite using them has gone away.
    private sealed class TextureLease : IDisposable
    {
        private readonly SharedTexture _resource;
        private int _disposed;

        public TextureLease(Texture texture, IDisposable owner)
        {
            _resource = new SharedTexture(texture, owner);
        }

        private TextureLease(SharedTexture resource)
        {
            _resource = resource;
            Interlocked.Increment(ref resource.References);
        }

        public Texture Texture => _resource.Texture;

        public TextureLease Clone()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return new TextureLease(_resource);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0
                && Interlocked.Decrement(ref _resource.References) == 0)
                _resource.Owner.Dispose();
        }

        private sealed class SharedTexture(Texture texture, IDisposable owner)
        {
            public readonly Texture Texture = texture;
            public readonly IDisposable Owner = owner;
            public int References = 1;
        }
    }
}
