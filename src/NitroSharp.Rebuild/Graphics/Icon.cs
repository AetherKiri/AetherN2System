using System;
using NitroSharp.Content;
using NitroSharp.Graphics.Core;
using System.IO;
using System.Numerics;
using Veldrid;

namespace NitroSharp.Graphics;

internal sealed class AnimatedIcons(Icon? waitLine, Icon? waitPage, Icon? waitAuto, Icon? backlogVoice) : IDisposable
{
    public Icon? WaitLine { get; } = waitLine;
    public Icon? WaitPage { get; } = waitPage;
    public Icon? WaitAuto { get; } = waitAuto;
    public Icon? BacklogVoice { get; } = backlogVoice;

    public void Dispose()
    {
        WaitLine?.Dispose();
        WaitPage?.Dispose();
        WaitAuto?.Dispose();
        BacklogVoice?.Dispose();
    }
}

internal sealed class Icon : IDisposable
{
    private readonly Texture _texture;
    public DesignSize Size => new(_texture.Width, _texture.Height);

    private Icon(Texture texture)
    {
        _texture = texture;
    }

    public static bool Exists(ContentManager content, IconPathPattern pathPattern)
    {
        if (pathPattern.IconCount == 0) return false;
        foreach (string path in pathPattern.EnumeratePaths())
        {
            using Stream? stream = content.TryOpenStream(path);
            if (stream is null)
            {
                return false;
            }
        }

        return true;
    }

    public static Icon Load(RenderContext renderContext, IconPathPattern pathPattern)
    {
        ContentManager content = renderContext.Content;
        ResourceFactory rf = renderContext.ResourceFactory;
        Texture? texture = null;
        CommandList cl = renderContext.CommandListPool.Rent();
        cl.Begin();
        uint layer = 0;
        foreach (string path in pathPattern.EnumeratePaths())
        {
            Texture staging = content.LoadTexture(path, staging: true);
            texture ??= rf.CreateTexture(TextureDescription.Texture2D(
                staging.Width, staging.Height,
                mipLevels: 1, arrayLayers: pathPattern.IconCount,
                staging.Format, TextureUsage.Sampled
            ));
            cl.CopyTexture(
                source: staging,
                srcX: 0, srcY: 0, srcZ: 0,
                srcMipLevel: 0, srcBaseArrayLayer: 0,
                destination: texture,
                dstX: 0, dstY: 0, dstZ: 0,
                dstMipLevel: 0, layer++,
                texture.Width, texture.Height,
                depth: 1, layerCount: 1
            );
            GraphicsUtils.DisposeAfterIdle(renderContext.GraphicsDevice, staging);
        }
        cl.End();
        renderContext.GraphicsDevice.SubmitCommands(cl);
        renderContext.CommandListPool.Return(cl);

        return new Icon(texture!);
    }

    public void Render(RenderContext context, Matrix4x4 transform, float elapsed, float opacity)
    {
        DrawBatch batch = context.MainBatch;
        IconShaderResources shaderResources = context.ShaderResources.Icon;
        ViewProjection vp = context.OrthoProjection;

        var quad = QuadPrimitive.Create(
            Size,
            transform,
            uvTopLeft: Vector2.Zero,
            uvBottomRight: Vector2.One,
            color: new Vector4(1, 1, 1, opacity)
        );

        uint frame = (uint)(elapsed / 120) % _texture.ArrayLayers;
        var quadUV3 = QuadPrimitiveUV3.FromQuad(quad, layer: frame);
        PrimitiveSlice<QuadVertexUV3> slice = context.QuadsUV3.Append(quadUV3.AsSpan());

        batch.PushDraw(new Draw
        {
            Pipeline = shaderResources.Pipeline,
            ResourceBindings = new ResourceBindings(
                new ResourceSetKey(vp.ResourceLayout, vp.Buffer.VdBuffer),
                new ResourceSetKey(
                    shaderResources.ResourceLayout,
                    _texture,
                    context.GetSampler(FilterMode.Linear)
                )
            ),
            BufferBindings = new BufferBindings(slice.Vertices.Buffer, slice.Indices.Buffer),
            Params = DrawParams.Indexed(0, slice.IndexBase, 6)
        });
    }

    public void Dispose()
    {
        _texture.Dispose();
    }
}
