using System;
using System.Linq;
using NitroSharp.LegacyNps;
using Xunit;

namespace NitroSharp.Tests;

public sealed class LegacyNpsReaderTests
{
    [Fact]
    public void InlineCommandsKeepTextAndQuotedComparisonSigns()
    {
        var tokens = LegacyNpsReader.Read("<A HREF=#NEXT OPERATOR=\"$x>3\">\r\nHello<k>world").ToArray();
        Assert.Equal("#NEXT", tokens[0].Attributes["href"]);
        Assert.Equal("$x>3", tokens[0].Attributes["operator"]);
        Assert.Equal("\r\nHello", tokens[1].Value);
        Assert.Equal("K", tokens[2].Value);
        Assert.Equal(2, tokens[2].Line);
        Assert.Equal("world", tokens[3].Value);
    }

    [Fact]
    public void CommentsDoNotConsumeTheNextLineAndPathsKeepBackslashes()
    {
        var tokens = LegacyNpsReader.Read("// ignored <broken\n<BACKGROUND SRC=\"title\\main.png\"><! note><コマンド メニュー>").ToArray();
        Assert.Equal(3, tokens.Count(x => x.Kind == LegacyNpsTokenKind.Comment));
        Assert.Equal("title\\main.png", tokens.Single(x => x.Kind == LegacyNpsTokenKind.Command).Attributes["SRC"]);
    }

    [Fact]
    public void UnknownCommandsRemainVisibleWithoutRejectingEarlierTokens()
    {
        using var tokens = LegacyNpsReader.Read("<WAIT TIME=1><UNKNOWN X=2><BROKEN").GetEnumerator();
        Assert.True(tokens.MoveNext()); Assert.Equal("WAIT", tokens.Current.Value);
        Assert.True(tokens.MoveNext()); Assert.Equal("UNKNOWN", tokens.Current.Value);
        Assert.Throws<FormatException>(() => tokens.MoveNext());
    }

    [Theory]
    [InlineData("<A HREF='never>")]
    [InlineData("<A HREF=one href=two>")]
    [InlineData("<>")]
    public void InvalidSyntaxReportsTheEncounteredSourceLine(string text)
        => Assert.Contains("line 2", Assert.Throws<FormatException>(() => LegacyNpsReader.Read("\n" + text).ToArray()).Message);
}
