using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MessagePack;
using NitroSharp.Common;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NitroSharp.Saving;

internal sealed class GameSaveManager
{
    private readonly GameContext _ctx;
    private readonly SaveSlotStorage _storage;
    private readonly Queue<uint> _pendingSaves = new();
    private byte[]? _pendingLoad;
    private byte[]? _storyThumbnail;

    public GameSaveManager(GameContext context)
    {
        _ctx = context;
        string writable = context.Profile.WritableDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AetherN2System", context.Profile.ProductName, context.Profile.Name);
        _storage = new SaveSlotStorage(context.Profile.SaveDataDirectory
            ?? SaveDirectory(writable, context.Profile.ProductName, context.Profile.Name),
            context.Profile.ProductName, context.Profile.Name);
        Directory.CreateDirectory(_storage.DirectoryPath);
        context.RootLog.Info($"N2System savedata directory: {_storage.DirectoryPath}");
        UpdateSavePath();
        if (_storage.Exists(0)) RequestLoad(0);
    }

    public string? LastError { get; private set; }
    internal static string SaveDirectory(string writable, string product, string profile)
    {
        string identity = Convert.ToHexString(CacheNameHash.HashData(Encoding.UTF8.GetBytes(product + "\0" + profile)))[..24];
        return Path.Combine(writable, "savedata", identity);
    }
    public bool HasPendingOperation => _pendingSaves.Count > 0 || _pendingLoad is not null;
    internal bool HasPendingLoad => _pendingLoad is not null;
    public bool SaveExists(uint slot) => _storage.Exists(slot);
    public DateTime? GetSlotDate(uint slot) => _storage.GetDate(slot);
    public void DeleteSave(uint slot) => _storage.Delete(slot);
    private void UpdateSavePath() => _ctx.VM.SetHostFlag("SYSTEM_save_path", ConstantValue.String(_storage.DirectoryPath));

    public void RequestSave(uint slot)
    {
        LastError = null;
        if (slot == 0)
        {
            try { _storage.Write(0, WriteGlobals(_ctx.VM.DumpFlags())); }
            catch (Exception error) { Report(error); }
            return;
        }
        _pendingSaves.Enqueue(slot);
    }

    public void RequestLoad(uint slot)
    {
        LastError = null;
        try
        {
            byte[] payload = _storage.Read(slot);
            if (slot == 0)
            {
                _ctx.VM.RestoreFlags(ReadGlobals(payload));
                UpdateSavePath();
            }
            else
            {
                // Validate framing before ending the requesting script slice.
                using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
                ReadCheckpoint(reader, out _, out _, out _);
                _pendingLoad = payload;
            }
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    internal bool ApplyPendingLoad()
    {
        if (_pendingLoad is not { } payload) return false;
        _pendingLoad = null;
        GlobalsDump previousVariables = _ctx.VM.DumpVariables();
        var previousTransient = _ctx.VM.CaptureTransientSaveState();
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            ReadCheckpoint(reader, out byte[] variables, out byte[] scene, out byte[] thumbnail);
            // Scene restoration can compile modules lazily. Load their saved
            // globals by name first, and retain current common flags/settings.
            _ctx.VM.RestoreVariables(ReadGlobals(variables));
            Process process = ScenePersistence.Restore(_ctx, scene);
            _ctx.World.ReplaceMainProcess(process);
            // Requests belonging to the replaced scene must never save the
            // newly loaded scene into a previous caller's slot.
            _pendingSaves.Clear();
            _ctx.Builtins.ResetInputState();
            _storyThumbnail = thumbnail;
            UpdateSavePath();
            LastError = null;
            return true;
        }
        catch (Exception error)
        {
            _ctx.VM.RestoreVariables(previousVariables);
            _ctx.VM.RestoreTransientSaveState(previousTransient);
            UpdateSavePath();
            Report(error);
            return false;
        }
    }

    internal void CompletePendingSaves()
    {
        while (_pendingSaves.TryDequeue(out uint slot))
        {
            try
            {
                byte[] variables = WriteGlobals(_ctx.VM.DumpVariables());
                byte[] scene = ScenePersistence.Capture(_ctx, _ctx.World.MainProcess);
                byte[] thumbnail = !_ctx.World.IsSystemMenu || _storyThumbnail is null
                    ? CaptureThumbnail() : _storyThumbnail;
                using var payload = new MemoryStream();
                using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(1);
                    writer.Write(_ctx.Profile.DesignResolution.Width);
                    writer.Write(_ctx.Profile.DesignResolution.Height);
                    WriteBytes(writer, variables);
                    WriteBytes(writer, scene);
                    WriteBytes(writer, thumbnail);
                }
                _storage.Write(slot, payload.ToArray());
                string directory = _storage.SlotDirectory(slot);
                SaveSlotStorage.AtomicWrite(Path.Combine(directory, "thum.npf"), thumbnail);
                foreach (string marker in new[] { "val.npf", "script.npf", "frames.npf", "bklg.npf" })
                    SaveSlotStorage.AtomicWrite(Path.Combine(directory, marker), []);
                LastError = null;
                _ctx.RootLog.Info($"Saved N2System slot {slot}.");
            }
            catch (Exception error) { Report(error); }
        }
    }

    internal void CaptureStoryFrame() => _storyThumbnail = CaptureThumbnail();

    private byte[] CaptureThumbnail()
    {
        var size = _ctx.RenderContext.RenderResolution;
        byte[] rgba = new byte[checked((int)(size.Width * size.Height * 4))];
        _ctx.RenderContext.CopyRgba(rgba);
        using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(rgba, (int)size.Width, (int)size.Height);
        image.Mutate(operation => operation.Resize(160, 90));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    private void ReadCheckpoint(BinaryReader reader, out byte[] variables, out byte[] scene, out byte[] thumbnail)
    {
        if (reader.ReadInt32() != 1 || reader.ReadUInt32() != _ctx.Profile.DesignResolution.Width
            || reader.ReadUInt32() != _ctx.Profile.DesignResolution.Height)
            throw new InvalidDataException("The save format or game resolution has changed.");
        variables = ReadBytes(reader);
        scene = ReadBytes(reader);
        thumbnail = ReadBytes(reader);
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("The save contains unexpected data.");
    }

    private static byte[] WriteGlobals(GlobalsDump globals)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        globals.Serialize(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static GlobalsDump ReadGlobals(byte[] bytes)
    {
        var reader = new MessagePackReader(bytes);
        var result = new GlobalsDump(ref reader);
        if (!reader.End) throw new InvalidDataException("Invalid saved globals.");
        return result;
    }

    private static void WriteBytes(BinaryWriter writer, byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    private static byte[] ReadBytes(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > SaveSlotStorage.MaximumPayloadLength || reader.BaseStream.Length - reader.BaseStream.Position < length)
            throw new InvalidDataException("The save is incomplete.");
        return reader.ReadBytes(length);
    }

    private void Report(Exception error)
    {
        LastError = error.Message;
        _ctx.RootLog.Error($"N2System save operation failed: {error.ToString()}");
    }
}
