using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace NitroSharp.Content;

internal sealed class LegacyPakArchive : ArchiveFile
{
    private readonly LegacyPakReader _reader;
    private readonly MemoryMappedFile _mapping;

    private LegacyPakArchive(MemoryMappedFile mapping, Stream stream, Encoding encoding)
    {
        _mapping = mapping;
        _reader = new LegacyPakReader(stream, encoding);
    }

    public static ArchiveFile? TryLoad(MemoryMappedFile mapping, Encoding encoding)
    {
        using (var header = mapping.CreateViewStream(0, 0, MemoryMappedFileAccess.Read))
        {
            Span<byte> signature = stackalloc byte[4];
            if (header.Read(signature) != 4 || BinaryPrimitives.ReadUInt32LittleEndian(signature) != 3) return null;
        }
        var stream = mapping.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
        try { return new LegacyPakArchive(mapping, stream, encoding); }
        catch { stream.Dispose(); throw; }
    }

    public override bool Contains(string path) => _reader.Contains(path);
    public override Stream OpenStream(string path) => _reader.OpenStream(path);
    public override void Dispose() { _reader.Dispose(); _mapping.Dispose(); }
}
