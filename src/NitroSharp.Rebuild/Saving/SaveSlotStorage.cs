using System;
using System.IO;
using System.Text;
using NitroSharp.Common;

namespace NitroSharp.Saving;

// The checkpoint is one atomic file. Menu sidecars are deliberately separate:
// authored scripts write their own comments, chapter names and dates.
internal sealed class SaveSlotStorage(string directory, string product, string profile)
{
    private const ulong Magic = 0x3156415353324E41; // AN2SSAV1
    internal const int MaximumPayloadLength = 256 * 1024 * 1024;
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public string SlotDirectory(uint slot) => Path.Combine(DirectoryPath, slot == 0 ? "common" : slot.ToString("D4"));
    private string SlotFile(uint slot) => Path.Combine(SlotDirectory(slot), slot == 0 ? "val.npf" : $"{slot:D4}.sav");

    public void Write(uint slot, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPayloadLength) throw new InvalidDataException("The save exceeds the supported size.");
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(1);
            writer.Write(slot);
            writer.Write(product);
            writer.Write(profile);
            writer.Write(payload.Length);
            writer.Write(CacheNameHash.HashData(payload));
            writer.Write(payload);
        }
        AtomicWrite(SlotFile(slot), buffer.ToArray());
    }

    public byte[] Read(uint slot)
    {
        using FileStream file = File.OpenRead(SlotFile(slot));
        if (file.Length > MaximumPayloadLength + 64 * 1024L) throw new InvalidDataException("The save exceeds the supported size.");
        using var reader = new BinaryReader(file, Encoding.UTF8);
        if (reader.ReadUInt64() != Magic || reader.ReadInt32() != 1 || reader.ReadUInt32() != slot)
            throw new InvalidDataException("This is not a supported Aether N2System save.");
        if (reader.ReadString() != product || reader.ReadString() != profile)
            throw new InvalidDataException("The save belongs to another game or profile.");
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumPayloadLength || file.Length - file.Position != length + 32L)
            throw new InvalidDataException("The save is incomplete.");
        byte[] checksum = reader.ReadBytes(32);
        byte[] payload = reader.ReadBytes(length);
        if (!checksum.AsSpan().SequenceEqual(CacheNameHash.HashData(payload)))
            throw new InvalidDataException("The save is damaged.");
        return payload;
    }

    public bool Exists(uint slot)
    {
        try
        {
            using var file = File.OpenRead(SlotFile(slot));
            using var reader = new BinaryReader(file, Encoding.UTF8);
            return file.Length >= 52 && reader.ReadUInt64() == Magic && reader.ReadInt32() == 1
                && reader.ReadUInt32() == slot && reader.ReadString() == product && reader.ReadString() == profile;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public DateTime? GetDate(uint slot) => Exists(slot) ? File.GetLastWriteTime(SlotFile(slot)) : null;

    public void Delete(uint slot)
    {
        string path = SlotDirectory(slot);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    internal static void AtomicWrite(string path, ReadOnlySpan<byte> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(data);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
