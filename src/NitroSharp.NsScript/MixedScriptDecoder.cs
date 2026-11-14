using System;
using System.Collections.Generic;
using System.Text;

namespace NitroSharp.NsScript;

public sealed record SourceTextDecodingOptions(Encoding DialogueEncoding, IReadOnlySet<string>? DialogueVariables = null);

// Translation patches can replace displayed text without re-encoding paths,
// identifiers, voice metadata or embedded script. Work from bytes so an invalid
// sequence in one encoding is never lost before the other encoding can read it.
internal sealed class MixedScriptDecoder(byte[] bytes, Encoding structure, SourceTextDecodingOptions options)
{
    private readonly StringBuilder _output = new();
    private int _position;

    public string Decode()
    {
        while (_position < bytes.Length) ReadCode(false);
        return _output.ToString();
    }

    private void ReadCode(bool embedded)
    {
        int depth = embedded ? 1 : 0;
        while (_position < bytes.Length)
        {
            int start = _position;
            if (Matches("//")) { CopyLine(); continue; }
            if (Matches("/*")) { CopyBlockComment(); continue; }
            if (IsPreStart()) { CopyTag(); ReadDialogue(); continue; }
            byte value = bytes[_position];
            if (value is (byte)'"' or (byte)'\'') { CopyQuoted(structure); continue; }
            if (value == '$' && TryDisplayVariable()) continue;
            if (value is (byte)'S' or (byte)'s' && TryDisplayFont()) continue;
            if (embedded && value == '{') depth++;
            if (embedded && value == '}' && --depth == 0)
            {
                _position++;
                Append(start, _position, structure);
                return;
            }
            _position += CharacterLength(_position, structure);
            Append(start, _position, structure);
        }
    }

    private void ReadDialogue()
    {
        int nesting = 1;
        while (_position < bytes.Length)
        {
            if (Matches("</PRE>", true))
            {
                CopyTag();
                if (--nesting == 0) return;
                continue;
            }
            if (IsPreStart()) { CopyTag(); nesting++; continue; }
            if (Matches("//")) { CopyLine(); continue; }
            if (nesting == 1 && bytes[_position] == '{')
            {
                int openingBrace = _position++;
                Append(openingBrace, _position, structure);
                ReadCode(true);
                continue;
            }
            if (bytes[_position] == '[' && IsLineStart(_position))
            {
                int start = _position++;
                while (_position < bytes.Length && bytes[_position] != ']' && bytes[_position] is not (10 or 13))
                    _position += CharacterLength(_position, structure);
                if (_position < bytes.Length && bytes[_position] == ']') _position++;
                Append(start, _position, structure);
                continue;
            }
            if (bytes[_position] == '<') { CopyTag(); continue; }
            int textStart = _position;
            do { _position += CharacterLength(_position, options.DialogueEncoding); }
            while (_position < bytes.Length && bytes[_position] is not ((byte)'<' or (byte)'{' or (byte)'[')
                && !Matches("//"));
            Append(textStart, _position, options.DialogueEncoding);
        }
    }

    private void CopyTag()
    {
        bool ruby = Matches("<RUBY", true) && _position + 5 < bytes.Length && IsSpace(bytes[_position + 5]);
        int start = _position++;
        while (_position < bytes.Length)
        {
            if (ruby && Matches("TEXT", true) && (_position == start + 5 || IsSpace(bytes[_position - 1])))
            {
                int attribute = _position;
                _position += 4;
                while (_position < bytes.Length && IsSpace(bytes[_position])) _position++;
                if (_position < bytes.Length && bytes[_position] == '=')
                {
                    _position++;
                    while (_position < bytes.Length && IsSpace(bytes[_position])) _position++;
                    if (_position < bytes.Length && bytes[_position] is (byte)'"' or (byte)'\'')
                    {
                        Append(start, _position, structure);
                        CopyQuoted(options.DialogueEncoding, allowDoubledDelimiter: true);
                        start = _position;
                        continue;
                    }
                }
                _position = attribute;
            }
            if (bytes[_position] is (byte)'"' or (byte)'\'')
            {
                Append(start, _position, structure);
                CopyQuoted(structure);
                start = _position;
                continue;
            }
            if (bytes[_position++] == '>') break;
        }
        Append(start, _position, structure);
    }

    private bool TryDisplayVariable()
    {
        if (options.DialogueVariables is not { Count: > 0 }) return false;
        int start = _position;
        int nameStart = ++_position;
        while (_position < bytes.Length && !IsSpace(bytes[_position]) && bytes[_position] is not ((byte)'=' or (byte)';'))
            _position += CharacterLength(_position, structure);
        string name = structure.GetString(bytes, nameStart, _position - nameStart);
        while (_position < bytes.Length && IsSpace(bytes[_position])) _position++;
        if (options.DialogueVariables.Contains(name) && _position < bytes.Length && bytes[_position] == '=')
        {
            _position++;
            while (_position < bytes.Length && IsSpace(bytes[_position])) _position++;
            if (_position < bytes.Length && bytes[_position] is (byte)'"' or (byte)'\'')
            {
                Append(start, _position, structure);
                CopyQuoted(options.DialogueEncoding);
                return true;
            }
        }
        _position = start;
        return false;
    }

