using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using NitroSharp.Common;

namespace AetherN2System;

internal static class SaveStorageLocation
{
    private const long MaximumFileLength = 256L * 1024 * 1024 + 64 * 1024;
    private const long MaximumMigrationLength = 2L * 1024 * 1024 * 1024;
    private const int MaximumMigrationFiles = 20_000;

    internal static string Identity(string product, string profile)
        => Convert.ToHexString(CacheNameHash.HashData(Encoding.UTF8.GetBytes(product + "\0" + profile)))[..24];

    internal static string Select(string gameRoot, string writable, string product, string profile)
    {
        string identity = Identity(product, profile);
        string previous = Path.GetFullPath(Path.Combine(writable, "savedata", identity));
        string preferred = Path.GetFullPath(Path.Combine(gameRoot, "savedata", "AetherN2System", identity));
        if (CanWrite(preferred))
        {
            if (!PathsEqual(previous, preferred)) MigrateOnce(previous, preferred);
            return preferred;
        }
        if (!CanWrite(previous)) throw new IOException("No writable N2System save directory is available.");
        return previous;
    }

    private static bool CanWrite(string directory)
    {
        string? probe = null;
        try
        {
            Directory.CreateDirectory(directory);
            probe = Path.Combine(directory, ".aether-save-probe-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (IsFileError(error)) { return false; }
        finally
        {
            if (probe is not null)
                try { File.Delete(probe); } catch (Exception error) when (IsFileError(error)) { }
        }
    }

    private static void MigrateOnce(string source, string destination)
    {
        if (!Directory.Exists(source) || IsLink(source)) return;
        string targetIdentity = Convert.ToHexString(CacheNameHash.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? destination.ToUpperInvariant() : destination)))[..24];
        // Keep completion beside the old app data: deleting the entire game
        // savedata directory must not resurrect a previously migrated checkpoint.
        string marker = Path.Combine(source, ".migrated-to-game-" + targetIdentity);
        if (File.Exists(marker)) return;
        long copied = 0;
        int files = 0, slots = 0;
        foreach (string slot in Directory.EnumerateDirectories(source))
        {
            if (++slots > MaximumMigrationFiles) throw new IOException("Too many old N2System save directories to migrate.");
            string name = Path.GetFileName(slot);
            if (!IsSlot(name) || IsLink(slot)) continue;
            string targetDirectory = Path.Combine(destination, name);
            string checkpoint = name == "common" ? "val.npf"
                : uint.Parse(name, NumberStyles.None, CultureInfo.InvariantCulture).ToString("D4", CultureInfo.InvariantCulture) + ".sav";
            // Metadata and thumbnails belong to their checkpoint. A local slot
            // wins as a whole instead of acquiring sidecars from an older save.
            if (File.Exists(Path.Combine(targetDirectory, checkpoint))) continue;
            foreach (string file in Directory.EnumerateFiles(slot))
            {
                if (++files > MaximumMigrationFiles) throw new IOException("Too many old N2System save files to migrate.");
                if (IsLink(file)) continue;
                string extension = Path.GetExtension(file);
                if (!extension.Equals(".npf", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".sav", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                if (info.Length > MaximumFileLength || copied > MaximumMigrationLength - info.Length)
                    throw new IOException("The old N2System saves exceed the supported migration size.");
                Directory.CreateDirectory(targetDirectory);
                string target = Path.Combine(targetDirectory, Path.GetFileName(file));
                if (File.Exists(target) || Directory.Exists(target)) continue;
                CopyWithoutReplacing(file, target);
                copied += info.Length;
            }
        }
        // Publish only after every supported file was copied or already existed.
        // The fallback is retained intact for recovery and never imported again.
        using var completed = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        completed.Write(Encoding.UTF8.GetBytes(destination));
        completed.Flush(flushToDisk: true);
    }

    private static void CopyWithoutReplacing(string source, string destination)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); } catch (Exception error) when (IsFileError(error)) { }
        }
    }

    private static bool IsSlot(string name)
        => name == "common" || name.Length is >= 1 and <= 10
            && uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool IsLink(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsFileError(Exception error)
        => error is IOException or UnauthorizedAccessException or SecurityException;
}
