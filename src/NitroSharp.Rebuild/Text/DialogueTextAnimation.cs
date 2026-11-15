using System;

namespace NitroSharp.Text;

// Layout is performed once when a segment is appended. Subsequent frames only
// change the opacity buffer consumed by TextRenderContext.
internal sealed partial class DialogueTextAnimation
{
    private readonly TextLayout _layout;
    private readonly int _start;
    private readonly float[] _characterPositions;
    private readonly float _interval;
    private readonly float _fadeDuration;
    private float _elapsed;

    public DialogueTextAnimation(TextLayout layout, int start, float interval, float fadeDuration)
    {
        _layout = layout;
        _start = start;
        _interval = MathF.Max(0, interval);
        _fadeDuration = MathF.Max(0, fadeDuration);
        _characterPositions = new float[layout.Glyphs.Length - start];
        int position = 0;
        int rubyStart = 0, rubyLength = 0;
        foreach (ref readonly GlyphRun run in layout.GlyphRuns)
        {
            var (offset, length) = run.GlyphSpan.GetOffsetAndLength(layout.Glyphs.Length);
            if (offset + length <= start) continue;
            int first = Math.Max(offset, start);
            if (run.IsRubyText && rubyLength > 0)
            {
                for (int i = first; i < offset + length; i++)
                    _characterPositions[i - start] = rubyStart
                        + (float)(i - offset) / Math.Max(1, length - 1) * (rubyLength - 1);
            }
            else
            {
                if (run.IsRubyBase) { rubyStart = position; rubyLength = offset + length - first; }
                else rubyLength = 0;
                for (int i = first; i < offset + length; i++)
                    _characterPositions[i - start] = position++;
            }
        }
        CharacterCount = position;
        IsActive = _characterPositions.Length > 0 && (_interval > 0 || _fadeDuration > 0);
        Apply();
    }

    public int CharacterCount { get; }
    public bool IsActive { get; private set; }

    public void Update(float deltaMilliseconds)
    {
        if (!IsActive) return;
        _elapsed += MathF.Max(0, deltaMilliseconds);
        Apply();
    }

    public void Complete()
    {
        _layout.GetOpacityValuesMut(_start..(_start + _characterPositions.Length)).Fill(1);
        IsActive = false;
    }

    private void Apply()
    {
        Span<float> values = _layout.GetOpacityValuesMut(_start..(_start + _characterPositions.Length));
        bool active = false;
        for (int i = 0; i < values.Length; i++)
        {
            float age = _elapsed - _characterPositions[i] * _interval;
            values[i] = _fadeDuration > 0 ? Math.Clamp(age / _fadeDuration, 0, 1) : age >= 0 ? 1 : 0;
            active |= values[i] < 1;
        }
        IsActive = active;
    }
}
