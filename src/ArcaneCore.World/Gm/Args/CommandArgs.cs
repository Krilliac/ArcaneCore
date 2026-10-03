using System.Globalization;

namespace ArcaneCore.World.Gm.Args;

/// <summary>
/// One shift-link found by <see cref="CommandArgs.ExtractLink"/>:
/// <c>|color|Hlinktype:key[:something...]|h[name]|h|r</c>.
/// </summary>
/// <param name="Text">The whole link text.</param>
/// <param name="LinkType">The link type including its leading H ("Hitem").</param>
/// <param name="TypeIndex">Index into the accepted link-type list (0 when no list was given).</param>
/// <param name="Key">The first field after the link type.</param>
/// <param name="Something">The second field when requested and present; empty otherwise.</param>
/// <param name="Name">The text between "[" and "]".</param>
public sealed record ChatLink(string Text, string LinkType, int TypeIndex, string Key, string Something, string Name);

/// <summary>
/// A cursor over a chat-command argument string, porting the vmangos ChatHandler Extract*
/// family (D:\refs\vmangos\src\game\Chat\Chat.cpp:2690-3290). Every extractor leaves the cursor
/// untouched when it fails, so a handler can retry another grammar; on success it moves past the
/// argument and the whitespace after it.
/// </summary>
/// <remarks>
/// Deliberate, documented differences from the C++: (1) all numeric extractors skip trailing
/// whitespace (vmangos ExtractInt32 leaves it for the next extractor, which skips it anyway);
/// (2) <see cref="ExtractUInt32"/> rejects a leading '-', where Linux strtoul wraps the value and
/// then fails the uint32 range check and Windows strtoul wraps it into a huge valid number;
/// (3) <see cref="ExtractFloat"/> accepts decimal floating-point only (no "inf", "nan" or hex floats).
/// </remarks>
public sealed class CommandArgs(string text)
{
    private readonly string _text = text ?? string.Empty;
    private int _pos;

    /// <summary>The unparsed remainder.</summary>
    public string Rest => _text[_pos..];

    /// <summary>True when nothing is left to parse.</summary>
    public bool IsEmpty => _pos >= _text.Length;

    /// <summary>C isspace.</summary>
    public static bool IsWhiteSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private char At(int index) => index < _text.Length ? _text[index] : '\0';

    private void SkipWhiteSpaces()
    {
        while (_pos < _text.Length && IsWhiteSpace(_text[_pos]))
        {
            _pos++;
        }
    }

    // ---- numbers ----------------------------------------------------------------------------

