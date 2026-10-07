using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// One JSON object per event, on one line, for log shippers:
/// <code>{"ts":"2026-10-04T12:34:56.789Z","level":"Information","category":"...","eventId":7,"eventName":"Perf","message":"...","template":"...","props":{"Name":"value"},"scopes":["..."],"exception":"..."}</code>
/// Keys are stable; absent data omits its key (<c>eventId</c> and <c>eventName</c> when default, <c>props</c> when the state has no
/// named values, <c>scopes</c> when none is active, <c>exception</c> when none). Strings are escaped per RFC 8259 (control
/// characters as <c>\uXXXX</c>), so a client-controlled value cannot break the line. Numbers, booleans and null in the state are
/// written as JSON values; everything else is written as its invariant string. Written by hand into the caller's buffer; the
/// only allocation on the fast path is the boxing of a value-type state, which the MEL contract makes unavoidable here.
/// </summary>
public static class JsonLineFormatter
{
    public static void Write<TState>(ref LogBuffer buffer, in LogEvent evt, TState state, IExternalScopeProvider? scopes)
    {
        buffer.Append("{\"ts\":\"");
        WriteTimestamp(ref buffer, evt.Timestamp);
        buffer.Append("\",\"level\":\"");
        buffer.Append(LogLineFormatter.LevelName(evt.Level));
        buffer.Append("\",\"category\":");
        WriteString(ref buffer, evt.Category);
        if (evt.EventId.Id != 0)
        {
            buffer.Append(",\"eventId\":");
            buffer.Append(evt.EventId.Id);
        }

        if (evt.EventId.Name is not null)
        {
            buffer.Append(",\"eventName\":");
            WriteString(ref buffer, evt.EventId.Name);
        }

        buffer.Append(",\"message\":");
        WriteString(ref buffer, evt.Message);

        if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
        {
            WriteState(ref buffer, pairs);
        }

        if (scopes is not null)
        {
            ScopeText text = ScopeText.Collect(scopes);
            if (text.Count > 0)
            {
                WriteScopes(ref buffer, text.Written);
            }
        }

        if (evt.Exception is not null)
        {
            buffer.Append(",\"exception\":");
            WriteString(ref buffer, evt.Exception.ToString());
        }

        buffer.Append("}\n");
    }

    /// <summary>ISO 8601 with milliseconds: <c>Z</c> for UTC, the local offset otherwise.</summary>
    public static void WriteTimestamp(ref LogBuffer buffer, DateTime time)
    {
        buffer.AppendDigits(time.Year, 4);
        buffer.Append('-');
        buffer.AppendDigits(time.Month, 2);
        buffer.Append('-');
        buffer.AppendDigits(time.Day, 2);
        buffer.Append('T');
        buffer.AppendDigits(time.Hour, 2);
        buffer.Append(':');
        buffer.AppendDigits(time.Minute, 2);
        buffer.Append(':');
        buffer.AppendDigits(time.Second, 2);
        buffer.Append('.');
        buffer.AppendDigits(time.Millisecond, 3);
        if (time.Kind == DateTimeKind.Utc)
        {
            buffer.Append('Z');
            return;
        }

        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(time);
        buffer.Append(offset < TimeSpan.Zero ? '-' : '+');
        offset = offset.Duration();
        buffer.AppendDigits(offset.Hours, 2);
        buffer.Append(':');
        buffer.AppendDigits(offset.Minutes, 2);
    }

    /// <summary>A quoted, escaped JSON string.</summary>
    public static void WriteString(ref LogBuffer buffer, ReadOnlySpan<char> text)
    {
        buffer.Append('"');
        int flushed = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 0x20 && c != '"' && c != '\\')
            {
                continue;
            }

            buffer.Append(text[flushed..i]);
            flushed = i + 1;
            switch (c)
            {
                case '"':
                    buffer.Append("\\\"");
                    break;
                case '\\':
                    buffer.Append("\\\\");
                    break;
                case '\n':
                    buffer.Append("\\n");
                    break;
                case '\r':
                    buffer.Append("\\r");
                    break;
                case '\t':
                    buffer.Append("\\t");
                    break;
                default:
                    buffer.Append("\\u00");
                    buffer.Append(HexDigit(c >> 4));
                    buffer.Append(HexDigit(c & 0xF));
                    break;
            }
        }

        buffer.Append(text[flushed..]);
        buffer.Append('"');
    }

    private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + value - 10);

    private static void WriteState(ref LogBuffer buffer, IReadOnlyList<KeyValuePair<string, object?>> pairs)
    {
        // Two passes over the (small, indexed) list: the template first, then the named values. The indexer is used instead of
        // foreach because FormattedLogValues' enumerator is a class.
        for (int i = 0; i < pairs.Count; i++)
        {
            KeyValuePair<string, object?> pair = pairs[i];
            if (pair.Key == "{OriginalFormat}" && pair.Value is string template)
            {
                buffer.Append(",\"template\":");
                WriteString(ref buffer, template);
                break;
            }
        }

        bool open = false;
        for (int i = 0; i < pairs.Count; i++)
        {
            KeyValuePair<string, object?> pair = pairs[i];
            if (pair.Key == "{OriginalFormat}")
            {
                continue;
            }

            buffer.Append(open ? "," : ",\"props\":{");
            open = true;
            WriteString(ref buffer, pair.Key);
            buffer.Append(':');
            WriteValue(ref buffer, pair.Value);
        }

        if (open)
        {
            buffer.Append('}');
        }
    }

    private static void WriteValue(ref LogBuffer buffer, object? value)
    {
        switch (value)
        {
            case null:
                buffer.Append("null");
                break;
            case string s:
                WriteString(ref buffer, s);
                break;
            case bool b:
                buffer.Append(b ? "true" : "false");
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                WriteNumber(ref buffer, (ISpanFormattable)value);
                break;
            case double d when double.IsFinite(d):
                WriteNumber(ref buffer, d);
                break;
            case float f when float.IsFinite(f):
                WriteNumber(ref buffer, f);
                break;
            case decimal m:
                WriteNumber(ref buffer, m);
                break;
            default:
                WriteString(ref buffer, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private static void WriteNumber(ref LogBuffer buffer, ISpanFormattable value)
    {
        Span<char> digits = stackalloc char[64];
        if (value.TryFormat(digits, out int written, default, CultureInfo.InvariantCulture))
        {
            buffer.Append(digits[..written]);
        }
        else
        {
            WriteString(ref buffer, value.ToString(null, CultureInfo.InvariantCulture));
        }
    }

    private static void WriteScopes(ref LogBuffer buffer, ReadOnlySpan<char> rendered)
    {
        // rendered is " => a => b": split on the separator into a JSON array of strings
        buffer.Append(",\"scopes\":[");
        ReadOnlySpan<char> separator = ScopeText.Separator;
        bool first = true;
        rendered = rendered[separator.Length..];
        while (true)
        {
            int next = rendered.IndexOf(separator);
            ReadOnlySpan<char> item = next < 0 ? rendered : rendered[..next];
            if (!first)
            {
                buffer.Append(',');
            }

            first = false;
            WriteString(ref buffer, item);
            if (next < 0)
            {
                break;
            }

            rendered = rendered[(next + separator.Length)..];
        }

        buffer.Append(']');
    }
}
