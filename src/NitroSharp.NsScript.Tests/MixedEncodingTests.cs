using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class MixedEncodingTests
{
    private static readonly Encoding Structure;
    private static readonly Encoding Dialogue;
    private static readonly SourceTextDecodingOptions Options;

    static MixedEncodingTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Structure = Encoding.GetEncoding("shift-jis");
        Dialogue = Encoding.GetEncoding("gbk");
        Options = new(Dialogue, new HashSet<string>(StringComparer.Ordinal) { "TITLE_NOW" });
    }

    [Fact]
    public void MixedBytesPreserveStructureAndDecodeOnlyDisplayText()
    {
        string braceTrail = Dialogue.GetString(new byte[] { 0x81, 0x7b });
        var parts = new (string text, Encoding encoding)[]
        {
            ("#include \"開始.nss\"\r\nchapter main {\r\n$TITLE_NOW = \"", Structure),
            ("序章 中文标题", Dialogue),
            ("\"; $path = \"画像/日本語.png\";\r\n<PRE @箱>\r\n[一]\r\n//日本語コメント\r\n<voice name=\"村正\" class=\"村正さん\" src=\"声/日本語\">\r\n", Structure),
            ("欢迎" + braceTrail + "<ruby TEXT=\"", Dialogue),
            ("钢铁", Dialogue),
            ("\">", Structure),
            ("凶刃", Dialogue),
            ("</ruby>{ if (true) { $path=\"画像/日本語.png\"; CreateChoice(\"日本語\"); } /* } */ }", Structure),
            ("正文。", Dialogue),
            ("\r\n</PRE>\r\n}\r\n", Structure)
        };
        byte[] bytes = Combine(parts);
        using var stream = new MemoryStream(bytes);
        SourceText text = SourceText.From(stream, default, Structure, Options);
        Assert.Equal(string.Concat(parts.Select(part => part.text)), text.Source);
        Assert.Equal(11, text.LineCount);
    }

    [Fact]
    public void LocalizedFontFamiliesKeepTheirClosingQuotesAndSurroundingCode()
    {
        var parts = new (string text, Encoding encoding)[]
        {
            ("chapter main {\nSetFont(\"", Structure),
            ("@黑体", Dialogue),
            ("\", 20, #FFFFFF, #222222, 500, RIGHTDOWN);\nsetfont ( '", Structure),
            ("黑体", Dialogue),
            ("', 20, #FFFFFF, #222222, 500, RIGHTDOWN);\n$path=\"画像/日本語.png\";\n// SetFont(\"日本語\", 20);\nCustomSetFont(\"日本語\");\n}\n", Structure)
        };
        using var stream = new MemoryStream(Combine(parts));
        SourceText text = SourceText.From(stream, default, Structure, Options);
        Assert.Equal(string.Concat(parts.Select(part => part.text)), text.Source);
    }

    [Theory]
    [InlineData('"')]
    [InlineData('\'')]
    public void InvalidDbcsLeadCannotConsumeAClosingQuote(char quote)
    {
        byte[] bytes = Structure.GetBytes($"chapter main {{ $path={quote}")
            .Concat(new byte[] { 0xe5 })
            .Concat(Structure.GetBytes($"{quote}; CreateChoice(\"日本語\"); }}")).ToArray();
        using var stream = new MemoryStream(bytes);
        SourceText text = SourceText.From(stream, default, Structure, Options);
        Assert.Contains($"{quote}; CreateChoice(\"日本語\"); }}", text.Source);
    }

    [Fact]
    public void InvalidStructuralTagAttributeKeepsItsQuoteAndFollowingDialogue()
    {
        byte[] bytes = Structure.GetBytes("chapter main { <PRE @box>\n[text]\n<voice src=\"")
            .Concat(new byte[] { 0xe5 })
            .Concat(Structure.GetBytes("\" name=\"村正\">"))
            .Concat(Dialogue.GetBytes("中文"))
            .Concat(Structure.GetBytes("</PRE> }" )).ToArray();
        using var stream = new MemoryStream(bytes);
        SourceText text = SourceText.From(stream, default, Structure, Options);
        Assert.Contains("\" name=\"村正\">中文</PRE> }", text.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf8SourcesAndBomSourcesKeepTheirOwnEncoding(bool bom)
    {
        const string source = "chapter main { $TITLE_NOW=\"日本語中文\"; <PRE @box>\n[text]\n中文<voice name=\"村正\">\n</PRE> }";
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        if (bom) bytes = Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
        using var stream = new MemoryStream(bytes);
        SourceText text = SourceText.From(stream, default, bom ? Structure : Encoding.UTF8, Options);
        Assert.Equal(source, text.Source);
    }

    [Fact]
    public void DynamicSceneCompilationAndSourceMappingUseMixedDecoding()
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-mixed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "boot.nss"), Structure.GetBytes("""
                #include "補助.nss"
                chapter main {
                    $target = "続き.nss->開始";
                    call_scene $target;
                    CreateChoice("returned");
                }
                """));
            File.WriteAllBytes(Path.Combine(root, "補助.nss"), Structure.GetBytes("function ShowText() { LoadText(\"開始\", \"箱\", \"@sample\", 100, 100, 0, 0); }"));
            File.WriteAllBytes(Path.Combine(root, "続き.nss"), Combine(new (string, Encoding)[]
            {
                ("scene 開始 {\n$TITLE_NOW=\"", Structure),
                ("中文标题", Dialogue),
                ("\"; CreateChoice($TITLE_NOW);\nSetFont(\"", Structure),
                ("@黑体", Dialogue),
                ("\", 20, #FFFFFF, #222222, 500, RIGHTDOWN);\n<PRE @箱>\n[sample]\n<voice name=\"村正\" class=\"村正さん\" src=\"声/日本語\">\n", Structure),
                ("欢迎<ruby TEXT=\"钢铁\">凶刃</ruby>。", Dialogue),
                ("\n</PRE>\nShowText();\n}", Structure)
            }));
            var compilation = new Compilation(root, Structure, Options);
            var emitted = compilation.Emit([compilation.GetSourceModule("boot.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            Assert.False(File.Exists(Path.Combine(root, "続き.nsx")));
            var locator = new FileSystemNsxModuleLocator(root, root, Structure, Options);
            using var globals = File.OpenRead(Path.Combine(root, "globals"));
            var vm = new NsScriptVM(locator, globals);
            var thread = vm.CreateThread("boot", "main")!.Value;
            var builtins = new TestBuiltins();
            for (int i = 0; i < 100 && !thread.DoneExecuting; i++) vm.Tick(ref thread, builtins);
            Assert.True(thread.DoneExecuting);
            Assert.Equal(new[] { "中文标题", "returned" }, builtins.Choices);
            Assert.Equal("@黑体", Assert.Single(builtins.FontFamilies));
            DialogueBlockToken token = Assert.Single(builtins.Dialogues);
            string markup = string.Concat(token.Module.GetSubroutine(token.SubroutineIndex).DialogueBlocks
                .SelectMany(block => block.Parts).OfType<CompiledDialogueBlockPart.Markup>()
                .Select(part => part.GetText(token.Module)));
            Assert.Contains("欢迎<ruby TEXT=\"钢铁\">凶刃</ruby>。", markup);
            Assert.Contains("name=\"村正\" class=\"村正さん\" src=\"声/日本語\"", markup);
            using var mapping = locator.BeginSourceMapping();
            string mapped = mapping.GetSourceText("続き").Source;
            Assert.Contains("$TITLE_NOW=\"中文标题\"", mapped);
            Assert.Contains("SetFont(\"@黑体\", 20, #FFFFFF, #222222, 500, RIGHTDOWN);", mapped);
            Assert.Contains("欢迎<ruby TEXT=\"钢铁\">凶刃</ruby>。", mapped);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] Combine(IEnumerable<(string text, Encoding encoding)> parts)
        => parts.SelectMany(part => part.encoding.GetBytes(part.text)).ToArray();

    private sealed class TestBuiltins : BuiltInFunctions
    {
        public List<string> Choices { get; } = [];
        public List<DialogueBlockToken> Dialogues { get; } = [];
        public List<string> FontFamilies { get; } = [];
        public override void CreateChoice(in EntityPath path) => Choices.Add(path.ToString());
        public override void LoadDialogueBlock(in DialogueBlockToken blockToken, uint maxWidth, uint maxHeight, int letterSpacing, int lineSpacing)
            => Dialogues.Add(blockToken);
        public override void SetFont(string family, uint size, NsColor color, NsColor outlineColor, NsFontWeight weight, NsOutlineOffset outlineOffset)
            => FontFamilies.Add(family);
    }
}
