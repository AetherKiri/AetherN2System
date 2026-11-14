using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;
using NitroSharp.Content;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
string temporary = Path.Combine(Path.GetTempPath(), "aether-npa-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    byte[] text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("chapter main { WaitKey(); }\n", 400)));
    foreach (string scheme in new[] { "muramasa", "muramasa-ad" })
    foreach (bool encrypted in new[] { false, true })
    foreach (bool compressed in new[] { false, true })
    {
        string path = Path.Combine(temporary, "fixture.npa");
        WriteArchive(path, text, scheme, encrypted, compressed);
        using var mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var archive = NpaFile.TryLoad(mapping, Encoding.UTF8)!;
        using var stream = archive.OpenStream("BOOT.NSS");
        byte[] decoded = new byte[stream.Length];
        stream.ReadExactly(decoded);
        Require(decoded.SequenceEqual(text), $"decode {scheme}/{encrypted}/{compressed}");
        stream.Seek(100, SeekOrigin.Begin);
        Require(stream.ReadByte() == text[100], "decoded streams support random access");
    }
    // A malformed range must be rejected before any payload is mapped/allocated.
    string bad = Path.Combine(temporary, "bad.npa");
    WriteArchive(bad, text, "muramasa", false, false);
    byte[] invalid = File.ReadAllBytes(bad);
    BitConverter.GetBytes(uint.MaxValue).CopyTo(invalid, 41 + 4 + "boot.nss".Length + 5);
    File.WriteAllBytes(bad, invalid);
    bool rejected = false;
    using (var mapping = MemoryMappedFile.CreateFromFile(bad, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        try { using var archive = NpaFile.TryLoad(mapping, Encoding.UTF8); }
        catch (ArchiveException) { rejected = true; }
    Require(rejected, "out-of-range entry rejection");
    foreach (string path in args)
    {
        using var mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var archive = NpaFile.TryLoad(mapping, Encoding.GetEncoding("gbk"))!;
        long bytes = 0;
        foreach (string name in archive.FileNames)
        {
            using var stream = archive.OpenStream(name);
            bytes += stream.Length;
        }
        Console.WriteLine($"{Path.GetFileName(path)}: {archive.FileNames.Count()} entries, {bytes} decoded bytes");
    }
    Console.WriteLine("NPA_ARCHIVE_SMOKE_OK: compression, both ciphers, seek, tagged header, bounds");
}
finally { Directory.Delete(temporary, recursive: true); }

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static void WriteArchive(string path, byte[] original, string scheme, bool encrypted, bool compressed)
{
    byte[] name = "boot.nss"u8.ToArray();
    byte[] payload = original;
    if (compressed)
    {
        using var destination = new MemoryStream();
        using (var z = new ZLibStream(destination, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(original);
        payload = destination.ToArray();
    }
    const uint key1 = 0x6888587c, key2 = 0x79cd7a65;
    uint key = unchecked(key1 * key2);
    if (encrypted)
    {
        var table = scheme == "muramasa" ? NpaCipherTables.Muramasa : NpaCipherTables.MuramasaAd;
        byte[] inverse = new byte[256];
        for (int i = 0; i < 256; i++) inverse[table[i]] = (byte)i;
        uint nameKey = 0x87654321;
        foreach (byte b in name) nameKey = unchecked(nameKey - b);
        byte entryKey = unchecked((byte)((nameKey * (uint)name.Length + key) * original.Length));
        payload = payload.ToArray();
        for (int i = 0; i < Math.Min(payload.Length, 4096 + name.Length); i++)
            payload[i] = inverse[(payload[i] + entryKey + i) & 255];
    }
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write("NPA\x01\0\0\0"u8);
    writer.Write(key1); writer.Write(key2); writer.Write(compressed); writer.Write(encrypted);
    writer.Write(1); writer.Write(0); writer.Write(1); writer.Write(0x6575736e652300UL); writer.Write(21 + name.Length);
    writer.Write(name.Length);
    int sum = (int)((key & 255) + ((key >> 8) & 255) + ((key >> 16) & 255) + (key >> 24));
    for (int i = 0; i < name.Length; i++) writer.Write(unchecked((byte)(name[i] - 252 * i + sum)));
    writer.Write((byte)2); writer.Write(0); writer.Write(0); writer.Write(payload.Length); writer.Write(original.Length);
    writer.Write(payload);
}
namespace NitroSharp.Content
{
    internal abstract class ArchiveFile : IDisposable
    {
        public abstract Stream OpenStream(string path);
        public abstract bool Contains(string path);
        public abstract void Dispose();
    }
    internal sealed class ArchiveException(string format, string message) : Exception(format + ": " + message);
}
