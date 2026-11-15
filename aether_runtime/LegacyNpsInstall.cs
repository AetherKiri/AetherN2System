using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NitroSharp;
using NitroSharp.Content;
using NitroSharp.LegacyNps;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AetherN2System;

internal sealed record LegacyNpsInstallSettings(string StartupScript, string ScriptRoot,
    string ContentRoot, int Width, int Height, string ProductName, string DisplayName,
    int FontSize, IReadOnlyList<MountPoint> Mounts, IReadOnlyDictionary<string, string> PathMappings,
    IReadOnlyList<string> Diagnostics);

// Original files stay untouched. The cache contains only generated UTF-8 NSS;
// images and audio continue to use the ordinary loose-file/PAK content loader.
internal static class LegacyNpsInstall
{
    public static LegacyNpsInstallSettings Prepare(string root, string writable)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var install = new Install(Path.GetFullPath(root));
        return install.Prepare(Path.GetFullPath(writable));
    }

    private sealed class Install : IDisposable
    {
        private readonly string _root;
        private readonly Encoding _sjis = Encoding.GetEncoding(932);
        private readonly Encoding _gbk = Encoding.GetEncoding(936);
        private readonly Encoding _strictSjis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        private readonly Encoding _strictGbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        private readonly Dictionary<string, Resource> _loose = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Resource> _packed = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<LegacyPakReader> _archives = [];
        private readonly List<MountPoint> _mounts = [];
        private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _modules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<Resource> _pending = new();
        private readonly List<string> _diagnostics = [];
        private readonly Dictionary<string, string> _bustupNames = new(StringComparer.Ordinal);
        private Dictionary<string, string?>? _originalVoiceActors;
        private bool _translatedVoiceAliasesLoaded;
        private readonly Dictionary<string, string?> _voiceAliases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _resolvedVoiceActors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _wipeMasks = new(StringComparer.OrdinalIgnoreCase);
        private string _cache = "";
        private const string AttributePattern = "([A-Za-z_][A-Za-z_0-9]*)[ \\t]*=[ \\t]*(?:\"([^\"]*)\"|'([^']*)'|([^ \\t\\r\\n>]+))";
        private bool _chinese;

        public Install(string root)
        {
            _root = root;
            try
            {
                foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (_loose.Count >= 100000) throw new InvalidDataException("Legacy install contains too many files.");
                    _loose.TryAdd(relative, new Resource(relative, path, null));
                }
                foreach (string path in Directory.EnumerateFiles(root)
                    .Where(path => Path.GetExtension(path).Equals(".pak", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(path => Path.GetFileNameWithoutExtension(path).Contains("patch", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var archive = new LegacyPakReader(File.OpenRead(path), _sjis);
                    _archives.Add(archive);
                    _mounts.Add(new MountPoint { ArchiveName = Path.GetFileName(path), MountName = "", FileNamesIni = null });
                    foreach (var entry in archive.Entries)
                        _packed.TryAdd(entry.Name, new Resource(entry.Name, null, archive));
                }
            }
            catch { Dispose(); throw; }
        }

        public LegacyNpsInstallSettings Prepare(string writable)
        {
            Resource gameIni = Find(["game_cn.ini", "game.ini"])
                ?? throw new FileNotFoundException("Legacy NPS installation has no game[_cn].ini.");
            _chinese = Path.GetFileName(gameIni.Name).Equals("game_cn.ini", StringComparison.OrdinalIgnoreCase);
            Resource systemIni = Find(_chinese ? ["sys_cn.ini", "sys.ini", "system.ini"] : ["sys.ini", "system.ini", "sys_cn.ini"])
                ?? throw new FileNotFoundException("Legacy NPS installation has no sys[_cn].ini or system.ini.");
            Dictionary<string, byte[]> game = ReadIni(gameIni), system = ReadIni(systemIni);
            string Value(Dictionary<string, byte[]> ini, string key, string fallback, bool text = false)
                => ini.TryGetValue(key, out byte[]? bytes)
                    ? (text && _chinese ? _gbk : _sjis).GetString(bytes).Trim() : fallback;
            int Number(string key, int fallback, int maximum)
                => int.TryParse(Value(system, key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                    && number is > 0 && number <= maximum ? number : fallback;
            foreach (var (key, fallback) in new[] { ("script", "script"), ("image", "cg"), ("data", "data"),
                ("bgm", "sound"), ("se", "sound"), ("voice", "voice"), ("movie", "mpeg") })
                _paths[key] = Normalize(Value(system, key, fallback));
            int width = Number("width", 800, 16384), height = Number("height", 600, 16384);
            int Volume(string key) => int.TryParse(Value(system, key, "100"), out int value) ? Math.Clamp(value, 0, 100) : 100;
            LegacyNpsPresentation presentation = ReadPresentation(width, height) with
            { BgmVolume = Volume("bgm_volume"), SeVolume = Volume("se_volume"), VoiceVolume = Volume("voice_volume") };
            byte[] beginBytes = game.GetValueOrDefault("Begin")
                ?? throw new InvalidDataException("Legacy game.ini has no Begin script.");
            Resource startup = FindScript(Candidates(beginBytes))
                ?? throw new FileNotFoundException("Legacy Begin NPS script is missing: " + _sjis.GetString(beginBytes));
            string startupModule = Register(startup);
            string cache = Path.Combine(writable, "legacy-nps-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cache);
            _cache = cache;
            presentation = presentation with { TransitionMasks = WriteTransitionMasks(cache, width, height) };
            long totalSource = 0;
            while (_pending.TryDequeue(out Resource? resource))
            {
                byte[] bytes = Read(resource);
                totalSource = checked(totalSource + bytes.Length);
                if (totalSource > 128L * 1024 * 1024) throw new InvalidDataException("Legacy NPS scripts exceed 128 MiB.");
                string translated;
                try
                {
                    bool chineseText = _chinese && (resource.LoosePath is not null
                        || Path.GetFileName(resource.Name).StartsWith("cn_", StringComparison.OrdinalIgnoreCase));
                    var result = LegacyNpsTranslator.Translate(Decode(bytes, chineseText), resource.Name,
                        ResolveModule, ResolveAsset, width, height, presentation);
                    translated = result.Source;
                    _diagnostics.AddRange(result.Diagnostics);
                }
                catch (FormatException error)
                {
                    if (resource.Name.Equals(startup.Name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Legacy startup script cannot be translated: " + error.Message, error);
                    // A later branch remains an explicit runtime failure. It must not
                    // prevent an unrelated title branch from compiling and playing.
                    _diagnostics.Add(resource.Name + ": " + error.Message);
                    translated = "chapter main { call_scene @->legacy_entry; }\nscene legacy_entry { LegacyUnsupported(\""
                        + Quote(resource.Name) + "\", 1, \"" + Quote(error.Message) + "\"); }\n";
                }
                File.WriteAllText(Path.Combine(cache, _modules[resource.Name]), translated, new UTF8Encoding(false));
            }
            return new LegacyNpsInstallSettings(startupModule, cache, _root, width, height,
                Value(game, "Name", Path.GetFileName(_root)), Value(game, "Title", Value(game, "Name", Path.GetFileName(_root)), text: true),
                Number("fontSize", 22, 200), _mounts.ToArray(), new Dictionary<string, string>(_paths), _diagnostics.ToArray());
        }

        private string ResolveModule(string path)
        {
            Resource resource = FindScript(Expand(path))
                ?? throw new FormatException("NPS script target is missing: " + path);
            return Register(resource);
        }

        private string Register(Resource resource)
        {
            if (_modules.TryGetValue(resource.Name, out string? module)) return module;
            if (_modules.Count >= 4096) throw new InvalidDataException("Too many referenced NPS modules.");
            module = "legacy_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource.Name.ToLowerInvariant())))[..24] + ".nss";
            _modules.Add(resource.Name, module);
            _pending.Enqueue(resource);
            return module;
        }

        private string ResolveAsset(string kind, string path)
        {
            // A missing authored voice is silent in the original player. Keep
            // malformed paths distinct so they cannot acquire that fallback.
            if (kind is "voice" or "wipe" or "glass") Normalize(path);
            Resource? resource = FindAsset(kind == "wipe" ? "data" : kind == "glass" ? "cg" : kind, Expand(path));
            if (resource is null)
            {
                if (kind is "voice" or "wipe" or "glass") return "";
                throw new FormatException("NPS resource is missing: " + kind + "/" + path);
            }
            if (kind != "wipe") return resource.Name;
            if (_wipeMasks.TryGetValue(resource.Name, out string? cached)) return cached;
            // The original PNG reader retains palette indices. Their displayed
            // palette colors are irrelevant to the wipe's threshold bytes.
            using Image<Rgba32> mask = ReadIndexedWipeMask(Read(resource));
            cached = Path.Combine(_cache, "masks", "authored-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource.Name)))[..24] + ".png");
            mask.SaveAsPng(cached);
            _wipeMasks.Add(resource.Name, cached);
            return cached;
        }

        private static Image<Rgba32> ReadIndexedWipeMask(byte[] png)
        {
            if (!png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new FormatException("WIPE mask must be an indexed PNG.");
            int width = 0, height = 0, depth = 0;
            using var compressed = new MemoryStream();
            for (int cursor = 8; cursor <= png.Length - 12;)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(cursor));
                if (length > png.Length - cursor - 12) throw new FormatException("Truncated WIPE PNG chunk.");
                ReadOnlySpan<byte> type = png.AsSpan(cursor + 4, 4), data = png.AsSpan(cursor + 8, (int)length);
                if (type.SequenceEqual("IHDR"u8))
                {
                    if (data.Length != 13 || width != 0) throw new FormatException("Invalid WIPE PNG header.");
                    uint w = BinaryPrimitives.ReadUInt32BigEndian(data), h = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    depth = data[8];
                    if (w is 0 or > 16384 || h is 0 or > 16384 || (long)w * h > 32L * 1024 * 1024
                        || depth is not (1 or 2 or 4 or 8) || data[9] != 3 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                        throw new FormatException("WIPE mask requires a bounded, non-interlaced indexed PNG.");
                    width = (int)w; height = (int)h;
                }
                else if (type.SequenceEqual("IDAT"u8)) compressed.Write(data);
                cursor += (int)length + 12;
                if (type.SequenceEqual("IEND"u8)) break;
            }
            if (width == 0 || compressed.Length == 0) throw new FormatException("WIPE PNG has no pixel data.");
            compressed.Position = 0;
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            int stride = (width * depth + 7) / 8;
            byte[] previous = new byte[stride], row = new byte[stride];
            var image = new Image<Rgba32>(width, height);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    int filter = zlib.ReadByte();
                    if (filter is < 0 or > 4) throw new FormatException("Invalid WIPE PNG row filter.");
                    zlib.ReadExactly(row);
                    for (int i = 0; i < stride; i++)
                    {
                        int left = i > 0 ? row[i - 1] : 0, above = previous[i], upperLeft = i > 0 ? previous[i - 1] : 0;
                        int prediction = filter switch
                        {
                            0 => 0, 1 => left, 2 => above, 3 => (left + above) / 2,
                            _ => Paeth(left, above, upperLeft)
                        };
                        row[i] = unchecked((byte)(row[i] + prediction));
                    }
                    for (int x = 0; x < width; x++)
                    {
                        int bit = x * depth;
                        byte value = (byte)((row[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1));
                        image[x, y] = new(value, value, value, 255);
                    }
                    (row, previous) = (previous, row);
                }
                if (zlib.ReadByte() != -1) throw new FormatException("WIPE PNG has excess pixel data.");
                return image;
            }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException)
            {
                image.Dispose();
                throw new FormatException("Invalid compressed WIPE PNG pixels.", error);
            }
            catch { image.Dispose(); throw; }

            static int Paeth(int left, int above, int upperLeft)
            {
                int prediction = left + above - upperLeft;
                int a = Math.Abs(prediction - left), b = Math.Abs(prediction - above), c = Math.Abs(prediction - upperLeft);
                return a <= b && a <= c ? left : b <= c ? above : upperLeft;
            }
        }

        private Resource? FindScript(IEnumerable<string> candidates)
            => Find(candidates.SelectMany(path => new[] { path, _paths.GetValueOrDefault("script", "script") + "/" + path })
                .Select(path => Path.HasExtension(path) ? path : path + ".nps"));

        private Resource? FindAsset(string kind, IEnumerable<string> candidates)
        {
            string[] roots = kind switch
            {
                "cg" => [_paths["image"], _paths["data"]],
                "sound" => [_paths["bgm"], _paths["se"], _paths["data"]],
                "voice" => [_paths["voice"], _paths["data"]],
                "movie" => [_paths["movie"], _paths["data"]],
                _ => [kind, _paths.GetValueOrDefault("data", "data")]
            };
            string[] extensions = kind is "sound" or "voice" ? [".ogg", ".wav"]
                : kind == "movie" ? [".mpg", ".mpeg", ".avi"] : [".png", ".jpg", ".bmp"];
            return Find(candidates.SelectMany(path => new[] { path }.Concat(roots.Select(root => root + "/" + path)))
                .SelectMany(path => Path.HasExtension(path) ? [path] : extensions.Select(extension => path + extension)));
        }

        private Resource? Find(IEnumerable<string> paths)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
                try { names.Add(Normalize(path)); } catch (FormatException) { }
            string[] candidates = names.ToArray();
            // Try every valid spelling against loose files first: a translated loose
            // patch must win over its original Japanese spelling in a PAK.
            foreach (string path in candidates) if (_loose.TryGetValue(path, out Resource? resource)) return resource;
            foreach (string path in candidates) if (_packed.TryGetValue(path, out Resource? resource)) return resource;
            return null;
        }

        private IEnumerable<string> Candidates(byte[] bytes)
        {
            foreach (Encoding encoding in new[] { _strictSjis, _strictGbk, new UTF8Encoding(false, true) })
            {
                string? decoded = null;
                try { decoded = encoding.GetString(bytes); } catch (DecoderFallbackException) { }
                if (decoded is not null)
                    foreach (string candidate in Expand(decoded)) yield return candidate;
            }
        }

        private IEnumerable<string> Expand(string value)
        {
            yield return value;
            // Some patches were unpacked with the wrong code page. Only accept a
            // repaired spelling when the real loose file or archive index has it.
            foreach (var (encode, decode) in new[] { (_strictSjis, _strictGbk), (_strictGbk, _strictSjis) })
            {
                string? repaired = null;
                try { repaired = decode.GetString(encode.GetBytes(value)); }
                catch (EncoderFallbackException) { }
                catch (DecoderFallbackException) { }
                if (repaired is not null) yield return repaired;
            }
        }

        private string Decode(byte[] bytes, bool chineseText)
        {
            ReadVoiceAliases(bytes, chineseText);
            // Latin-1 is an exact byte carrier. Split tags before applying either
            // multibyte decoder so a failed SJIS conversion never destroys GBK text.
            string raw = Encoding.Latin1.GetString(bytes);
            var output = new StringBuilder(raw.Length);
            int position = 0;
            while (position < raw.Length)
            {
                if (raw.AsSpan(position).StartsWith("//"))
                {
                    int end = raw.IndexOf('\n', position);
                    if (end < 0) end = raw.Length;
                    output.Append(_sjis.GetString(bytes.AsSpan(position, end - position)));
                    position = end;
                    continue;
                }
                if (raw[position] != '<')
                {
                    int end = position + 1;
                    while (end < raw.Length && raw[end] != '<' && !raw.AsSpan(end).StartsWith("//")) end++;
                    output.Append((chineseText ? _gbk : _sjis).GetString(bytes.AsSpan(position, end - position)));
                    position = end;
                    continue;
                }
                int tagEnd = position + 1;
                char quote = '\0';
                while (tagEnd < raw.Length)
                {
                    char c = raw[tagEnd++];
                    if (quote != '\0') { if (c == quote) quote = '\0'; }
                    else if (c is '\'' or '"') quote = c;
                    else if (c == '>') break;
                }
                if (tagEnd == raw.Length && raw[tagEnd - 1] != '>') throw new FormatException("Unterminated NPS tag.");
                string tag = raw[position..tagEnd];
                string decodedTag = _sjis.GetString(bytes.AsSpan(position, tagEnd - position));
                if (decodedTag.StartsWith("<コマンド", StringComparison.Ordinal) || decodedTag.StartsWith("<コメント", StringComparison.Ordinal)
                    || decodedTag.StartsWith("<!", StringComparison.Ordinal))
                    output.Append(decodedTag);
                else output.Append(DecodeTag(tag, chineseText));
                position = tagEnd;
            }
            return output.ToString();
        }

        private string DecodeTag(string tag, bool chineseText)
        {
            Match commandMatch = Regex.Match(tag, "^<[ \\t\\r\\n]*([^ \\t\\r\\n>]+)");
            string command = commandMatch.Groups[1].Value.ToUpperInvariant();
            const string attributes = AttributePattern;
            string? actorName = null, actorFace = null;
            if (command == "BUSTUP")
            {
                var values = Regex.Matches(tag, attributes).Cast<Match>()
                    .ToDictionary(match => match.Groups[1].Value, RawValue, StringComparer.OrdinalIgnoreCase);
                if (values.TryGetValue("NAME", out string? rawName))
                {
                    _bustupNames.TryGetValue(rawName, out actorName);
                    if (actorName is null) _resolvedVoiceActors.TryGetValue(rawName, out actorName);
                    if (values.TryGetValue("FACE", out string? rawFace))
                    {
                        string[] faces = Candidates(Encoding.Latin1.GetBytes(rawFace)).Distinct().ToArray();
                        Resource? image = FindAsset("cg", Candidates(Encoding.Latin1.GetBytes(rawName))
                            .SelectMany(name => faces.Select(face => name + "_" + face)));
                        bool direct = image is not null;
                        if (image is null && _voiceAliases.GetValueOrDefault(rawName) is { } alias)
                            image = FindAsset("cg", faces.Select(face => alias + "_" + face));
                        if (image is not null)
                        {
                            string stem = Path.GetFileNameWithoutExtension(image.Name);
                            actorFace = faces.FirstOrDefault(face => stem.EndsWith("_" + face, StringComparison.OrdinalIgnoreCase));
                            if (actorFace is not null)
                            {
                                actorName = stem[..^(actorFace.Length + 1)];
                                if (direct) _bustupNames[rawName] = actorName;
                                else _resolvedVoiceActors[rawName] = actorName;
                            }
                        }
                    }
                }
            }
            return Regex.Replace(tag, attributes, match =>
            {
                string key = match.Groups[1].Value.ToUpperInvariant();
                string rawValue = RawValue(match);
                byte[] bytes = Encoding.Latin1.GetBytes(rawValue);
                string value = _sjis.GetString(bytes);
                if (command == "VOICE" && key == "NAME") value = (chineseText ? _gbk : _sjis).GetString(bytes);
                else if (command == "CHOICE" && key == "TEXT") value = (chineseText ? _gbk : _sjis).GetString(bytes);
                else if (command == "BUSTUP" && key == "NAME") value = actorName ?? value;
                else if (command == "BUSTUP" && key == "FACE") value = actorFace ?? value;
                else if (command == "WIPE" && key == "EFFECT")
                {
                    if (value.ToUpperInvariant() is not ("NORMAL" or "FADE" or "WHITEOUT" or "BLACKOUT" or "BLIND"
                        or "SPIRAL" or "RASTER" or "OVER" or "UNDER" or "NOIZE" or "SLASH" or "SLASH2" or "TINY" or "TRANSFORM" or "DELAYFADE"))
                        value = FindAsset("data", Candidates(bytes))?.Name ?? value;
                }
                else if (key == "HREF" && !value.StartsWith('#')) value = FindScript(Candidates(bytes))?.Name ?? value;
                else if (key == "FILE" && command == "CHOICE")
                    value = string.Join(';', rawValue.Split(';').Select(item =>
                        FindAsset("cg", Candidates(Encoding.Latin1.GetBytes(item)))?.Name ?? _sjis.GetString(Encoding.Latin1.GetBytes(item))));
                else if (key == "SRC" && command is ("ANIM" or "BGANIM"))
                {
                    Match first = Regex.Matches(tag, attributes).Cast<Match>()
                        .FirstOrDefault(item => item.Groups[1].Value.Equals("START", StringComparison.OrdinalIgnoreCase)) ?? Match.Empty;
                    int frame = first.Success && int.TryParse(RawValue(first), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
                    Resource? image = FindAsset("cg", Candidates(bytes).Select(prefix => prefix + frame.ToString("D3", CultureInfo.InvariantCulture)));
                    if (image is not null)
                    {
                        string suffix = frame.ToString("D3", CultureInfo.InvariantCulture);
                        value = image.Name[..^(Path.GetExtension(image.Name).Length + suffix.Length)];
                    }
                }
                else if (key is "SRC" or "ONSE" or "PUSHSE" || key == "SE" && command is ("ANIM" or "BGANIM"))
                {
                    string kind = command == "VOICE" ? "voice" : command == "MOVIE" ? "movie"
                        : command is "BGM" or "SE" || key is "ONSE" or "PUSHSE" or "SE" ? "sound" : "cg";
                    if (value.Length > 0 && !value.StartsWith('#')) value = FindAsset(kind, Candidates(bytes))?.Name ?? value;
                }
                char delimiter = value.Contains('"') ? '\'' : '"';
                if (value.Contains(delimiter)) throw new FormatException("NPS attribute contains both quote delimiters.");
                return match.Groups[1].Value + "=" + delimiter + value + delimiter;
            });
        }

        private static string RawValue(Match match) => match.Groups[2].Success ? match.Groups[2].Value
            : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;

        private static IEnumerable<(string Source, string Name)> VoiceActors(byte[] bytes)
        {
            // Preserve mixed-code-page NAME bytes and skip line comments and editor
            // annotations. Audio paths are the authored identity shared by patches.
            foreach (Match tag in Regex.Matches(Encoding.Latin1.GetString(bytes), "//[^\\r\\n]*|<(?:\"[^\"]*\"|'[^']*'|[^'\">])*>"))
            {
                if (!Regex.IsMatch(tag.Value, "^<[ \\t\\r\\n]*VOICE(?=[ \\t\\r\\n>])", RegexOptions.IgnoreCase)) continue;
                var values = Regex.Matches(tag.Value, AttributePattern).Cast<Match>()
                    .GroupBy(match => match.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => RawValue(group.First()), StringComparer.OrdinalIgnoreCase);
                if (values.TryGetValue("SRC", out string? source) && source.Length > 0
                    && values.TryGetValue("NAME", out string? name) && name.Length > 0)
                    yield return (source, name);
            }
        }

        private void ReadVoiceAliases(byte[] bytes, bool chineseText)
        {
            _resolvedVoiceActors.Clear();
            if (!chineseText) return;
            if (_originalVoiceActors is null)
            {
                _originalVoiceActors = new(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (Resource script in _packed.Values.Where(resource => Path.GetExtension(resource.Name).Equals(".nps", StringComparison.OrdinalIgnoreCase)))
                {
                    byte[] original = Read(script);
                    total = checked(total + original.Length);
                    if (total > 128L * 1024 * 1024) throw new InvalidDataException("Original NPS scripts exceed 128 MiB.");
                    foreach (var (source, name) in VoiceActors(original))
                    {
                        string actor;
                        try { actor = _strictSjis.GetString(Encoding.Latin1.GetBytes(name)); }
                        catch (DecoderFallbackException) { continue; }
                        if (FindAsset("voice", Candidates(Encoding.Latin1.GetBytes(source))) is not { } voice) continue;
                        if (!_originalVoiceActors.TryAdd(voice.Name, actor) && _originalVoiceActors[voice.Name] != actor)
                            _originalVoiceActors[voice.Name] = null;
                    }
                }
            }
            if (_translatedVoiceAliasesLoaded) { AddAliases(bytes); return; }
            _translatedVoiceAliasesLoaded = true;
            long translatedTotal = 0;
            foreach (Resource script in _loose.Values.Where(resource => Path.GetExtension(resource.Name).Equals(".nps", StringComparison.OrdinalIgnoreCase)))
            {
                byte[] translated = Read(script);
                translatedTotal = checked(translatedTotal + translated.Length);
                if (translatedTotal > 128L * 1024 * 1024) throw new InvalidDataException("Translated NPS scripts exceed 128 MiB.");
                AddAliases(translated);
            }
            // A translated script can also live in a patch archive.
            AddAliases(bytes);

            void AddAliases(byte[] translated)
            {
                foreach (var (source, name) in VoiceActors(translated))
                {
                    try { _strictGbk.GetString(Encoding.Latin1.GetBytes(name)); }
                    catch (DecoderFallbackException) { continue; }
                    if (FindAsset("voice", Candidates(Encoding.Latin1.GetBytes(source))) is not { } voice
                        || !_originalVoiceActors.TryGetValue(voice.Name, out string? actor)) continue;
                    // Conflicting original speakers or translated names stay unresolved;
                    // no ordering, spelling distance, or language conversion is inferred.
                    if (!_voiceAliases.TryAdd(name, actor) && _voiceAliases[name] != actor) _voiceAliases[name] = null;
                }
            }
        }

        private LegacyNpsPresentation ReadPresentation(int width, int height)
        {
            var boxes = new Dictionary<int, LegacyNpsTextBox>
                { [9] = new(20, height - 180, width - 40, 170) };
            var aligns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                { ["center"] = width / 2, ["left"] = width * 3 / 10, ["right"] = width * 7 / 10 };
            if (Find(["align.ini", _paths["data"] + "/align.ini"]) is { } alignIni)
                foreach (var section in ReadIniSections(alignIni).Values)
                    if (section.TryGetValue("alignname", out byte[]? name)
                        && section.TryGetValue("alignX", out byte[]? coordinate)
                        && int.TryParse(_sjis.GetString(coordinate), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x))
                        aligns[_sjis.GetString(name)] = x;
            if (Find(["textbox.ini", _paths["data"] + "/textbox.ini"]) is { } textIni)
                foreach (var (name, values) in ReadIniSections(textIni))
                {
                    if (!name.StartsWith("BOX", StringComparison.OrdinalIgnoreCase) || !int.TryParse(name[3..], out int type)) continue;
                    int Number(string key, int fallback = 0) => values.TryGetValue(key, out byte[]? bytes)
                        && int.TryParse(_sjis.GetString(bytes), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
                    int boxWidth = Number("BoxWidth"), boxHeight = Number("BoxHeight");
                    int left = Number("MarginLeft"), top = Number("MarginTop"), right = Number("MarginRight"), bottom = Number("MarginBottom");
                    if (boxWidth <= left + right || boxHeight <= top + bottom || boxWidth > 16384 || boxHeight > 16384
                        || Math.Min(Math.Min(left, right), Math.Min(top, bottom)) < 0)
                    {
                        boxes.Remove(type);
                        _diagnostics.Add(textIni.Name + ": " + name + " has an invalid text area.");
                        continue;
                    }
                    string? frame = null;
                    if (values.TryGetValue("framename", out byte[]? frameBytes) && frameBytes.Length > 0)
                    {
                        frame = FindAsset("data", Candidates(frameBytes))?.Name ?? _sjis.GetString(frameBytes).Replace('\\', '/');
                        if (Find([frame]) is null)
                        {
                            boxes.Remove(type);
                            _diagnostics.Add(textIni.Name + ": missing frame resource " + frame + ".");
                            continue;
                        }
                    }
                    boxes[type] = new(Number("BoxLeft"), Number("BoxTop"), boxWidth, boxHeight,
                        left, top, right, bottom, frame, Math.Clamp(Number("transparency", 100), 0, 100),
                        values.TryGetValue("ColumnarWriting", out byte[]? columnar) && columnar.Length > 0 && _sjis.GetString(columnar) != "0");
                }
            var choices = new Dictionary<int, LegacyNpsChoiceBox>();
            if (Find(["choice.ini", _paths["data"] + "/choice.ini"]) is { } choiceIni)
                foreach (var (name, values) in ReadIniSections(choiceIni))
                {
                    if (!name.StartsWith("BOX", StringComparison.OrdinalIgnoreCase) || !int.TryParse(name[3..], out int slot) || slot is < 0 or > 9) continue;
                    int Number(string key, int fallback = 0) => values.TryGetValue(key, out byte[]? bytes)
                        && int.TryParse(_sjis.GetString(bytes), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
                    string? Image(string key) => values.TryGetValue(key, out byte[]? bytes) && bytes.Length > 0
                        ? FindAsset("cg", Candidates(bytes))?.Name ?? throw new FormatException("CHOICE frame resource is missing: " + _sjis.GetString(bytes)) : null;
                    int w = Number("BoxWidth"), h = Number("BoxHeight");
                    if (w is <= 0 or > 16384 || h is <= 0 or > 16384) continue;
                    choices[slot] = new(Number("BoxLeft"), Number("BoxTop"), w, h, Number("MarginTop"),
                        Number("frameleft"), Number("frametop"), Image("framename"), Image("onFramename"), Image("pushFramename"), Math.Clamp(Number("transparency", 50), 0, 100));
                }
            return new(boxes, aligns, ChoiceBoxes: choices);
        }

        private static IReadOnlyDictionary<string, string> WriteTransitionMasks(string cache, int width, int height)
        {
            var masks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string directory = Path.Combine(cache, "masks");
            Directory.CreateDirectory(directory);
            // BLIND is procedural in the original. Other effect names are authored
            // image masks under the data root and must resolve from the install.
            foreach (string effect in new[] { "BLIND" })
            {
                using var image = new Image<Rgba32>(width, height);
                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        Span<Rgba32> row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            // Original system.dll 0x1000ADD0 copies rows where
                            // y%7 <= progress/7. Its 0..255 progress therefore
                            // fully reveals by 42 but still waits the full TIME.
                            byte value = (byte)(7 * (y % 7));
                            row[x] = new Rgba32(value, value, value, 255);
                        }
                    }
                });
                string path = Path.Combine(directory, effect.ToLowerInvariant() + ".png");
                image.SaveAsPng(path);
                masks.Add(effect, path);
            }
            return masks;
        }

        private static Dictionary<string, Dictionary<string, byte[]>> ReadIniSections(Resource resource)
        {
            var sections = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, byte[]>? section = null;
            foreach (string raw in Encoding.Latin1.GetString(Read(resource)).Split('\n'))
            {
                string line = raw.Trim(' ', '\t', '\r');
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = new(StringComparer.OrdinalIgnoreCase);
                    sections[line[1..^1].Trim()] = section;
                    continue;
                }
                if (section is null || line.Length == 0 || line[0] is '\'' or ';' || line.StartsWith("//")) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string value = line[(equals + 1)..];
                int comment = value.IndexOf('\'');
                if (comment >= 0) value = value[..comment];
                section[line[..equals].Trim()] = Encoding.Latin1.GetBytes(value.Trim(' ', '\t', '\r', '"'));
            }
            return sections;
        }

        private static Dictionary<string, byte[]> ReadIni(Resource resource)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in Encoding.Latin1.GetString(Read(resource)).Split('\n'))
            {
                string line = raw.Trim(' ', '\t', '\r');
                if (line.Length == 0 || line[0] is '[' or '\'' or ';' || line.StartsWith("//")) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string value = line[(equals + 1)..];
                int comment = value.IndexOf('\'');
                if (comment >= 0) value = value[..comment];
                result[line[..equals].Trim()] = Encoding.Latin1.GetBytes(value.Trim(' ', '\t', '\r', '"'));
            }
            return result;
        }

        private static byte[] Read(Resource resource)
        {
            using Stream stream = resource.LoosePath is { } path ? File.OpenRead(path) : resource.Archive!.OpenStream(resource.Name);
            if (stream.Length > 16L * 1024 * 1024) throw new InvalidDataException("Legacy script or INI exceeds 16 MiB.");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }

        private static string Normalize(string path)
        {
            path = path.Replace('\\', '/');
            foreach (string part in path.Split('/'))
                if (part is "" or "." or ".." || part.Contains(':') || part.Contains('\0'))
                    throw new FormatException("Unsafe legacy resource path: " + path);
            return path;
        }

        private static string Quote(string value) => value.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ').Replace('\\', '/');
        public void Dispose() { foreach (LegacyPakReader archive in _archives) archive.Dispose(); }
        private sealed record Resource(string Name, string? LoosePath, LegacyPakReader? Archive);
    }
}
