using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using NitroSharp.Content;
using Xunit;

namespace NitroSharp.Tests;

public sealed class LegacyPakReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EntriesDecodeAndReturnedStreamsOutliveTheArchive(bool compressed)
    {
        byte[] expected = Encoding.UTF8.GetBytes("Synthetic archive payload longer than sixteen bytes.");
        var archive = new LegacyPakReader(Build("script\\entry.nps", expected, compressed), Encoding.UTF8);
        Assert.True(archive.Contains("SCRIPT/ENTRY.NPS"));
        using Stream entry = archive.OpenStream("script/entry.nps");
        archive.Dispose();
        using var output = new MemoryStream();
        entry.CopyTo(output);
        Assert.Equal(expected, output.ToArray());
    }

    [Theory]
    [InlineData("../escape.nps")]
    [InlineData("/root.nps")]
    [InlineData("C:/root.nps")]
    [InlineData("script//entry.nps")]
    public void UnsafeEntryNamesAreRejected(string name)
        => Assert.Throws<InvalidDataException>(() => new LegacyPakReader(Build(name, [1, 2], false), Encoding.UTF8));

    [Fact]
    public void DecompressedSizeMismatchIsRejected()
    {
        using var archive = new LegacyPakReader(Build("entry.nps", [1, 2, 3], true, declaredSize: 2), Encoding.UTF8);
        Assert.Throws<InvalidDataException>(() => archive.OpenStream("entry.nps"));
    }

    [Fact]
    public void EntryOutsideTheArchiveIsRejected()
    {
        MemoryStream stream = Build("entry.nps", [1, 2, 3], false);
        stream.SetLength(stream.Length - 1);
        Assert.Throws<InvalidDataException>(() => new LegacyPakReader(stream, Encoding.UTF8));
    }

    private static MemoryStream Build(string name, byte[] bytes, bool compressed, int? declaredSize = null)
    {
        byte[] title = "fixture"u8.ToArray();
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        uint key = Hash(nameBytes);
        byte[] payload = compressed ? Compress(bytes) : (byte[])bytes.Clone();
        if (!compressed)
            for (int i = 0; i < Math.Min(16, payload.Length); i++) payload[i] ^= (byte)(key >> ((i % 4) * 8));
        using var index = new MemoryStream();
        using (var writer = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(nameBytes.Length); writer.Write(nameBytes);
            writer.Write(key); writer.Write((uint)(declaredSize ?? bytes.Length) ^ key);
            writer.Write(key); writer.Write((compressed ? 1u : 0u) ^ key); writer.Write((uint)payload.Length ^ key);
        }
        byte[] packed = Compress(index.ToArray());
        var archive = new MemoryStream();
        using var header = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true);
        header.Write(3); header.Write(title); header.Write(new byte[256 - title.Length]);
        header.Write(100); header.Write((uint)index.Length ^ Hash(title));
        header.Write(1u ^ Hash(title)); header.Write((uint)packed.Length ^ 100);
        header.Write(packed); header.Write(payload); archive.Position = 0;
        return archive;
    }

    private static uint Hash(byte[] bytes)
    {
        uint result = 0;
        foreach (byte value in bytes) result = unchecked(result * 137 + (uint)(sbyte)value);
        return result;
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var result = new MemoryStream();
        using (var compressor = new ZLibStream(result, CompressionMode.Compress, leaveOpen: true)) compressor.Write(bytes);
        return result.ToArray();
    }
}
