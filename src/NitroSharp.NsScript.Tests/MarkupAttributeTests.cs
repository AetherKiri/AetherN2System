using System;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Syntax.Markup;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class MarkupAttributeTests
{
    [Theory]
    [InlineData("<RUBY TEXT=\"Ｆａｌｌｉｎｇ　Ｄｏｗｎ\">base</RUBY>", "Ｆａｌｌｉｎｇ　Ｄｏｗｎ")]
    [InlineData("<RUBY TEXT=\"two words\">base</RUBY>", "two words")]
    [InlineData("<RUBY\tTEXT = 'two words'>base</RUBY>", "two words")]
    [InlineData("<RUBY TEXT=\"\"ＤＶＤ\"\">base</RUBY>", "ＤＶＤ")]
    [InlineData("<RUBY TEXT=\"\">base</RUBY>", "")]
    public void RubyReadingsAcceptQuotedWhitespaceAndShippedDoubleDelimiters(string markup, string reading)
    {
        var ruby = Assert.IsType<RubyElement>(Assert.Single(MarkupContent.Parse(markup).Children));
        Assert.Equal(reading, ruby.RubyText);
    }

    [Theory]
    [InlineData("<RUBY")]
    [InlineData("<RUBY TEXT>")]
    [InlineData("<RUBY TEXT=\"unterminated")]
    [InlineData("<RUBY TEXT=\"\"unterminated>")]
    [InlineData("<RUBY TEXT=value")]
    public void MalformedAttributesThrowBeforeScanningPastEnd(string markup)
        => Assert.Throws<FormatException>(() => MarkupContent.Parse(markup));

    [Fact]
    public void MixedDecoderKeepsDoubleDelimitedRubyReadingsInDialogueEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding structure = Encoding.GetEncoding("shift-jis"), dialogue = Encoding.GetEncoding("gbk");
        using var stream = new MemoryStream();
        stream.Write(structure.GetBytes("chapter main {\n<PRE @box0>\n[text]\n<RUBY TEXT=\"\""));
        stream.Write(dialogue.GetBytes("ＤＶＤ"));
        stream.Write(structure.GetBytes("\"\">"));
        stream.Write(dialogue.GetBytes("影像光盘"));
        stream.Write(structure.GetBytes("</RUBY>\n</PRE>\n}"));
        stream.Position = 0;
        string decoded = SourceText.From(stream, default, structure, new SourceTextDecodingOptions(dialogue)).Source;
        Assert.Contains("<RUBY TEXT=\"\"ＤＶＤ\"\">影像光盘</RUBY>", decoded);
        int start = decoded.IndexOf("<RUBY", StringComparison.Ordinal), end = decoded.IndexOf("</RUBY>", StringComparison.Ordinal) + 7;
        var ruby = Assert.IsType<RubyElement>(Assert.Single(MarkupContent.Parse(decoded[start..end]).Children));
        Assert.Equal("ＤＶＤ", ruby.RubyText);
        Assert.Equal("影像光盘", Assert.IsType<MarkupText>(Assert.Single(ruby.RubyBase.Children)).Text);
    }
}
