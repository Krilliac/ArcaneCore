using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// The text line format shared by the console and the file sink:
/// <code>2026-10-04 12:34:56.789 INFO ArcaneCore.World.Net.WorldServer[Perf:7] =&gt; scope: message</code>
/// followed by the exception text on its own lines. The level column is fixed at four characters so journald and
/// <c>cut</c>/<c>awk</c> see stable columns. In colour mode the same text is wrapped in ANSI SGR codes (level coloured,
/// timestamp/category/scopes dimmed); the layout is identical, so a <c>sed 's/\x1b\[[0-9;]*m//g'</c> of a colour log equals the plain log.
/// Allocation: none for a message without exception or scopes (every field is formatted into the caller's <see cref="LogBuffer"/>).
/// Variable text (message, category, event name, scopes, exception lines) goes through <see cref="AppendEscaped"/>: CR, LF, every other C0
/// control (including ESC), DEL, the C1 controls (including NEL) and U+2028/U+2029 are written as visible escapes, so no caller can make
/// a sink emit a forged line (CWE-117). Text without such characters is copied unchanged and allocates nothing. Callers still use
/// <see cref="LogSafe"/> where truncation is wanted.
/// </summary>
public static class LogLineFormatter
{
    private const string Reset = "\u001b[0m";
    private const string Dim = "\u001b[2m";

    /// <summary>Writes one complete line (with trailing newline) for <paramref name="evt"/>.</summary>
    public static void Write(ref LogBuffer buffer, in LogEvent evt, IExternalScopeProvider? scopes, bool color)
    {
        if (color)
        {
            buffer.Append(Dim);
        }

        WriteTimestamp(ref buffer, evt.Timestamp);
        if (color)
        {
            buffer.Append(Reset);
        }

        buffer.Append(' ');
        if (color)
        {
            buffer.Append(LevelColor(evt.Level));
        }

        buffer.Append(LevelText(evt.Level));
        if (color)
        {
            buffer.Append(Reset);
            buffer.Append(' ');
            buffer.Append(Dim);
        }
        else
        {
            buffer.Append(' ');
        }

        AppendEscaped(ref buffer, evt.Category);
        WriteEventId(ref buffer, evt.EventId);
        if (scopes is not null)
        {
            ScopeText text = ScopeText.Collect(scopes);
            AppendEscaped(ref buffer, text.Written);
        }

        buffer.Append(':');
        if (color)
        {
            buffer.Append(Reset);
        }

        buffer.Append(' ');
        AppendEscaped(ref buffer, evt.Message);
        buffer.Append('\n');
        if (evt.Exception is not null)
        {
            WriteException(ref buffer, evt.Exception);
        }
    }

    /// <summary>Writes <paramref name="exception"/> as it prints itself, each line indented, ending with a newline.</summary>
    public static void WriteException(ref LogBuffer buffer, Exception exception)
    {
        ReadOnlySpan<char> text = exception.ToString();
        while (!text.IsEmpty)
        {
            int newline = text.IndexOf('\n');
            ReadOnlySpan<char> line = newline < 0 ? text : text[..newline];
            text = newline < 0 ? default : text[(newline + 1)..];
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            buffer.Append("      ");
            AppendEscaped(ref buffer, line);
            buffer.Append('\n');
        }
    }

    /// <summary>True for a character a line-oriented sink must not emit raw: C0 controls, DEL, C1 controls, U+2028 and U+2029.</summary>
    public static bool NeedsEscape(char c) => c < 0x20 || (c >= 0x7F && c <= 0x9F) || c == (char)0x2028 || c == (char)0x2029;

    /// <summary>
    /// Appends <paramref name="text"/> with every <see cref="NeedsEscape"/> character written as a visible escape: <c>\r</c>, <c>\n</c>,
    /// <c>\t</c> or <c>\uXXXX</c> (upper-case hex). Clean runs are copied in one piece.
    /// </summary>
    public static void AppendEscaped(ref LogBuffer buffer, scoped ReadOnlySpan<char> text)
    {
        int flushed = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!NeedsEscape(c))
            {
                continue;
            }

            buffer.Append(text[flushed..i]);
            flushed = i + 1;
            switch (c)
            {
                case '\r':
                    buffer.Append("\\r");
                    break;
                case '\n':
                    buffer.Append("\\n");
                    break;
                case '\t':
                    buffer.Append("\\t");
                    break;
                default:
                    buffer.Append("\\u");
                    buffer.Append(HexUpper((c >> 12) & 0xF));
                    buffer.Append(HexUpper((c >> 8) & 0xF));
                    buffer.Append(HexUpper((c >> 4) & 0xF));
                    buffer.Append(HexUpper(c & 0xF));
                    break;
            }
        }

        buffer.Append(text[flushed..]);
    }

    private static char HexUpper(int value) => (char)(value < 10 ? '0' + value : 'A' + value - 10);

    /// <summary><c>yyyy-MM-dd HH:mm:ss.fff</c>, hand formatted so the fast path does not depend on the runtime's format parser.</summary>
    public static void WriteTimestamp(ref LogBuffer buffer, DateTime time)
    {
        buffer.AppendDigits(time.Year, 4);
        buffer.Append('-');
        buffer.AppendDigits(time.Month, 2);
        buffer.Append('-');
        buffer.AppendDigits(time.Day, 2);
        buffer.Append(' ');
        buffer.AppendDigits(time.Hour, 2);
        buffer.Append(':');
        buffer.AppendDigits(time.Minute, 2);
        buffer.Append(':');
        buffer.AppendDigits(time.Second, 2);
        buffer.Append('.');
        buffer.AppendDigits(time.Millisecond, 3);
    }

    /// <summary><c>[Name:Id]</c>, <c>[Name]</c> or <c>[Id]</c>; nothing for the default event id.</summary>
    public static void WriteEventId(ref LogBuffer buffer, EventId eventId)
    {
        if (eventId.Id == 0 && eventId.Name is null)
        {
            return;
        }

        buffer.Append('[');
        if (eventId.Name is not null)
        {
            AppendEscaped(ref buffer, eventId.Name);
            if (eventId.Id != 0)
            {
                buffer.Append(':');
                buffer.Append(eventId.Id);
            }
        }
        else
        {
            buffer.Append(eventId.Id);
        }

        buffer.Append(']');
    }

    /// <summary>The fixed four-character level column.</summary>
    public static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRCE",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Critical => "CRIT",
        _ => "NONE",
    };

    /// <summary>The level's full name as the JSON sink writes it.</summary>
    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Information",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        _ => "None",
    };

    /// <summary>ANSI SGR sequence for a level: grey, white, green, yellow, red, white on red.</summary>
    public static string LevelColor(LogLevel level) => level switch
    {
        LogLevel.Trace => "\u001b[90m",
        LogLevel.Debug => "\u001b[37m",
        LogLevel.Information => "\u001b[32m",
        LogLevel.Warning => "\u001b[33m",
        LogLevel.Error => "\u001b[31m",
        LogLevel.Critical => "\u001b[97;41m",
        _ => Reset,
    };
}
