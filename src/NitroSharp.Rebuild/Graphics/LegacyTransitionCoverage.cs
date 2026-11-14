using System;

namespace NitroSharp.Graphics;

internal static class LegacyTransitionCoverage
{
    internal static TimeSpan QuantizeDuration(TimeSpan duration)
    {
        const long interval = 128 * TimeSpan.TicksPerMillisecond;
        return TimeSpan.FromTicks(duration.Ticks / interval * interval);
    }

    internal static int LastStep(float progress)
        => Math.Clamp((int)MathF.Floor(Math.Max(0, progress) * 128), 0, 127);

    // Coverage since the preceding draw, applied to the previous composite.
    // Reapplying cumulative coverage would repeatedly count earlier steps.
    internal static float Incremental(int previousStep, int currentStep)
    {
        double remaining = 1;
        for (int i = Math.Max(0, previousStep + 1); i <= Math.Min(127, currentStep); i++)
            remaining *= (256 - 2 * i) / 256.0;
        return (float)(1 - remaining);
    }

    // The old renderer repeatedly mixes the new scene into its previous result
    // at progress 0,2,...254, then copies the final scene. Reconstruct all steps
    // from elapsed time so frame drops do not change the effect's appearance.
    internal static float Calculate(float progress, int maskByte, bool masked)
    {
        if (progress >= 1) return 1;
        int last = LastStep(progress);
        double remaining = 1;
        for (int i = 0; i <= last; i++)
        {
            int value = masked ? Math.Clamp(8 * i - maskByte, 0, 255) : 2 * i;
            if (value >= 255) return 1;
            remaining *= (256 - value) / 256.0;
        }
        return (float)(1 - remaining);
    }
}
