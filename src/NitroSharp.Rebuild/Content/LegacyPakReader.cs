using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NitroSharp.Content;

// Early N2System PAK v3. Format details cross-checked against GARbro's
// ArcFormats/NitroPlus/ArcNitro.cs; this reader does not select a script runtime.
internal sealed class LegacyPakReader : IDisposable
{
    internal sealed record Entry(string Name, long Offset, int StoredSize, int UnpackedSize, bool Compressed, uint Key);
    private const int MaxIndexSize = 64 * 1024 * 1024;
    private const int MaxEntrySize = 256 * 1024 * 1024;
    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Entry> Entries => _entries.Values;

    public LegacyPakReader(Stream input, Encoding nameEncoding, bool leaveOpen = false)
    {
        _input = input;
        _leaveOpen = leaveOpen;
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("A readable, seekable PAK stream is required.", nameof(input));
        try { ReadIndex(nameEncoding); }
        catch { if (!leaveOpen) input.Dispose(); throw; }
    }

    private void ReadIndex(Encoding encoding)
    {
        _input.Position = 0;
        byte[] header = new byte[0x114];
        _input.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 3 || ReadUInt(header, 0x104) != 100)
            throw new InvalidDataException("Unsupported early N2System PAK header.");
        int titleLength = Array.IndexOf(header, (byte)0, 4, 256) - 4;
        if (titleLength is < 1 or > 16) throw new InvalidDataException("Invalid PAK title.");
        foreach (byte value in header.AsSpan(4, titleLength))
            if (value is < 32 or >= 128) throw new InvalidDataException("Invalid PAK title.");
        uint key = CalculateKey(header.AsSpan(4, titleLength));
        uint indexSize = ReadUInt(header, 0x108) ^ key;
        uint count = ReadUInt(header, 0x10c) ^ key;
        uint storedSize = ReadUInt(header, 0x110) ^ 100;
        if (indexSize > MaxIndexSize || count is 0 or > 100000 || storedSize > MaxIndexSize
            || 0x114L + storedSize > _input.Length)
            throw new InvalidDataException("Invalid PAK index bounds.");
        byte[] packed = new byte[checked((int)storedSize)];
        _input.ReadExactly(packed);
        byte[] index = Inflate(packed, checked((int)indexSize));
        using var reader = new BinaryReader(new MemoryStream(index));
        long dataStart = 0x114L + storedSize;
        for (uint i = 0; i < count; i++)
        {
            int length = reader.ReadInt32();
            if (length is < 1 or > 256) throw new InvalidDataException("Invalid PAK entry name length.");
            byte[] nameBytes = reader.ReadBytes(length);
            if (nameBytes.Length != length) throw new EndOfStreamException();
            string name = Normalize(encoding.GetString(nameBytes));
            uint entryKey = CalculateKey(nameBytes);
            long offset = dataStart + (reader.ReadUInt32() ^ entryKey);
            uint unpacked = reader.ReadUInt32() ^ entryKey;
            _ = reader.ReadUInt32();
            bool compressed = (reader.ReadUInt32() ^ entryKey) != 0;
            uint compressedSize = reader.ReadUInt32() ^ entryKey;
            uint stored = compressed ? compressedSize : unpacked;
            if (unpacked > MaxEntrySize || stored > MaxEntrySize || offset > _input.Length - stored)
                throw new InvalidDataException("Invalid PAK entry bounds.");
            if (!_entries.TryAdd(name, new Entry(name, offset, (int)stored, (int)unpacked, compressed, entryKey)))
                throw new InvalidDataException("Duplicate PAK entry name.");
        }
        if (reader.BaseStream.Position != index.Length) throw new InvalidDataException("Unexpected PAK index data.");
    }

    public Stream OpenStream(string path)
    {
        if (!_entries.TryGetValue(Normalize(path), out Entry? entry)) throw new FileNotFoundException("PAK entry not found.", path);
        byte[] bytes = new byte[entry.StoredSize];
        // Each returned stream owns its bytes; disposing the archive cannot invalidate it.
        lock (_input)
        {
            _input.Position = entry.Offset;
            _input.ReadExactly(bytes);
        }
        if (entry.Compressed) bytes = Inflate(bytes, entry.UnpackedSize);
        else
        {
            uint key = entry.Key;
            for (int i = 0; i < Math.Min(16, bytes.Length); i++)
            {
                bytes[i] ^= (byte)key;
                key = (key >> 8) | (key << 24);
            }
        }
        return new MemoryStream(bytes, writable: false);
    }

    public bool Contains(string path) => _entries.ContainsKey(Normalize(path));

    private static byte[] Inflate(byte[] bytes, int size)
    {
        byte[] result = new byte[size];
        using var zlib = new ZLibStream(new MemoryStream(bytes, writable: false), CompressionMode.Decompress);
        zlib.ReadExactly(result);
        if (zlib.ReadByte() != -1) throw new InvalidDataException("PAK decompressed size mismatch.");
        return result;
    }

    private static string Normalize(string name)
    {
        name = name.Replace('\\', '/');
        foreach (string segment in name.Split('/'))
            if (segment is "" or "." or ".." || segment.Contains(':') || segment.Contains('\0'))
                throw new InvalidDataException("Invalid PAK entry path.");
        return name;
    }

    internal static uint CalculateKey(ReadOnlySpan<byte> bytes)
    {
        uint key = 0;
        foreach (byte value in bytes) key = unchecked(key * 137 + (uint)(sbyte)value);
        return key;
    }

    private static uint ReadUInt(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    public void Dispose() { if (!_leaveOpen) _input.Dispose(); }
}