    private bool TryDisplayFont()
    {
        const string function = "SETFONT";
        int start = _position;
        if (!Matches(function, true) || (start > 0 && IsIdentifierByte(bytes[start - 1]))) return false;
        _position += function.Length;
        while (_position < bytes.Length && IsSpace(bytes[_position])) _position++;
        if (_position < bytes.Length && bytes[_position] == '(')
        {
            _position++;
            while (_position < bytes.Length && IsSpace(bytes[_position])) _position++;
            if (_position < bytes.Length && bytes[_position] is (byte)'"' or (byte)'\'')
            {
                // Localized patches also replace font family names; the remaining
                // arguments and unrelated quoted strings retain structural encoding.
                Append(start, _position, structure);
                CopyQuoted(options.DialogueEncoding);
                return true;
            }
        }
        _position = start;
        return false;
    }

    private void CopyQuoted(Encoding encoding, bool allowDoubledDelimiter = false)
    {
        byte quote = bytes[_position++];
        _output.Append((char)quote);
        bool doubled = allowDoubledDelimiter && _position + 1 < bytes.Length
            && bytes[_position] == quote && bytes[_position + 1] != '>' && !IsSpace(bytes[_position + 1]);
        if (doubled) { _position++; _output.Append((char)quote); }
        int start = _position;
        while (_position < bytes.Length && (bytes[_position] != quote
            || doubled && (_position + 1 >= bytes.Length || bytes[_position + 1] != quote)))
            _position += CharacterLength(_position, encoding);
        Append(start, _position, encoding);
        if (_position < bytes.Length)
        {
            _position++;
            _output.Append((char)quote);
            if (doubled && _position < bytes.Length) { _position++; _output.Append((char)quote); }
        }
    }

    private void CopyLine()
    {
        int start = _position;
        while (_position < bytes.Length && bytes[_position] is not (10 or 13)) _position++;
        Append(start, _position, structure);
    }

    private void CopyBlockComment()
    {
        int start = _position;
        _position += 2;
        while (_position < bytes.Length && !Matches("*/")) _position += CharacterLength(_position, structure);
        if (_position < bytes.Length) _position += 2;
        Append(start, _position, structure);
    }

    private bool IsPreStart() => Matches("<PRE", true) && _position + 4 < bytes.Length
        && (IsSpace(bytes[_position + 4]) || bytes[_position + 4] == '>');

    private bool IsLineStart(int position)
    {
        while (--position >= 0 && bytes[position] is not (10 or 13))
            if (!IsSpace(bytes[position])) return false;
        return true;
    }

    private bool Matches(string text, bool ignoreCase = false)
    {
        if (bytes.Length - _position < text.Length) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char value = (char)bytes[_position + i];
            if (ignoreCase) value = char.ToUpperInvariant(value);
            if (value != text[i]) return false;
        }
        return true;
    }

    private int CharacterLength(int position, Encoding encoding)
    {
        byte value = bytes[position];
        bool lead = encoding.CodePage switch
        {
            932 => value is >= 0x81 and <= 0x9f or >= 0xe0 and <= 0xfc,
            936 or 950 or 949 => value is >= 0x81 and <= 0xfe,
            _ => false
        };
        if (!lead || position + 1 >= bytes.Length) return 1;
        byte trail = bytes[position + 1];
        bool validTrail = encoding.CodePage switch
        {
            932 => trail is >= 0x40 and <= 0x7e or >= 0x80 and <= 0xfc,
            936 => trail is >= 0x40 and <= 0xfe && trail != 0x7f,
            950 => trail is >= 0x40 and <= 0x7e or >= 0xa1 and <= 0xfe,
            949 => trail is >= 0x41 and <= 0x5a or >= 0x61 and <= 0x7a or >= 0x81 and <= 0xfe,
            _ => false
        };
        // An invalid lead must not swallow an ASCII quote, newline or other
        // delimiter when a translated region uses a different encoding.
        return validTrail ? 2 : 1;
    }

    private static bool IsIdentifierByte(byte value)
        => value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'0' and <= (byte)'9' or (byte)'_' or >= 0x80;
    private static bool IsSpace(byte value) => value is 9 or 10 or 13 or 32;
    private void Append(int start, int end, Encoding encoding)
        => _output.Append(encoding.GetString(bytes, start, end - start));
}
