using System;
using System.IO;
using System.Reflection;
using System.Text;
using NitroSharp.Media;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.Tests;

public sealed class SystemAudioSettingsTests
{
    [Fact]
    public void MissingSettingsDefaultToFullVolumeAndExplicitZeroRemainsMuted()
    {
        Assert.Equal(1, SystemAudioSettings.CalculateGain(null, null));
        Assert.Equal(0, SystemAudioSettings.CalculateGain(null, ConstantValue.Number(0)));
        Assert.Equal(0, SystemAudioSettings.CalculateGain(ConstantValue.Boolean(false), ConstantValue.Number(1000)));
        Assert.Equal(.35f, SystemAudioSettings.CalculateGain(ConstantValue.Boolean(true), ConstantValue.Number(350)));
    }

    [Fact]
    public void ScriptChannelFlagsAffectOnlyTheirOwnChannelAndCanUnmute()
    {
        using var globals = Globals("SYSTEM_sound_bgm", "SYSTEM_sound_volume_bgm",
            "SYSTEM_sound_se", "SYSTEM_sound_volume_se", "SYSTEM_sound_voice", "SYSTEM_sound_volume_voice");
        var vm = new NsScriptVM(new EmptyLocator(), globals);
        vm.GetFlag(0) = ConstantValue.Boolean(true);
        vm.GetFlag(1) = ConstantValue.Number(250);
        vm.GetFlag(2) = ConstantValue.Boolean(false);
        vm.GetFlag(3) = ConstantValue.Number(900);
        vm.GetFlag(4) = ConstantValue.Boolean(true);
        vm.GetFlag(5) = ConstantValue.Number(0);

        Assert.Equal(.25f, SystemAudioSettings.GetGain(vm, NsAudioKind.BackgroundMusic));
        Assert.Equal(0, SystemAudioSettings.GetGain(vm, NsAudioKind.SoundEffect));
        Assert.Equal(0, SystemAudioSettings.GetGain(vm, NsAudioKind.Voice));
        vm.GetFlag(2) = ConstantValue.Boolean(true);
        vm.GetFlag(5) = ConstantValue.Number(600);
        Assert.Equal(.9f, SystemAudioSettings.GetGain(vm, NsAudioKind.SoundEffect));
        Assert.Equal(.6f, SystemAudioSettings.GetGain(vm, NsAudioKind.Voice));
    }

    [Fact]
    public void CachedMissingNamesRefreshAfterGlobalsExpand()
    {
        using var globals = Globals();
        var vm = new NsScriptVM(new EmptyLocator(), globals);
        Assert.Equal(1, SystemAudioSettings.GetGain(vm, NsAudioKind.BackgroundMusic));
        GlobalsLookupTable expanded = vm.GlobalsLookup.WithGlobal("SYSTEM_sound_bgm", flag: true)
            .WithGlobal("SYSTEM_sound_volume_bgm", flag: true);
        typeof(NsScriptVM).GetMethod("UpdateGlobals", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { expanded });
        vm.GetFlag(0) = ConstantValue.Boolean(true);
        vm.GetFlag(1) = ConstantValue.Number(400);
        Assert.Equal(.4f, SystemAudioSettings.GetGain(vm, NsAudioKind.BackgroundMusic));
    }

    private static MemoryStream Globals(params string[] names)
    {
        using var heap = new MemoryStream();
        using var heapWriter = new BinaryWriter(heap, Encoding.UTF8, leaveOpen: true);
        var offsets = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            offsets[i] = (int)heap.Position;
            byte[] bytes = Encoding.UTF8.GetBytes(names[i]);
            heapWriter.Write((ushort)bytes.Length);
            heapWriter.Write(bytes);
        }
        var result = new MemoryStream();
        using var writer = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write((ushort)names.Length);
        foreach (int offset in offsets) writer.Write(offset);
        writer.Write((ushort)names.Length);
        for (ushort i = 0; i < names.Length; i++) writer.Write(i);
        writer.Write(heap.ToArray());
        result.Position = 0;
        return result;
    }

    private sealed class EmptyLocator : NsxModuleLocator
    {
        public override Stream OpenModule(string name) => throw new NotSupportedException();
        protected internal override SourceText LoadSourceText(string moduleName) => throw new NotSupportedException();
    }
}
