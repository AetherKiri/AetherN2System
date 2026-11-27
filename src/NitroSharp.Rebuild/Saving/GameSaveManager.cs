using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MessagePack;
using NitroSharp.Common;
using NitroSharp.Graphics;
using NitroSharp.Graphics.Core;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;
using NitroSharp.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace NitroSharp.Saving;

internal sealed class GameSaveManager
{
    private const string TextSpeedFlag = "SYSTEM_text_speed";
    private const string TextSpeedInitializedFlag = "AETHER_hanachirasu_text_speed_initialized";
    private readonly GameContext _ctx;
    private readonly SaveSlotStorage _storage;
    private readonly Queue<uint> _pendingSaves = new();
    // Read history belongs to the installation, not a particular slot. Only append newly completed blocks.
    private readonly HashSet<string> _readDialogue = new(StringComparer.Ordinal);
    private string ReadHistoryPath => Path.Combine(_storage.DirectoryPath, "read-dialogue-v1.txt");
    internal bool HasRead(string key) => _readDialogue.Contains(key);
    internal void MarkRead(string key)
    {
        if (!_readDialogue.Add(key)) return;
        try { File.AppendAllLines(ReadHistoryPath, [key]); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _ctx.RootLog.Warn($"Cannot persist dialogue history: {error.Message}"); }
    }
    private byte[]? _pendingLoad;
    private byte[]? _storyThumbnail;
    private bool _captureWithoutText;

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
        try
        {
            if (File.Exists(ReadHistoryPath)) _readDialogue.UnionWith(File.ReadLines(ReadHistoryPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { context.RootLog.Warn($"Cannot load dialogue history: {error.Message}"); }
        UpdateSavePath();
        if (_storage.Exists(0)) RequestLoad(0);
        InitializeTextSpeed();
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

    private void InitializeTextSpeed()
    {
        if (_ctx.Profile.DefaultTextSpeed is not { } defaultSpeed) return;

        var vm = _ctx.VM;
        float? speed = vm.GetHostFlag(TextSpeedFlag).AsNumber();
        bool hanachirasu = _ctx.Profile.NpaScheme == "hanachirasu";
        // Older builds could persist the VM's implicit zero for an unset speed.
        // Record the migration in common flags so the player can still choose instant text.
        if (speed is null || (hanachirasu && speed == 0 && vm.GetHostFlag(TextSpeedInitializedFlag).AsBool() != true))
            vm.SetHostFlag(TextSpeedFlag, ConstantValue.Number(defaultSpeed));
        if (hanachirasu) vm.SetHostFlag(TextSpeedInitializedFlag, ConstantValue.True);
    }

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
                InitializeTextSpeed();
            }
            else
            {
                // Validate framing before ending the requesting script slice.
                using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
                ReadCheckpoint(reader, out _, out _, out _, out _);
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
            ReadCheckpoint(reader, out byte[] variables, out byte[] scene, out byte[] thumbnail, out BacklogEntry[] backlog);
            // Scene restoration can compile modules lazily. Load their saved
            // globals by name first, and retain current common flags/settings.
            _ctx.VM.RestoreVariables(ReadGlobals(variables));
            Process process = ScenePersistence.Restore(_ctx, scene);
            _ctx.World.ReplaceMainProcess(process);
            // Requests belonging to the replaced scene must never save the
            // newly loaded scene into a previous caller's slot.
            _pendingSaves.Clear();
            _ctx.Builtins.ResetInputState();
            _ctx.Builtins.RestoreBacklog(backlog);
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

    internal void CompletePendingSaves(RenderTarget? storyFrame)
    {
        if (storyFrame is not null)
        {
            try { _storyThumbnail = CaptureThumbnail(storyFrame); }
            catch (Exception error) { Report(error); }
        }
        while (_pendingSaves.TryDequeue(out uint slot))
        {
            try
            {
                byte[] variables = WriteGlobals(_ctx.VM.DumpVariables());
                byte[] scene = ScenePersistence.Capture(_ctx, _ctx.World.MainProcess);
                byte[] thumbnail = !_ctx.World.IsSystemMenu ? CaptureThumbnail(storyFrame)
                    : _storyThumbnail ?? CaptureThumbnail();
                using var payload = new MemoryStream();
                using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(2);
                    writer.Write(_ctx.Profile.DesignResolution.Width);
                    writer.Write(_ctx.Profile.DesignResolution.Height);
                    WriteBytes(writer, variables);
                    WriteBytes(writer, scene);
                    WriteBytes(writer, thumbnail);
                    WriteBacklog(writer);
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

    internal void CaptureStoryFrame()
    {
        if (_ctx.Profile.SaveThumbnailSize.HasValue)
            _captureWithoutText = true;
        else
            _storyThumbnail = CaptureThumbnail();
    }

    internal RenderTarget? RecordStoryThumbnail()
    {
        if (!_captureWithoutText && !(_pendingSaves.Count != 0
            && !_ctx.World.IsSystemMenu && _ctx.Profile.SaveThumbnailSize.HasValue)) return null;
        _captureWithoutText = false;
        var target = _ctx.RenderContext.CreateSceneTarget();
        try
        {
            _ctx.World.MainProcess.CaptureWithoutText(_ctx, target);
            return target;
        }
        catch (Exception error)
        {
            GraphicsUtils.DisposeAfterIdle(_ctx.RenderContext.GraphicsDevice, target);
            Report(error);
            return null;
        }
    }

    private byte[] CaptureThumbnail(RenderTarget? source = null)
    {
        var size = _ctx.RenderContext.RenderResolution;
        byte[] rgba = new byte[checked((int)(size.Width * size.Height * 4))];
        _ctx.RenderContext.CopyRgba(rgba, source);
        using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(rgba, (int)size.Width, (int)size.Height);
        var render = _ctx.RenderContext;
        var target = _ctx.Profile.SaveThumbnailSize ?? new DesignSizeU(160, 90);
        if (_ctx.Profile.SaveThumbnailSize.HasValue)
        {
            int left = (int)MathF.Floor(render.ViewportLeft);
            int top = (int)MathF.Floor(render.ViewportTop);
            int right = (int)MathF.Ceiling(render.ViewportRight);
            int bottom = (int)MathF.Ceiling(render.ViewportBottom);
            image.Mutate(operation => operation.Crop(new SixLabors.ImageSharp.Rectangle(left, top, right - left, bottom - top)));
        }
        image.Mutate(operation => operation.Resize((int)target.Width, (int)target.Height));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    internal Stream? OpenThumbnail(string path)
    {
        if (_ctx.Profile.SaveThumbnailSize is not { } size
            || SaveTextFileIO.ResolvePath(_storage.DirectoryPath, path) is not { } full
            || !Path.GetFileName(full).Equals("thum.npf", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(Path.GetFileName(Path.GetDirectoryName(full)), out uint slot)
            || full != Path.Combine(_storage.SlotDirectory(slot), "thum.npf")
            || !File.Exists(full))
            return null;

        using var image = Image.Load<Rgba32>(full);
        if (image.Width != size.Width || image.Height != size.Height)
            image.Mutate(operation => operation.Resize((int)size.Width, (int)size.Height));
        var stream = new MemoryStream();
        image.SaveAsPng(stream);
        stream.Position = 0;
        return stream;
    }

    private void WriteBacklog(BinaryWriter writer)
    {
        var entries = _ctx.Builtins.Backlog;
        writer.Write(entries.Count);
        foreach (BacklogEntry entry in entries)
        {
            writer.Write(entry.Text);
            writer.Write(entry.Voice is not null);
            if (entry.Voice is { } voice)
            {
                writer.Write(voice.CharacterName);
                writer.Write(voice.FileName);
            }
        }
    }

    private void ReadCheckpoint(BinaryReader reader, out byte[] variables, out byte[] scene, out byte[] thumbnail,
        out BacklogEntry[] backlog)
    {
        int version = reader.ReadInt32();
        if (version is not (1 or 2) || reader.ReadUInt32() != _ctx.Profile.DesignResolution.Width
            || reader.ReadUInt32() != _ctx.Profile.DesignResolution.Height)
            throw new InvalidDataException("The save format or game resolution has changed.");
        variables = ReadBytes(reader);
        scene = ReadBytes(reader);
        thumbnail = ReadBytes(reader);
        int count = version == 2 ? reader.ReadInt32() : 0;
        if (count < 0 || count > Builtins.MaximumBacklogEntries)
            throw new InvalidDataException("The save contains an invalid backlog.");
        backlog = new BacklogEntry[count];
        for (int i = 0; i < count; i++)
        {
            string text = reader.ReadString();
            VoiceSegment? voice = reader.ReadBoolean()
                ? new VoiceSegment(reader.ReadString(), reader.ReadString(), NsVoiceAction.Play) : null;
            backlog[i] = new BacklogEntry(text, voice);
        }
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
