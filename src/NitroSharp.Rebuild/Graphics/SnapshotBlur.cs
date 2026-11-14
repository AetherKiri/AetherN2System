using System;
using NitroSharp.Graphics.Core;
using Veldrid;

namespace NitroSharp.Graphics;

internal static class SnapshotBlur
{
    public static SpriteTexture Apply(RenderContext context, in SpriteTexture source, uint passes)
    {
        Texture input = source.Resolve(context);
        using var first = new RenderTarget(context.GraphicsDevice, new ScreenSizeU(input.Width, input.Height));
        using var second = new RenderTarget(context.GraphicsDevice, first.Size, first.ColorTarget.Format);
        using CommandList commands = context.ResourceFactory.CreateCommandList();
        var bindings = new ResourceSet[3];
        Texture? result = null;
        try
        {
            var resources = context.ShaderResources.Effects;
            Texture[] inputs = [input, first.ColorTarget, second.ColorTarget];
            for (int i = 0; i < inputs.Length; i++)
                bindings[i] = context.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                    resources.ResourceLayout, inputs[i], context.GetSampler(FilterMode.Linear)));
            result = context.ResourceFactory.CreateTexture(TextureDescription.Texture2D(input.Width, input.Height,
                1, 1, first.ColorTarget.Format, TextureUsage.Sampled));
            commands.Begin();
            RenderTarget target = first;
            for (int pass = 0; pass < passes; pass++)
            {
                target = pass % 2 == 0 ? first : second;
                commands.SetFramebuffer(target.Framebuffer);
                commands.SetFullViewports();
                commands.SetFullScissorRect(0);
                commands.ClearColorTarget(0, RgbaFloat.Clear);
                commands.SetPipeline(resources.GetPipeline(EffectKind.BoxBlur));
                commands.SetGraphicsResourceSet(0, bindings[pass == 0 ? 0 : pass % 2 == 0 ? 2 : 1]);
                commands.Draw(3);
            }
            commands.CopyTexture(target.ColorTarget, result);
            commands.End();
            context.GraphicsDevice.SubmitCommands(commands);
            // Temporary framebuffers and bindings must survive the submitted passes.
            context.GraphicsDevice.WaitForIdle();
            return source.WithOwnedTexture(result);
        }
        catch { result?.Dispose(); throw; }
        finally { foreach (ResourceSet? binding in bindings) binding?.Dispose(); }
    }
}
