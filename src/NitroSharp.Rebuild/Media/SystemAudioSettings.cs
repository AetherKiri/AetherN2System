using System;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;

namespace NitroSharp.Media;

internal static class SystemAudioSettings
{
    public static float GetGain(NsScriptVM vm, NsAudioKind kind)
    {
        // SystemVariableLookup caches these indices and refreshes them when
        // on-demand compilation or dynamic globals replace the lookup table.
        (ConstantValue? enabled, ConstantValue? volume) = vm.SystemVariables.GetAudioSettings(kind);
        return CalculateGain(enabled, volume);
    }

    internal static float CalculateGain(ConstantValue? enabled, ConstantValue? volume)
    {
        if (!(enabled?.AsBool() ?? true)) return 0;
        float value = volume?.AsNumber() ?? 1000;
        return float.IsFinite(value) ? Math.Clamp(value / 1000, 0, 1) : 1;
    }
}
