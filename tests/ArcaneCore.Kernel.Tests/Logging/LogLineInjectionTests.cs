using System.Text.Json;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Logging;

/// <summary>
/// Log-line hardening (CWE-117): whatever text reaches a message, category, event name or exception, the text and JSON sinks emit
/// exactly one line, with no raw line break, control character or ANSI escape inside it.
/// </summary>
public sealed class LogLineInjectionTests
{
    private static readonly DateTime Stamp = new(2026, 10, 4, 12, 34, 56, 789, DateTimeKind.Utc);

    /// <summary>CR, LF, CRLF, other C0 controls, ESC, DEL, C1 (NEL), LS and PS.</summary>
    private static readonly string Hostile =
        "ok\rINFO forged\nline\r\nx\u0001\u0007\u001b[31mred\u007f\u0085" + (char)0x2028 + (char)0x2029 + "end";

    private static string Text(LogEvent evt, bool color = false)
    {
        var buffer = new LogBuffer(stackalloc char[64]);
        LogLineFormatter.Write(ref buffer, in evt, null, color);
        string line = buffer.Written.ToString();
        buffer.Dispose();
        return line;
    }

    private static string Json(LogEvent evt)
    {
        var buffer = new LogBuffer(stackalloc char[64]);
        JsonLineFormatter.Write<object?>(ref buffer, in evt, null, null);
        string line = buffer.Written.ToString();
        buffer.Dispose();
        return line;
    }

    private static void AssertOneCleanLine(string line)
    {
        Assert.EndsWith("\n", line, StringComparison.Ordinal);
        string body = line[..^1];
        foreach (char c in body)
        {
            Assert.False(LogLineFormatter.NeedsEscape(c), $"raw control character U+{(int)c:X4} in a log line");
        }
    }

    [Fact]
    public void TextSink_EscapesControlsInMessage()
    {
        string line = Text(new LogEvent(Stamp, LogLevel.Information, "Cat", default, Hostile, null));

        AssertOneCleanLine(line);
        Assert.Contains("ok\\rINFO forged\\nline\\r\\nx\\u0001\\u0007\\u001B[31mred\\u007F\\u0085\\u2028\\u2029end", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TextSink_EscapesCategoryAndEventName()
    {
        string line = Text(new LogEvent(Stamp, LogLevel.Warning, "Cat\rX", new EventId(3, "Ev\nY"), "m", null));

        AssertOneCleanLine(line);
        Assert.Contains("Cat\\rX[Ev\\nY:3]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TextSink_EscapesControlsInsideAnExceptionLine()
    {
        var exception = new InvalidOperationException("first\rsecond\u001b[0m");
        string line = Text(new LogEvent(Stamp, LogLevel.Error, "Cat", default, "failed", exception));

        foreach (string part in line.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            AssertOneCleanLine(part + "\n");
        }

        Assert.Contains("first\\rsecond\\u001B[0m", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TextSink_CleanTextIsUnchanged_AndColourCodesSurvive()
    {
        string plain = Text(new LogEvent(Stamp, LogLevel.Warning, "Cat", default, "café 中文 ok", null));
        string colored = Text(new LogEvent(Stamp, LogLevel.Warning, "Cat", default, "café 中文 ok", null), color: true);

        Assert.Equal("2026-10-04 12:34:56.789 WARN Cat: café 中文 ok\n", plain);
        Assert.Contains("\u001b[33mWARN\u001b[0m", colored, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonSink_StaysValidAndEscapesEveryControl()
    {
        string line = Json(new LogEvent(Stamp, LogLevel.Information, "Cat", default, Hostile, null));

        AssertOneCleanLine(line);
        using JsonDocument doc = JsonDocument.Parse(line);
        Assert.Equal(Hostile, doc.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("\\\\r", line, StringComparison.Ordinal); // not double-escaped
    }
}
