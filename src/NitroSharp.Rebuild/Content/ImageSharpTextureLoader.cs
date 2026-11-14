using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;

namespace NitroSharp.Content;

// Static images do not need FFmpeg initialization and remain AOT-compatible.
internal sealed class ImageSharpTextureLoader(GraphicsDevice device) : TextureLoader(device)
{
    public override TextureSizeU GetTextureSize(Stream stream)
    {
        stream.Position = 0;
        ImageInfo info = Image.Identify(stream);
        return new TextureSizeU((uint)info.Width, (uint)info.Height);
    }

    protected override unsafe Texture LoadStaging(Stream stream)
    {
        stream.Position = 0;
        using Image<Rgba32> image = Image.Load<Rgba32>(stream);
        Texture texture = _rf.CreateTexture(
            TextureDescription.Texture2D(
                (uint)image.Width,
                (uint)image.Height,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Staging
            )
        );
        MappedResource map = _gd.Map(texture, MapMode.Write);
        byte[] row = new byte[image.Width * 4];
        try
        {
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < image.Height; y++)
                {
                    System
                        .Runtime.InteropServices.MemoryMarshal.AsBytes(accessor.GetRowSpan(y))
                        .CopyTo(row);
                    row.AsSpan()
                        .CopyTo(new Span<byte>((byte*)map.Data + y * map.RowPitch, row.Length));
                }
            });
        }
        finally
        {
            _gd.Unmap(texture);
        }
        return texture;
    }
}
