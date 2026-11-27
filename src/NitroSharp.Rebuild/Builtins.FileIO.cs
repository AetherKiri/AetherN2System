using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace NitroSharp;

internal sealed partial class Builtins
{
    private string SaveTextDirectory => VM.SystemVariables.SavePath.AsString() is { Length: > 0 } path
        ? Path.GetFullPath(SaveTextFileIO.NativePath(path))
        : Path.Combine(_ctx.Profile.WritableDirectory ?? _ctx.Profile.ContentRoot, "savedata");

    public override string ReadFile(string path)
    {
        string native = SaveTextFileIO.NativePath(path);
        string? saved = SaveTextFileIO.ResolvePath(SaveTextDirectory, native);
        if (saved is not null && File.Exists(saved))
        {
            string text = SaveTextFileIO.Read(saved);
            // The Hanachirasu script formats hours with division, which yields a float in NSS.
            if (_ctx.Profile.NpaScheme == "hanachirasu" && Path.GetFileName(saved) == "tm.npf")
            {
                int separator = text.IndexOf(':');
                if (separator > 0)
                {
                    string hours = text[..separator].Replace("<PRE>0</PRE>", "", StringComparison.Ordinal);
                    if (double.TryParse(hours, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                        && value >= 0 && value <= int.MaxValue)
                        return ((int)value).ToString("D2", CultureInfo.InvariantCulture) + text[separator..];
                }
            }
            return text;
        }
        // Relative authored files may live in an archive rather than beside the
        // executable. Savedata remains a normal writable directory on mobile.
        if (Path.IsPathRooted(native)) return "";
        using Stream? stream = _ctx.Content.TryOpenStream(native);
        if (stream is null) return "";
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public override void WriteFile(string path, string text, bool append = false)
    {
        string? saved = SaveTextFileIO.ResolvePath(SaveTextDirectory, path);
        if (saved is null)
        {
            _log.Warn($"WriteFile path is outside the savedata directory: {path}");
            return;
        }
        SaveTextFileIO.Write(saved, text, append);
    }
}

internal static class SaveTextFileIO
{
    internal static string NativePath(string path) => path.Replace('\\', Path.DirectorySeparatorChar)
        .Replace('/', Path.DirectorySeparatorChar);

    internal static string? ResolvePath(string saveDirectory, string path)
    {
        string root = Path.GetFullPath(NativePath(saveDirectory));
        string full = Path.GetFullPath(Path.Combine(root, NativePath(path)));
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(prefix, comparison) ? full : null;
    }

    internal static string Read(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";

    internal static bool Exists(string saveDirectory, string path)
        => ResolvePath(saveDirectory, path) is { } saved && File.Exists(saved);

    internal static void Write(string path, string text, bool append)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (append) File.AppendAllText(path, text, new UTF8Encoding(false));
        else File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
