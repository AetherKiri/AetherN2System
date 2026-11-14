using System;
using System.Numerics;
using System.Text;

namespace NitroSharp.Text;

/// <summary>Local glyph orientation for an @ font on a clockwise-rotated page.</summary>
internal static class VerticalGlyphOrientation
{
    public static bool IsUpright(Rune scalar)
    {
        int value = scalar.Value;
        // Brackets, long vowels and dashes take the page rotation. This gives
        // their vertical forms without requiring a second font or glyph atlas.
        if (value is 0x3008 or 0x3009 or 0x300A or 0x300B or 0x300C or 0x300D
            or 0x300E or 0x300F or 0x3010 or 0x3011 or 0x3014 or 0x3015
            or 0x3016 or 0x3017 or 0x3018 or 0x3019 or 0x301A or 0x301B
            or 0x30FC or 0x2014 or 0x2015 or 0x2026 or 0x2025
            or 0xFF08 or 0xFF09 or 0xFF3B or 0xFF3D or 0xFF5B or 0xFF5D)
            return false;

        return value is >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7AF
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE1F
            or >= 0xFE30 and <= 0xFE4F
            or >= 0xFF01 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x20000 and <= 0x323AF;
    }

    public static Matrix4x4 GetLocalTransform(Rune scalar, Vector2 position, Vector2 bitmapSize,
        float emSize)
    {
        if (!IsUpright(scalar))
            return Matrix4x4.Identity;

        Vector2 center = position + bitmapSize * 0.5f;
        Matrix4x4 rotation = Matrix4x4.CreateRotationZ(-MathF.PI / 2, new Vector3(center, 0));
        // Commas and full stops sit at the upper right of a vertical cell.
        // The outer +90-degree rotation maps this local offset to (+x, -y).
        if (scalar.Value is 0x3001 or 0x3002 or 0xFF0C or 0xFF0E)
            rotation *= Matrix4x4.CreateTranslation(-emSize * 0.2f, -emSize * 0.2f, 0);
        return rotation;
    }
}
