using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;

namespace NitroSharp.Content;

internal sealed class NpaFile : ArchiveFile
{
    private static ReadOnlySpan<byte> Magic => [0x4E, 0x50, 0x41, 1, 0, 0, 0];
    private const int HeaderSize = 41;
    private const int MaximumEntrySize = 512 * 1024 * 1024;
    private const uint SchemeNameKey = 0x87654321;
    private readonly record struct Entry(byte[] Name, long Offset, uint StoredSize, uint Size);
    private readonly Dictionary<string, Entry> _entries;
    private readonly MemoryMappedFile _file;
    private readonly uint _archiveKey;
    private readonly bool _compressed;
    private readonly byte[]? _cipher;

    private NpaFile(MemoryMappedFile file, Dictionary<string, Entry> entries, uint key,
        bool compressed, byte[]? cipher)
    {
        _file = file;
        _entries = entries;
        _archiveKey = key;
        _compressed = compressed;
        _cipher = cipher;
    }

    public static NpaFile? TryLoad(MemoryMappedFile file, Encoding encoding)
        => TryLoad(file, encoding, null);

    internal static NpaFile? TryLoad(MemoryMappedFile file, Encoding encoding, string? scheme)
    {
        using var stream = file.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
        if (stream.Length < HeaderSize) return null;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(7).AsSpan().SequenceEqual(Magic)) return null;
        uint key = unchecked(reader.ReadUInt32() * reader.ReadUInt32());
        byte compression = reader.ReadByte(), encryption = reader.ReadByte();
        if (compression > 1 || encryption > 1) throw Invalid("Invalid archive flags");
        uint count = reader.ReadUInt32(), folders = reader.ReadUInt32(), files = reader.ReadUInt32();
        // Publishers and translation patches use this reserved field for tags.
        _ = reader.ReadUInt64();
        uint indexSize = reader.ReadUInt32();
        long payloadStart = HeaderSize + (long)indexSize;
        if (count != (ulong)folders + files || count > 1_000_000 ||
            payloadStart > stream.Length || count > indexSize / 21)
            throw Invalid("Invalid directory size or entry count");

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < count; i++)
        {
            if (stream.Position + 4 > payloadStart) throw Invalid("Truncated directory");
            uint length = reader.ReadUInt32();
            if (length == 0 || length > 32768 || stream.Position + length + 17 > payloadStart)
                throw Invalid("Invalid entry name length");
            byte[] raw = reader.ReadBytes((int)length);
            for (int c = 0; c < raw.Length; c++)
                raw[c] = unchecked((byte)(raw[c] + 252 * c - ByteSum(key) - ByteSum(i)));
            byte type = reader.ReadByte();
            _ = reader.ReadUInt32();
            uint offset = reader.ReadUInt32(), stored = reader.ReadUInt32(), size = reader.ReadUInt32();
            if (type == 1) continue;
            if (type != 2 || payloadStart + offset + stored > stream.Length)
                throw Invalid("Entry range is outside the archive");
            string name = encoding.GetString(raw).Replace('\\', '/');
            if (name.IndexOf('\0') >= 0 || !entries.TryAdd(name, new Entry(raw, payloadStart + offset, stored, size)))
                throw Invalid("Duplicate or invalid entry name");
        }
        if (stream.Position != payloadStart) throw Invalid("Directory length does not match its entries");
        var archive = new NpaFile(file, entries, key, compression != 0, null);
        byte[]? cipher = encryption == 0 ? null : archive.SelectCipher(scheme);
        return new NpaFile(file, entries, key, compression != 0, cipher);
    }

    internal IEnumerable<string> FileNames => _entries.Keys;
    public override bool Contains(string path) => _entries.ContainsKey(path.Replace('\\', '/'));

    public override Stream OpenStream(string path)
    {
        if (!_entries.TryGetValue(path.Replace('\\', '/'), out var entry))
            throw new FileNotFoundException("File not found in the NPA archive", path);
        if (entry.StoredSize > MaximumEntrySize || entry.Size > MaximumEntrySize)
            throw Invalid("Entry exceeds the 512 MiB decoding limit");
        if (entry.StoredSize == 0 && entry.Size == 0) return new MemoryStream();
        using var input = _file.CreateViewStream(entry.Offset, entry.StoredSize, MemoryMappedFileAccess.Read);
        byte[] data = new byte[entry.StoredSize];
        input.ReadExactly(data);
        if (_cipher is not null) DecryptPrefix(data, entry, _cipher);
        if (IsPacked(path, entry))
        {
            using var packed = new MemoryStream(data, writable: false);
            using var inflater = new ZLibStream(packed, CompressionMode.Decompress);
            byte[] decoded = new byte[entry.Size];
            try
            {
                inflater.ReadExactly(decoded);
                if (inflater.ReadByte() != -1) throw Invalid("Decompressed entry exceeds its declared size");
            }
            catch (InvalidDataException ex) { throw Invalid("Invalid compressed entry: " + ex.Message); }
            catch (EndOfStreamException) { throw Invalid("Decompressed entry is shorter than its declared size"); }
            data = decoded;
        }
        else if (entry.StoredSize != entry.Size) throw Invalid("Uncompressed entry has inconsistent sizes");
        return new MemoryStream(data, writable: false);
    }

    private byte[] SelectCipher(string? scheme)
    {
        byte[][] candidates = scheme?.ToLowerInvariant() switch
        {
            null or "" or "auto" => [NpaCipherTables.Muramasa.ToArray(), NpaCipherTables.MuramasaAd.ToArray()],
            "muramasa" => [NpaCipherTables.Muramasa.ToArray()],
            "muramasa-ad" => [NpaCipherTables.MuramasaAd.ToArray()],
            _ => throw Invalid("Unknown NPA encryption scheme: " + scheme),
        };
        if (!string.IsNullOrWhiteSpace(scheme) && !scheme.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return candidates[0];
        foreach (var cipher in candidates)
        {
            // Validate known file signatures, without trusting a game folder's name.
            foreach (var pair in _entries
                .Where(x => Path.GetExtension(x.Key).Equals(".nss", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Value.StoredSize)
                .Take(128))
            {
                var entry = pair.Value;
                if (entry.StoredSize < 4) continue;
                int size = (int)Math.Min(Math.Max(64, entry.StoredSize == entry.Size ? 64 : 4096 + entry.Name.Length), entry.StoredSize);
                using var input = _file.CreateViewStream(entry.Offset, size, MemoryMappedFileAccess.Read);
                byte[] prefix = new byte[size]; input.ReadExactly(prefix);
                DecryptPrefix(prefix, entry, cipher);
                string extension = Path.GetExtension(pair.Key).ToLowerInvariant();
                if (IsPacked(pair.Key, entry))
                {
                    if (prefix[0] != 0x78 || ((prefix[0] << 8) + prefix[1]) % 31 != 0 ||
                        entry.StoredSize > MaximumEntrySize || entry.Size > MaximumEntrySize) continue;
                    try
                    {
                        using var payload = new MemoryStream(prefix, writable: false);
                        using var decoded = new ZLibStream(payload, CompressionMode.Decompress);
                        byte[] sample = new byte[Math.Min(256, (int)entry.Size)];
                        int read = decoded.Read(sample);
                        if (read >= 7 && (Encoding.UTF8.GetString(sample, 0, read).StartsWith("#include", StringComparison.Ordinal) ||
                            Encoding.UTF8.GetString(sample, 0, read).StartsWith("chapter ", StringComparison.Ordinal) ||
                            Encoding.GetEncoding("shift-jis").GetString(sample, 0, read).Contains("chapter", StringComparison.OrdinalIgnoreCase)))
                            return cipher;
                    }
                    catch (ArchiveException) { }
                    catch (InvalidDataException) { }
                }
                else if (HasSignature(prefix, extension)) return cipher;
            }
        }
        throw Invalid("No supported NPA encryption scheme matched the archive contents");
    }

    private static bool HasSignature(ReadOnlySpan<byte> data, string extension) => extension switch
    {
        ".png" => data.StartsWith(new byte[] {137, 80, 78, 71, 13, 10, 26, 10}),
        ".jpg" or ".jpeg" => data.StartsWith(new byte[] {255, 216, 255}),
        ".ogg" => data.StartsWith("OggS"u8),
        ".wav" => data.StartsWith("RIFF"u8),
        ".mpg" or ".mpeg" => data.StartsWith(new byte[] {0, 0, 1, 186}),
        ".nss" => data.StartsWith("#include"u8) || data.StartsWith("chapter "u8) || data.StartsWith("//"u8),
        _ => false,
    };

    private bool IsPacked(string path, Entry entry)
    {
        if (!_compressed) return false;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return entry.StoredSize != entry.Size || extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga");
    }

    private void DecryptPrefix(Span<byte> data, Entry entry, ReadOnlySpan<byte> cipher)
    {
        uint nameKey = SchemeNameKey;
        foreach (byte value in entry.Name) nameKey = unchecked(nameKey - value);
        byte key = unchecked((byte)((nameKey * (uint)entry.Name.Length + _archiveKey) * entry.Size));
        int limit = Math.Min(data.Length, 4096 + entry.Name.Length);
        for (int i = 0; i < limit; i++) data[i] = unchecked((byte)(cipher[data[i]] - key - i));
    }

    private static int ByteSum(uint value) => (int)((value & 255) + ((value >> 8) & 255) + ((value >> 16) & 255) + (value >> 24));
    private static ArchiveException Invalid(string message) => new("NPA", message);
    public override void Dispose() => _file.Dispose();
}
