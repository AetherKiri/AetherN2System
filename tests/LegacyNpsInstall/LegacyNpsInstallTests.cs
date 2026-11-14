using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AetherN2System;
using NitroSharp.NsScript.Compiler;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NitroSharp.Tests;

public sealed class LegacyNpsInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-install-test-" + Guid.NewGuid().ToString("N"));
    private readonly Encoding _sjis;
    private readonly Encoding _gbk;

    public LegacyNpsInstallTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _sjis = Encoding.GetEncoding(932);
        _gbk = Encoding.GetEncoding(936);
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SparseMixedByteAnimationResolvesPrefixAndSoundAndBlindMaskPreservesOriginalPhases()
    {
        Write("game_cn.ini", _sjis.GetBytes("Name=fixture\nBegin=start.nps\n"));
        Write("sys_cn.ini", _sjis.GetBytes("width=80\nheight=60\n"));
        foreach (int frame in new[] { 0, 1, 3 }) Write("cg/anim/角色_" + frame.ToString("D3") + ".png", [1]);
        Write("sound/cut.ogg", [1]);
        Write("script/start.nps", Combine(_sjis.GetBytes("<ANIM SRC='anim/"), _gbk.GetBytes("角色"),
            _sjis.GetBytes("_' NUM=8 START=000 END=003 STILL=003 WAIT=0 SE=cut><WIPE EFFECT=Blind TIME=10>")));
        var settings = LegacyNpsInstall.Prepare(_root, Path.Combine(_root, "cache"));
        string source = File.ReadAllText(Path.Combine(settings.ScriptRoot, settings.StartupScript));
        Assert.Contains("\"cg/anim/角色_000.png\", \"cg/anim/角色_001.png\", \"\", \"cg/anim/角色_003.png\"", source);
        Assert.Contains("sound/cut.ogg", source);
        Assert.Contains("WaitFrameAnimation(", source);
        Assert.DoesNotContain("LegacyUnsupported", source);
        using var image = Image.Load<Rgba32>(Path.Combine(settings.ScriptRoot, "masks", "blind.png"));
        // Original 0x1000ADD0: progress2 reveals remainder0, 8 reveals0..1,
        // and42 reveals all7 rows; the script still waits the full TIME.
        foreach (int progress in new[] { 2, 8, 42 })
            for (int row = 0; row < 14; row++)
            {
                Assert.Equal((byte)(7 * (row % 7)), image[5, row].R);
                Assert.Equal(row % 7 <= progress / 7, image[5, row].R <= progress);
            }
        var compilation = new Compilation(settings.ScriptRoot, Encoding.UTF8);
        var emitted = compilation.Emit([compilation.GetSourceModule(settings.StartupScript)], settings.ScriptRoot, "globals");
        Assert.Empty(emitted.Diagnostics);
    }

    [Fact]
    public void DirectionalMasksUseCacheAndHaveOppositeScreenGradients()
    {
        Write("game.ini", _sjis.GetBytes("Name=fixture\nBegin=start.nps\n"));
        Write("sys.ini", _sjis.GetBytes("width=80\nheight=60\n"));
        Write("script/start.nps", _sjis.GetBytes("<WIPE EFFECT=RIGHT TIME=5><WIPE EFFECT=LEFT TIME=5><WIPE EFFECT=DOWN TIME=5><WIPE EFFECT=UP TIME=5>"));
        var settings = LegacyNpsInstall.Prepare(_root, Path.Combine(_root, "cache"));
        foreach (string direction in new[] { "right", "left", "down", "up" })
        {
            string path = Path.Combine(settings.ScriptRoot, "masks", direction + ".png");
            using var image = Image.Load<Rgba32>(path);
            Assert.Equal((80, 60), (image.Width, image.Height));
            Assert.Equal(direction is "right" or "down" ? (byte)0 : (byte)255, image[0, 0].R);
            Assert.Equal(direction is "right" or "down" ? (byte)255 : (byte)0, image[79, 59].R);
            Assert.False(File.Exists(Path.Combine(_root, "masks", direction + ".png")));
        }
        string source = File.ReadAllText(Path.Combine(settings.ScriptRoot, settings.StartupScript));
        Assert.Contains("DrawTransition(", source);
        Assert.DoesNotContain("LegacyUnsupported", source);
        var compilation = new Compilation(settings.ScriptRoot, Encoding.UTF8);
        var emitted = compilation.Emit([compilation.GetSourceModule(settings.StartupScript)], settings.ScriptRoot, "globals");
        Assert.Empty(emitted.Diagnostics);
    }

    [Fact]
    public void PackedPresentationControlsNarrativeAreaFrameAndBustupAlignment()
    {
        WritePak("data.pak", new Dictionary<string, byte[]> {
            ["game.ini"] = _sjis.GetBytes("Name=fixture\nBegin=start.nps\n"),
            ["sys.ini"] = _sjis.GetBytes("width=800\nheight=600\n"),
            ["textbox.ini"] = _sjis.GetBytes("[BOX9]\nBoxLeft=35\nBoxTop=45\nBoxWidth=630\nBoxHeight=420\nMarginLeft=17\nMarginTop=8\nMarginRight=13\nMarginBottom=12\nframename=data\\panel.png\ntransparency=75\n[BOX2]\nColumnarWriting=1\nBoxWidth=630\nBoxHeight=420\n"),
            ["align.ini"] = _sjis.GetBytes("[align0]\nalignname=left\nalignX=275\n"),
            ["data/panel.png"] = [1],
            ["cg/衛士ａ_通常.png"] = [1],
            ["script/start.nps"] = _sjis.GetBytes("<BOX TYPE=9><BUSTUP NAME=衛士Ａ FACE=通常 MODE=on ALIGN=left><WIPE EFFECT=NORMAL>Page<k><BUSTUP NAME=衛士Ａ MODE=off><WIPE EFFECT=NORMAL>") });
        var settings = LegacyNpsInstall.Prepare(_root, Path.Combine(_root, "cache"));
        string source = File.ReadAllText(Path.Combine(settings.ScriptRoot, settings.StartupScript));
        Assert.Contains("CreateTexture(\"legacy_box_frame\", 9999, 35, 45, \"data/panel.png\")", source);
        Assert.Contains("CreateWindow(\"legacy_box\", 10000, 52, 43, 600, 410, false)", source);
        Assert.Contains("cg/衛士ａ_通常.png", source);
        Assert.Contains("_actor0_x = -125", source);
        Assert.DoesNotContain("LegacyUnsupported", source);
        Assert.Contains(settings.Diagnostics, value => value.Contains("unsupported vertical writing"));
        var compilation = new Compilation(settings.ScriptRoot, Encoding.UTF8);
        var emitted = compilation.Emit([compilation.GetSourceModule(settings.StartupScript)], settings.ScriptRoot, "globals");
        Assert.Empty(emitted.Diagnostics);
    }

    [Fact]
    public void MixedByteBustupNameAndFaceResolveTogetherAndOffKeepsIdentity()
    {
        Write("game_cn.ini", _sjis.GetBytes("Name=fixture\nBegin=start.nps\n"));
        Write("sys_cn.ini", _sjis.GetBytes("width=800\nheight=600\n"));
        Write("cg/角色_通常.png", [1]);
        Write("script/start.nps", Combine(_sjis.GetBytes("<BUSTUP NAME='"), _gbk.GetBytes("角色"),
            _sjis.GetBytes("' FACE=通常 MODE=on><WIPE EFFECT=NORMAL><BUSTUP NAME='"), _gbk.GetBytes("角色"),
            _sjis.GetBytes("' MODE=off><WIPE EFFECT=NORMAL>")));
        var settings = LegacyNpsInstall.Prepare(_root, Path.Combine(_root, "cache"));
        string source = File.ReadAllText(Path.Combine(settings.ScriptRoot, settings.StartupScript));
        Assert.Contains("cg/角色_通常.png", source);
        Assert.DoesNotContain("_actor1", source);
        Assert.Contains("_actor0_visible = false", source);
        Assert.DoesNotContain("LegacyUnsupported", source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalizedActorAliasUsesSharedVoiceIdentityAndPrefersDirectImage(bool directImage)
    {
        Write("game_cn.ini", _sjis.GetBytes("Name=fixture\nBegin=translated.nps\n"));
        Write("sys_cn.ini", _sjis.GetBytes("width=80\nheight=60\n"));
        WritePak("original.pak", new Dictionary<string, byte[]> {
            ["script/original.nps"] = _sjis.GetBytes("<VOICE NAME=葉 SRC=chapter/001><VOICE NAME=葉 SRC=chapter/002>"),
            ["voice/chapter/001.ogg"] = [1], ["voice/chapter/002.ogg"] = [1],
            ["cg/葉_通常.png"] = [1] });
        if (directImage) Write("cg/叶_通常.png", [1]);
        // The display command precedes the confirming voice commands. Different
        // script names and command positions cannot serve as correspondence.
        Write("script/translated.nps", Combine(_sjis.GetBytes("<BUSTUP NAME='"), _gbk.GetBytes("叶"),
            _sjis.GetBytes("' FACE=通常 MODE=on><WIPE EFFECT=NORMAL><VOICE NAME='"), _gbk.GetBytes("叶"),
            _sjis.GetBytes("' SRC=chapter/002><VOICE NAME='"), _gbk.GetBytes("叶"),
            _sjis.GetBytes("' SRC=chapter/001><BUSTUP NAME='"), _gbk.GetBytes("叶"),
            _sjis.GetBytes("' MODE=off><WIPE EFFECT=NORMAL>")));
        var settings = LegacyNpsInstall.Prepare(_root, Path.Combine(_root, "cache"));
        string source = File.ReadAllText(Path.Combine(settings.ScriptRoot, settings.StartupScript));
        Assert.Contains(directImage ? "cg/叶_通常.png" : "cg/葉_通常.png", source);
        Assert.DoesNotContain("_actor1", source);
        Assert.Contains("_actor0_visible = false", source);
        Assert.DoesNotContain("LegacyUnsupported", source);
    }

    [Theory]
    [InlineData("original-conflict")]
    [InlineData("translated-conflict")]
    [InlineData("missing-image")]
    [InlineData("missing-original")]
    public void LocalizedActorAliasRequiresUnambiguousVoiceEvidenceAndExistingImage(string failure)
    {
        Write("game_cn.ini", _sjis.GetBytes("Name=fixture\nBegin=translated.nps\n"));
        Write("sys_cn.ini", _sjis.GetBytes("width=80\nheight=60\n"));
        var original = new Dictionary<string, byte[]> {
            ["voice/chapter/001.ogg"] = [1], ["voice/chapter/002.ogg"] = [1] };
        if (failure != "missing-original") original["script/original.nps"] = _sjis.GetBytes(
            "<VOICE NAME=葉 SRC=chapter/001><VOICE NAME=他 SRC=chapter/002>"
            + (failure == "original-conflict" ? "<VOICE NAME=他 SRC=chapter/001>" : ""));
        if (failure != "missing-image") original["cg/葉_通常.png"] = [1];
        WritePak("original.pak", original);
        Write("script/translated.nps", Combine(_sjis.GetBytes("<VOICE NAME='"), _gbk.GetBytes("叶"),
            _sjis.GetBytes("' SRC=chapter/001>"),
            failure == "translated-conflict" ? Combine(_sjis.GetBytes("<VOICE NAME='"), _gbk.GetBytes("叶"), _sjis.GetBytes("' SRC=chapter/002>")) : [],
            _sjis.GetBytes("<BUSTUP NAME='"), _gbk.GetBytes("叶"), _sjis.GetBytes("' FACE=通常 MODE=on>")));
        var settings = LegacyNpsInstall.Prepare(_root, Path.Combine(_root, "cache"));
        string source = File.ReadAllText(Path.Combine(settings.ScriptRoot, settings.StartupScript));
        Assert.Contains("LegacyUnsupported", source);
        Assert.Contains("resource is missing", source);
        Assert.DoesNotContain("cg/葉_通常.png", source);
    }

    [Fact]
    public void MixedPatchKeepsChineseTextAndResolvesLooseMojibakeBeforePackedOriginal()
    {
        Write("game_cn.ini", _gbk.GetBytes("[設定]\r\nName=fixture\r\nTitle=中文标题\r\nBegin=menu.nps 'comment\r\n"));
        Write("sys_cn.ini", _sjis.GetBytes("width=800\nheight=600\nscript=scenario\nimage=images\nbgm=audio\nse=audio\nvoice=voices\nfontSize=24\n"));
        string image = "花の城";
        string mojibake = _gbk.GetString(_sjis.GetBytes(image));
        Write("images/" + mojibake + ".png", [1]);
        Write("images/start.png", [1]);
        WritePak("original.pak", new Dictionary<string, byte[]> {
            ["images/" + image + ".png"] = [2],
            ["scenario/menu.nps"] = _sjis.GetBytes("<UNKNOWN>"),
            ["voices/刃鳴_01/001A.ogg"] = [3] });
        Write("scenario/menu.nps", Combine(_sjis.GetBytes("<コマンド メニュー><BACKGROUND SRC='花の城'><WIPE EFFECT=NORMAL><CHOICE FILE='images/start.png' HREF='"),
            _gbk.GetBytes("中文剧情.nps"), _sjis.GetBytes("'><SELECT>")));
        Write("scenario/中文剧情.nps", Combine(_sjis.GetBytes("<BOX TYPE=9><VOICE SRC='刃鳴_01\\001A' NAME='"),
            _gbk.GetBytes("赤音"), _sjis.GetBytes("'>"), _gbk.GetBytes("中文正文。"), _sjis.GetBytes("<k><UNSUPPORTED>")));

        var settings = LegacyNpsInstall.Prepare(_root, Path.Combine(_root, "cache"));
        Assert.Equal((800, 600, 24), (settings.Width, settings.Height, settings.FontSize));
        Assert.Equal("中文标题", settings.DisplayName);
        Assert.Equal("scenario", settings.PathMappings["script"]);
        Assert.All(settings.Mounts, mount => Assert.Equal("", mount.MountName));
        string menu = File.ReadAllText(Path.Combine(settings.ScriptRoot, settings.StartupScript));
        Assert.Contains("images/" + mojibake + ".png", menu);
        Assert.DoesNotContain("UNKNOWN", menu);
        string story = Directory.EnumerateFiles(settings.ScriptRoot).Select(File.ReadAllText)
            .Single(text => text.Contains("中文正文。"));
        Assert.Contains("voices/刃鳴_01/001A.ogg", story);
        Assert.Contains("LegacyUnsupported", story);
        Assert.Contains("UNSUPPORTED", story);
        Assert.Contains("legacy_entry", menu);
    }

    [Fact]
    public void PackedIniAndScriptSupplyJapaneseInstallWithoutLooseOverrides()
    {
        WritePak("system.pak", new Dictionary<string, byte[]> {
            ["game.ini"] = _sjis.GetBytes("Name=original\nTitle=原作\nBegin=開始.nps\n"),
            ["system.ini"] = _sjis.GetBytes("width=640\nheight=480\nscript=script\nimage=cg\n"),
            ["script/開始.nps"] = _sjis.GetBytes("<BACKGROUND SRC=title><WIPE EFFECT=NORMAL><BOX TYPE=9>日本語本文<k>"),
            ["cg/title.jpg"] = [1] });
        var settings = LegacyNpsInstall.Prepare(_root, Path.Combine(_root, "cache"));
        Assert.Equal((640, 480), (settings.Width, settings.Height));
        Assert.Equal("原作", settings.DisplayName);
        string text = File.ReadAllText(Path.Combine(settings.ScriptRoot, settings.StartupScript));
        Assert.Contains("日本語本文", text);
        Assert.Contains("cg/title.jpg", text);
        Assert.DoesNotContain("LegacyUnsupported", text);
    }

    [Fact]
    public void MissingBranchAssetsRemainExplicitRuntimeFailuresAndCompile()
    {
        Write("game.ini", _sjis.GetBytes("Name=fixture\nBegin=menu.nps\n"));
        Write("sys.ini", _sjis.GetBytes("width=800\nheight=600\n"));
        Write("script/menu.nps", _sjis.GetBytes("<BOX TYPE=9>Visible first page<k><BACKGROUND SRC=missing>"));
        var settings = LegacyNpsInstall.Prepare(_root, Path.Combine(_root, "cache"));
        string source = File.ReadAllText(Path.Combine(settings.ScriptRoot, settings.StartupScript));
        Assert.Contains("Visible first page", source);
        Assert.Contains("LegacyUnsupported", source);
        Assert.Contains("resource is missing", source);
        var compilation = new Compilation(settings.ScriptRoot, Encoding.UTF8);
        var emitted = compilation.Emit([compilation.GetSourceModule(settings.StartupScript)], settings.ScriptRoot, "globals");
        Assert.Empty(emitted.Diagnostics);
    }

    [Fact]
    public void MissingStartupAndTraversalFailBeforeCreatingExecutableCache()
    {
        Write("game.ini", _sjis.GetBytes("Name=fixture\nBegin=../outside.nps\n"));
        Write("sys.ini", _sjis.GetBytes("width=800\nheight=600\n"));
        Assert.Throws<FileNotFoundException>(() => LegacyNpsInstall.Prepare(_root, Path.Combine(_root, "cache")));
    }

    private void Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] Combine(params byte[][] pieces) => pieces.SelectMany(bytes => bytes).ToArray();

    private void WritePak(string name, Dictionary<string, byte[]> files)
    {
        byte[] title = "fixture"u8.ToArray();
        using var index = new MemoryStream();
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var (path, value) in files)
            {
                byte[] encoded = _sjis.GetBytes(path), bytes = (byte[])value.Clone();
                uint key = Hash(encoded);
                writer.Write(encoded.Length); writer.Write(encoded);
                writer.Write((uint)payload.Position ^ key); writer.Write((uint)bytes.Length ^ key);
                writer.Write(key); writer.Write(key); writer.Write((uint)bytes.Length ^ key);
                for (int i = 0; i < Math.Min(16, bytes.Length); i++) bytes[i] ^= (byte)(key >> ((i % 4) * 8));
                payload.Write(bytes);
            }
        }
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionMode.Compress, leaveOpen: true)) zlib.Write(index.ToArray());
        using var archive = new MemoryStream();
        using var header = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true);
        header.Write(3); header.Write(title); header.Write(new byte[256 - title.Length]); header.Write(100);
        header.Write((uint)index.Length ^ Hash(title)); header.Write((uint)files.Count ^ Hash(title));
        header.Write((uint)packed.Length ^ 100); header.Write(packed.ToArray()); header.Write(payload.ToArray());
        Write(name, archive.ToArray());
    }

    private static uint Hash(byte[] bytes)
    {
        uint key = 0;
        foreach (byte value in bytes) key = unchecked(key * 137 + (uint)(sbyte)value);
        return key;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
