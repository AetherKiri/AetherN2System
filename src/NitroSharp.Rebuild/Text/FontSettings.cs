using System.Numerics;

namespace NitroSharp.Text;

[Persistable]
internal sealed partial record FontSettings
{
    public FontFaceKey DefaultFont { get; init; }
    public FontFaceKey? ItalicFont { get; init; }
    public PtFontSize DefaultFontSize { get; init; }
    public Vector4 DefaultTextColor { get; init; }
    public Vector4? DefaultOutlineColor { get; init; }
    public float RubyFontSizeMultiplier { get; init; }
    // @ fonts supply upright CJK glyphs to a page that the script rotates +90°.
    public bool VerticalGlyphs { get; init; }

    public FontSettings()
    {
    }
}
