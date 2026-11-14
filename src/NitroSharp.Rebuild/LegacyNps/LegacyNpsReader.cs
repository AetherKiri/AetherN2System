using System;
using System.Collections.Generic;

namespace NitroSharp.LegacyNps;

internal enum LegacyNpsTokenKind { Command, Text, Comment }

internal sealed record LegacyNpsToken(LegacyNpsTokenKind Kind, string Value,
    IReadOnlyDictionary<string, string> Attributes, int Offset, int Line);

// Tokenization is deliberately lazy: an unsupported command can be reported when
// reached, without rejecting unseen story branches. No rendering commands are ignored here.
internal static class LegacyNpsReader
{
    public static IEnumerable<LegacyNpsToken> Read(string source)
    {
        int position = 0;
        int line = 1;
        while (position < source.Length)
        {
            int start = position;
            int startLine = line;
            if (source[position] == '/' && position + 1 < source.Length && source[position + 1] == '/')
            {
                while (position < source.Length && source[position] != '\n') position++;
                yield return Token(LegacyNpsTokenKind.Comment, source[start..position], start, startLine);
                continue;
            }
            if (source[position] != '<')
            {
                while (position < source.Length && source[position] != '<'
                    && !(source[position] == '/' && position + 1 < source.Length && source[position + 1] == '/'))
                {
                    if (source[position++] == '\n') line++;
                }
                yield return Token(LegacyNpsTokenKind.Text, source[start..position], start, startLine);
                continue;
            }
            position++;
            char quote = '\0';
            while (position < source.Length)
            {
                char character = source[position++];
                if (character == '\n') line++;
                if (quote != '\0')
                {
                    if (character == quote) quote = '\0';
                }
                else if (character is '"' or '\'') quote = character;
                else if (character == '>') break;
            }
            if (position == source.Length && source[position - 1] != '>')
                throw new FormatException($"Unterminated NPS tag at line {startLine}.");
            string contents = source[(start + 1)..(position - 1)].Trim();
            if (contents.StartsWith('!'))
            {
                yield return Token(LegacyNpsTokenKind.Comment, contents, start, startLine);
                continue;
            }
            int cursor = 0;
            while (cursor < contents.Length && !char.IsWhiteSpace(contents[cursor])) cursor++;
            string command = contents[..cursor];
            if (command.Length == 0) throw new FormatException($"Empty NPS command at line {startLine}.");
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (cursor < contents.Length)
            {
                SkipSpace();
                if (cursor == contents.Length) break;
                int nameStart = cursor;
                while (cursor < contents.Length && !char.IsWhiteSpace(contents[cursor]) && contents[cursor] != '=') cursor++;
                string name = contents[nameStart..cursor];
                SkipSpace();
                if (name.Length == 0 || cursor == contents.Length || contents[cursor++] != '=')
                {
                    // Editor annotations are preserved, rather than confused with executable commands.
                    if (command is "コマンド" or "コメント") break;
                    throw new FormatException($"Invalid NPS attribute at line {startLine}.");
                }
                SkipSpace();
                string value;
                if (cursor < contents.Length && contents[cursor] is '"' or '\'')
                {
                    char delimiter = contents[cursor++];
                    int valueStart = cursor;
                    while (cursor < contents.Length && contents[cursor] != delimiter) cursor++;
                    if (cursor == contents.Length) throw new FormatException($"Unterminated NPS attribute at line {startLine}.");
                    value = contents[valueStart..cursor++];
                }
                else
                {
                    int valueStart = cursor;
                    while (cursor < contents.Length && !char.IsWhiteSpace(contents[cursor])) cursor++;
                    value = contents[valueStart..cursor];
                }
                if (!attributes.TryAdd(name, value)) throw new FormatException($"Duplicate NPS attribute '{name}' at line {startLine}.");
            }
            yield return new LegacyNpsToken(command is "コマンド" or "コメント" ? LegacyNpsTokenKind.Comment : LegacyNpsTokenKind.Command,
                command.ToUpperInvariant(), attributes, start, startLine);

            void SkipSpace() { while (cursor < contents.Length && char.IsWhiteSpace(contents[cursor])) cursor++; }
        }
    }

    private static LegacyNpsToken Token(LegacyNpsTokenKind kind, string value, int offset, int line)
        => new(kind, value, new Dictionary<string, string>(), offset, line);
}