    /// <summary>Chat.cpp:2699 ExtractInt32 (strtol base 10, must end at whitespace or the end).</summary>
    public bool ExtractInt32(out int value)
    {
        value = 0;
        if (IsEmpty || !ScanInteger(allowSign: true, out long raw, out int end) || raw is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        value = (int)raw;
        _pos = end;
        SkipWhiteSpaces();
        return true;
    }

    /// <summary>Chat.cpp:2732 ExtractOptInt32.</summary>
    public bool ExtractOptInt32(out int value, int defaultValue)
    {
        if (IsEmpty)
        {
            value = defaultValue;
            return true;
        }

        return ExtractInt32(out value);
    }

    /// <summary>Chat.cpp:2756 ExtractUInt32Base (decimal).</summary>
    public bool ExtractUInt32(out uint value)
    {
        value = 0;
        if (IsEmpty || !ScanInteger(allowSign: false, out long raw, out int end) || raw is < 0 or > uint.MaxValue)
        {
            return false;
        }

        value = (uint)raw;
        _pos = end;
        SkipWhiteSpaces();
        return true;
    }

    /// <summary>Chat.cpp:2790 ExtractOptUInt32.</summary>
    public bool ExtractOptUInt32(out uint value, uint defaultValue)
    {
        if (IsEmpty)
        {
            value = defaultValue;
            return true;
        }

        return ExtractUInt32(out value);
    }

    /// <summary>Chat.cpp:2769 ExtractFloat (strtod decimal grammar).</summary>
    public bool ExtractFloat(out float value)
    {
        value = 0;
        if (IsEmpty || !ScanFloat(out double raw, out int end))
        {
            return false;
        }

        value = (float)raw;
        _pos = end;
        SkipWhiteSpaces();
        return true;
    }

    /// <summary>Chat.cpp:2797 ExtractOptFloat.</summary>
    public bool ExtractOptFloat(out float value, float defaultValue)
    {
        if (IsEmpty)
        {
            value = defaultValue;
            return true;
        }

        return ExtractFloat(out value);
    }

    private bool ScanInteger(bool allowSign, out long value, out int end)
    {
        value = 0;
        int i = _pos;
        while (i < _text.Length && IsWhiteSpace(_text[i]))
        {
            i++; // strtol skips leading whitespace
        }

        bool negative = false;
        if (At(i) is '+' or '-')
        {
            if (!allowSign && At(i) == '-')
            {
                end = i;
                return false;
            }

            negative = At(i) == '-';
            i++;
        }

        int digits = i;
        long acc = 0;
        while (i < _text.Length && _text[i] is >= '0' and <= '9')
        {
            if (acc <= long.MaxValue / 10 - 1)
            {
                acc = (acc * 10) + (_text[i] - '0');
            }
            else
            {
                acc = long.MaxValue / 2; // saturate: out of range for int32/uint32 either way
            }

            i++;
        }

        end = i;
        if (i == digits || (i < _text.Length && !IsWhiteSpace(_text[i])))
        {
            return false;
        }

        value = negative ? -acc : acc;
        return true;
    }

    private bool ScanFloat(out double value, out int end)
    {
        value = 0;
        int i = _pos;
        while (i < _text.Length && IsWhiteSpace(_text[i]))
        {
            i++;
        }

        int start = i;
        if (At(i) is '+' or '-')
        {
            i++;
        }

        int mantissaDigits = 0;
        while (At(i) is >= '0' and <= '9')
        {
            i++;
            mantissaDigits++;
        }

        if (At(i) == '.')
        {
            i++;
            while (At(i) is >= '0' and <= '9')
            {
                i++;
                mantissaDigits++;
            }
        }

        end = i;
        if (mantissaDigits == 0)
        {
            return false;
        }

        if (At(i) is 'e' or 'E')
        {
            int j = i + 1;
            if (At(j) is '+' or '-')
            {
                j++;
            }

            int expDigits = j;
            while (At(j) is >= '0' and <= '9')
            {
                j++;
            }

            if (j > expDigits)
            {
                i = j; // strtod only consumes a well-formed exponent
            }
        }

        end = i;
        if (end < _text.Length && !IsWhiteSpace(_text[end]))
        {
            return false;
        }

        return double.TryParse(_text.AsSpan(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ---- strings ----------------------------------------------------------------------------

    /// <summary>
    /// Chat.cpp:2825 ExtractLiteralArg: a name-like word up to the next space; rejects quotes, '['
    /// and links ('||' is the client's escape for one '|'). With <paramref name="literal"/> only
    /// that exact whole word matches.
    /// </summary>
    public string? ExtractLiteral(string? literal = null)
    {
        if (IsEmpty)
        {
            return null;
        }

        int head = _pos;
        switch (_text[head])
        {
            case '[' or '\'' or '"':
                return null;
            case '|':
                if (At(head + 1) != '|')
                {
                    return null;
                }

                head++;
                break;
        }

        if (literal is not null)
        {
            if (string.CompareOrdinal(_text, head, literal, 0, literal.Length) != 0
                || head + literal.Length > _text.Length
                || (head + literal.Length < _text.Length && !IsWhiteSpace(_text[head + literal.Length])))
            {
                return null;
            }

            _pos = head + literal.Length;
            SkipWhiteSpaces();
            return literal;
        }

        int space = _text.IndexOf(' ', head);
        string word;
        if (space < 0)
        {
            word = _text[head..];
            _pos = _text.Length;
        }
        else
        {
            word = _text[head..space];
            _pos = space + 1;
        }

        SkipWhiteSpaces();
        return word;
    }

    /// <summary>Chat.cpp:2905 ExtractQuotedArg: text guarded by ' " or [ ], which must be followed by whitespace or the end.</summary>
    public string? ExtractQuoted(bool asis = false)
    {
        if (IsEmpty || _text[_pos] is not ('\'' or '"' or '['))
        {
            return null;
        }

        char guard = _text[_pos] == '[' ? ']' : _text[_pos];
        int tail = _pos + 1;
        while (tail < _text.Length && _text[tail] != guard)
        {
            tail++;
        }

        if (tail >= _text.Length || (tail + 1 < _text.Length && !IsWhiteSpace(_text[tail + 1])))
        {
            return null;
        }

        string result = asis ? _text[_pos..(tail + 1)] : _text[(_pos + 1)..tail];
        _pos = tail + 1;
        SkipWhiteSpaces();
        return result;
    }

    /// <summary>Chat.cpp:2944 ExtractQuotedOrLiteralArg.</summary>
    public string? ExtractQuotedOrLiteral(bool asis = false) => ExtractQuoted(asis) ?? ExtractLiteral();

    /// <summary>Chat.cpp:2964 ExtractOnOff: only "on", "off", "ON" or "OFF".</summary>
    public bool ExtractOnOff(out bool value)
    {
        value = false;
        int saved = _pos;
        string? word = ExtractLiteral();
        switch (word)
        {
            case "on" or "ON":
                value = true;
                return true;
            case "off" or "OFF":
                return true;
            default:
                _pos = saved;
                return false;
        }
    }

    // ---- links ------------------------------------------------------------------------------

    /// <summary>
    /// Chat.cpp:3009 ExtractLinkArg. <paramref name="linkTypes"/> (each with its leading H) restricts
    /// the accepted link types; <paramref name="wantSomething"/> additionally captures the second field.
    /// </summary>
    public ChatLink? ExtractLink(IReadOnlyList<string>? linkTypes = null, bool wantSomething = false)
    {
        if (IsEmpty || _text[_pos] != '|' || At(_pos + 1) == '|')
        {
            return null;
        }

        int head = _pos;
        int tail = head + 1;

        if (At(tail) != 'H')
        {
            while (tail < _text.Length && _text[tail] != '|')
            {
                tail++;
            }

            if (tail >= _text.Length)
            {
                return null;
            }

            tail++;
        }

        if (At(tail) != 'H')
        {
            return null;
        }

        int typeStart = tail;
        int typeIndex = 0;
        if (linkTypes is not null)
        {
            for (; typeIndex < linkTypes.Count; typeIndex++)
            {
                string type = linkTypes[typeIndex];
                if (string.CompareOrdinal(_text, tail, type, 0, type.Length) == 0 && At(tail + type.Length) is ':' or '|')
                {
                    break;
                }
            }

            if (typeIndex >= linkTypes.Count)
            {
                return null;
            }

            tail += linkTypes[typeIndex].Length;
            if (At(tail) != ':')
            {
                return null;
            }
        }
        else
        {
            while (tail < _text.Length && _text[tail] != ':')
            {
                tail++;
            }

            if (tail >= _text.Length)
            {
                return null;
            }
        }

        string linkType = _text[typeStart..tail];
        tail++; // ':'

        int keyStart = tail;
        while (tail < _text.Length && _text[tail] != '|' && _text[tail] != ':')
        {
            tail++;
        }

        if (tail >= _text.Length)
        {
            return null;
        }

        int keyEnd = tail;
        int somethingStart = tail + 1;
        int somethingEnd = tail + 1;
        if (_text[tail] == ':' && wantSomething)
        {
            tail++;
            while (tail < _text.Length && _text[tail] != '|' && _text[tail] != ':')
            {
                tail++;
            }

            if (tail >= _text.Length)
            {
                return null;
            }

            somethingEnd = tail;
            somethingStart = keyEnd + 1;
        }

        while (tail < _text.Length && !(_text[tail] == '|' && At(tail + 1) == 'h'))
        {
            tail++;
        }

        if (tail >= _text.Length)
        {
            return null;
        }

        tail += 2; // "|h"
        if (At(tail) != '[')
        {
            return null;
        }

        int nameStart = tail + 1;
        while (tail < _text.Length && !(_text[tail] == ']' && At(tail + 1) == '|'))
        {
            tail++;
        }

        if (tail >= _text.Length)
        {
            return null;
        }

        int nameEnd = tail;
        tail += 2; // "]|"
        if (At(tail) != 'h' || At(tail + 1) != '|')
        {
            return null;
        }

        tail += 2; // "h|"
        if (At(tail) != 'r' || (tail + 1 < _text.Length && !IsWhiteSpace(_text[tail + 1])))
        {
            return null;
        }

        tail++; // 'r'
        string key = _text[keyStart..keyEnd];
        string something = wantSomething && somethingEnd > somethingStart ? _text[somethingStart..somethingEnd] : string.Empty;
        var link = new ChatLink(_text[head..tail], linkType, typeIndex, key, something, _text[nameStart..nameEnd]);
        _pos = tail;
        SkipWhiteSpaces();
        return link;
    }

    /// <summary>Chat.cpp:3170 ExtractArg: a quoted string, literal or shift-link.</summary>
    public string? ExtractArg(bool asis = false)
    {
        if (IsEmpty)
        {
            return null;
        }

        return ExtractQuotedOrLiteral(asis) ?? ExtractLink()?.Text;
    }

    /// <summary>
    /// Chat.cpp:3190 ExtractOptNotLastArg: the argument, but only when more data follows; when it
    /// is the last argument the cursor stays on it and null is returned.
    /// </summary>
    public string? ExtractOptNotLastArg()
    {
        int saved = _pos;
        string? arg = ExtractArg(asis: true);
        if (!IsEmpty)
        {
            return arg;
        }

        _pos = arg is null ? _text.Length : saved;
        return null;
    }

    /// <summary>
    /// Chat.cpp:3245 ExtractKeyFromLink: the key of an accepted link, or the plain quoted/literal
    /// text when the argument is not a link (<paramref name="foundIndex"/> is then -1).
    /// </summary>
    public string? ExtractKeyFromLink(IReadOnlyList<string> linkTypes, out int foundIndex, out string? something)
    {
        foundIndex = -1;
        something = null;
        if (IsEmpty)
        {
            return null;
        }

        string? plain = ExtractQuotedOrLiteral();
        if (plain is not null)
        {
            return plain;
        }

        ChatLink? link = ExtractLink(linkTypes, wantSomething: true);
        if (link is null)
        {
            return null;
        }

        foundIndex = link.TypeIndex;
        something = link.Something;
        return link.Key;
    }

    /// <summary>Single-link-type form of <see cref="ExtractKeyFromLink(IReadOnlyList{string}, out int, out string?)"/>.</summary>
    public string? ExtractKeyFromLink(string linkType, out int foundIndex, out string? something)
        => ExtractKeyFromLink([linkType], out foundIndex, out something);

    /// <summary>Chat.cpp:3282 ExtractUint32KeyFromLink: a plain number or the numeric key of a link.</summary>
    public bool ExtractUInt32KeyFromLink(string linkType, out uint value)
    {
        value = 0;
        int saved = _pos;
        string? key = ExtractKeyFromLink(linkType, out _, out _);
        if (key is null || !new CommandArgs(key).ExtractUInt32(out value))
        {
            _pos = saved;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Chat.cpp:3364 ExtractGameTeleFromLink: a numeric id (<paramref name="id"/>, name null),
    /// otherwise a name (<paramref name="id"/> 0); a <c>|Htele:id|</c> link yields its id.
    /// </summary>
    public bool ExtractGameTele(out uint id, out string? name)
    {
        id = 0;
        name = null;
        string? key = ExtractKeyFromLink("Htele", out _, out _);
        if (key is null)
        {
            return false;
        }

        if (new CommandArgs(key).ExtractUInt32(out id))
        {
            return true;
        }

        name = key;
        return true;
    }
}
