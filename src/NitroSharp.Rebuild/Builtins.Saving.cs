using System;
using System.IO;
using NitroSharp.NsScript;

namespace NitroSharp;

internal sealed partial class Builtins
{
    public override bool SaveExists(uint slot) => _ctx.Saves.SaveExists(slot);
    public override bool MountSaveData(uint slot) => SaveExists(slot);
    public override bool X360_UserDataExists() => SaveExists(0);
    public override bool FileExists(string path)
    {
        string native = SaveTextFileIO.NativePath(path);
        if (SaveTextFileIO.Exists(SaveTextDirectory, native)) return true;
        if (File.Exists(native)) return true;
        using Stream? stream = _ctx.Content.TryOpenStream(native);
        return stream is not null;
    }
    public override void DeleteSave(uint slot) => _ctx.Saves.DeleteSave(slot);
    public override void SaveGame(uint slot)
    {
        _ctx.Saves.RequestSave(slot);
        // Finish dispatch before capturing the VM program counter and stack.
        if (slot != 0) CurrentThread.Wait(Thread.WaitOperation.Suspend(Clock.Elapsed));
    }
    public override void LoadGame(uint slot) => _ctx.Saves.RequestLoad(slot);
    public override void OpenSaveMenu(bool saving) => _world.OpenNativeSaveMenu(_ctx, saving);

    internal void ResetInputState()
    {
        _choiceInputs.Clear();
        _focusedScrollbar = null;
        _draggedScrollbar = null;
    }
}
