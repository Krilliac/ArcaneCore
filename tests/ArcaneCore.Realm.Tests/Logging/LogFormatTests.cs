using System.Text.Json;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Logging;

/// <summary>The text and JSON line formats of the ArcaneCore logging provider, and the colour decision.</summary>
public sealed class LogFormatTests
{
    private static readonly DateTime Stamp = new(2026, 10, 4, 12, 34, 56, 789, DateTimeKind.Utc);

    private static ArcaneLoggingOptions Options(ConsoleMode mode = ConsoleMode.Plain, bool scopes = true)
    {
        var options = new ArcaneLoggingOptions { IncludeScopes = scopes };
        options.Console.Mode = mode;
        return options;
    }

    /// <summary>A factory whose only provider writes to <paramref name="output"/> synchronously (the console sink).</summary>
    private static (ILoggerFactory Factory, ArcaneLoggerProvider Provider) Factory(StringWriter output, ConsoleMode mode, bool colorAllowed, bool scopes = true, LogSink[]? extra = null)
    {
        LogSink[] sinks = [new TextLogSink("console", new TextWriterLineWriter(output), color: false), .. extra ?? []];
        var provider = new ArcaneLoggerProvider(Options(mode, scopes), colorAllowed, sinks);
        ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        return (factory, provider);
    }

    [Fact]
    public void PlainLine_HasFixedColumns_NoEscapeCodes()
    {
        var buffer = new LogBuffer(stackalloc char[64]);
        var evt = new LogEvent(Stamp, LogLevel.Information, "ArcaneCore.World.Net.WorldServer", new EventId(7, "Perf"), "Listening on 0.0.0.0:8085", null);
        LogLineFormatter.Write(ref buffer, in evt, null, color: false);
        string line = buffer.Written.ToString();
        buffer.Dispose();

        Assert.Equal("2026-10-04 12:34:56.789 INFO ArcaneCore.World.Net.WorldServer[Perf:7]: Listening on 0.0.0.0:8085\n", line);
        Assert.DoesNotContain('\u001b', line);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRCE")]
    [InlineData(LogLevel.Debug, "DBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARN")]
    [InlineData(LogLevel.Error, "FAIL")]
    [InlineData(LogLevel.Critical, "CRIT")]
    public void LevelColumn_IsFourCharacters(LogLevel level, string text)
    {
        Assert.Equal(text, LogLineFormatter.LevelText(level));
        Assert.Equal(4, text.Length);
    }

    [Fact]
    public void ColorLine_StripsToThePlainLine()
    {
        var evt = new LogEvent(Stamp, LogLevel.Warning, "Cat", new EventId(3), "careful", null);
        var plain = new LogBuffer(stackalloc char[64]);
        LogLineFormatter.Write(ref plain, in evt, null, color: false);
        var color = new LogBuffer(stackalloc char[64]);
        LogLineFormatter.Write(ref color, in evt, null, color: true);
        string colored = color.Written.ToString();
        string stripped = System.Text.RegularExpressions.Regex.Replace(colored, "\u001b\\[[0-9;]*m", string.Empty);

        Assert.Contains("\u001b[33mWARN\u001b[0m", colored, StringComparison.Ordinal);
        Assert.Equal(plain.Written.ToString(), stripped);
        plain.Dispose();
        color.Dispose();
    }

    [Fact]
    public void Exception_IsIndentedOnFollowingLines()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        var buffer = new LogBuffer(stackalloc char[64]);
        var evt = new LogEvent(Stamp, LogLevel.Error, "Cat", default, "failed", exception);
        LogLineFormatter.Write(ref buffer, in evt, null, color: false);
        string[] lines = buffer.Written.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        buffer.Dispose();

        Assert.Equal("2026-10-04 12:34:56.789 FAIL Cat: failed", lines[0]);
        Assert.StartsWith("      System.InvalidOperationException: boom", lines[1], StringComparison.Ordinal);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("      ", l, StringComparison.Ordinal));
    }

    [Fact]
    public void Scopes_AreAppendedAfterTheCategory_AndOmittedWhenDisabled()
    {
        var withScopes = new StringWriter();
        (ILoggerFactory factory, ArcaneLoggerProvider provider) = Factory(withScopes, ConsoleMode.Plain, colorAllowed: false);
        using (factory)
        using (provider)
        {
            ILogger logger = factory.CreateLogger("Cat");
            using (logger.BeginScope("session 42"))
            using (logger.BeginScope("account {Account}", "TESTER"))
            {
                logger.LogInformation("hello");
            }

            logger.LogInformation("outside");
        }

        string[] lines = withScopes.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(" INFO Cat => session 42 => Account=TESTER: hello", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(" INFO Cat: outside", lines[1], StringComparison.Ordinal);

        var noScopes = new StringWriter();
        (factory, provider) = Factory(noScopes, ConsoleMode.Plain, colorAllowed: false, scopes: false);
        using (factory)
        using (provider)
        {
            ILogger logger = factory.CreateLogger("Cat");
            using (logger.BeginScope("session 42"))
            {
                logger.LogInformation("hello");
            }
        }

        Assert.EndsWith(" INFO Cat: hello\n", noScopes.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConsoleMode_Color_RendersColorOnlyWhenTheEnvironmentAllows()
    {
        var colored = new StringWriter();
        (ILoggerFactory factory, ArcaneLoggerProvider provider) = Factory(colored, ConsoleMode.Color, colorAllowed: true);
        using (factory)
        using (provider)
        {
            Assert.True(provider.ConsoleColor);
            factory.CreateLogger("Cat").LogError("red");
        }

        Assert.Contains("\u001b[31mFAIL", colored.ToString(), StringComparison.Ordinal);

        var redirected = new StringWriter();
        (factory, provider) = Factory(redirected, ConsoleMode.Color, colorAllowed: false);
        using (factory)
        using (provider)
        {
            Assert.False(provider.ConsoleColor);
            factory.CreateLogger("Cat").LogError("red");
        }

        Assert.DoesNotContain('\u001b', redirected.ToString());
    }

    [Fact]
    public void ColorDecision_OffWhenRedirected_NoColor_OrPlainMode_OrTerminalRefuses()
    {
        Assert.True(ConsoleColorSupport.Resolve(ConsoleMode.Color, outputRedirected: false, noColor: null, static () => true));
        Assert.True(ConsoleColorSupport.Resolve(ConsoleMode.Color, outputRedirected: false, noColor: string.Empty, static () => true));
        Assert.False(ConsoleColorSupport.Resolve(ConsoleMode.Color, outputRedirected: true, noColor: null, static () => true));
        Assert.False(ConsoleColorSupport.Resolve(ConsoleMode.Color, outputRedirected: false, noColor: "1", static () => true));
        Assert.False(ConsoleColorSupport.Resolve(ConsoleMode.Color, outputRedirected: false, noColor: null, static () => false));
        bool terminalAsked = false;
        Assert.False(ConsoleColorSupport.Resolve(ConsoleMode.Plain, outputRedirected: false, noColor: null, () => terminalAsked = true));
        Assert.False(ConsoleColorSupport.Resolve(ConsoleMode.Off, outputRedirected: false, noColor: null, () => terminalAsked = true));
        Assert.False(terminalAsked);
        // this test process has stdout redirected by the runner, or a terminal; either way the real probe must not throw
        _ = ConsoleColorSupport.Resolve(ConsoleMode.Color);
    }

    [Fact]
    public void ConsoleMode_Off_WritesNothing()
    {
        var output = new StringWriter();
        (ILoggerFactory factory, ArcaneLoggerProvider provider) = Factory(output, ConsoleMode.Off, colorAllowed: true);
        using (factory)
        using (provider)
        {
            factory.CreateLogger("Cat").LogCritical("silence");
        }

        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void JsonLine_IsOneValidObject_WithStructuredState()
    {
        var output = new StringWriter();
        var json = new JsonLogSink("json", new TextWriterLineWriter(output));
        (ILoggerFactory factory, ArcaneLoggerProvider provider) = Factory(new StringWriter(), ConsoleMode.Off, colorAllowed: false, extra: [json]);
        using (factory)
        using (provider)
        {
            ILogger logger = factory.CreateLogger("ArcaneCore.Realm.Net.LogonServer");
            using (logger.BeginScope("peer 10.0.0.1"))
            {
                logger.LogWarning(new EventId(12, "Auth"), "Account {Account} failed {Attempts} times (ok={Ok}, ratio={Ratio}) \"quoted\"\n", "TES\tTER", 3, true, 0.5);
            }
        }

        string line = output.ToString();
        Assert.EndsWith("\n", line, StringComparison.Ordinal);
        Assert.Single(line.TrimEnd('\n').Split('\n'));
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        Assert.Equal("Warning", root.GetProperty("level").GetString());
        Assert.Equal("ArcaneCore.Realm.Net.LogonServer", root.GetProperty("category").GetString());
        Assert.Equal(12, root.GetProperty("eventId").GetInt32());
        Assert.Equal("Auth", root.GetProperty("eventName").GetString());
        Assert.Equal("Account TES\tTER failed 3 times (ok=True, ratio=0.5) \"quoted\"\n", root.GetProperty("message").GetString());
        Assert.Equal("Account {Account} failed {Attempts} times (ok={Ok}, ratio={Ratio}) \"quoted\"\n", root.GetProperty("template").GetString());
        JsonElement props = root.GetProperty("props");
        Assert.Equal("TES\tTER", props.GetProperty("Account").GetString());
        Assert.Equal(3, props.GetProperty("Attempts").GetInt32());
        Assert.True(props.GetProperty("Ok").GetBoolean());
        Assert.Equal(0.5, props.GetProperty("Ratio").GetDouble());
        Assert.Equal("peer 10.0.0.1", root.GetProperty("scopes")[0].GetString());
        Assert.EndsWith("Z", root.GetProperty("ts").GetString(), StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("exception", out _));
    }

    [Fact]
    public void JsonString_EscapesControlCharacters_SoAClientCannotBreakTheLine()
    {
        var buffer = new LogBuffer(stackalloc char[64]);
        JsonLineFormatter.WriteString(ref buffer, "a\"b\\c\u0001d\u001be\r\n");
        string text = buffer.Written.ToString();
        buffer.Dispose();
        Assert.Equal("\"a\\\"b\\\\c\\u0001d\\u001be\\r\\n\"", text);
        Assert.Equal("a\"b\\c\u0001d\u001be\r\n", JsonDocument.Parse(text).RootElement.GetString());
    }

    [Fact]
    public void JsonTimestamp_LocalCarriesAnOffset()
    {
        var buffer = new LogBuffer(stackalloc char[64]);
        JsonLineFormatter.WriteTimestamp(ref buffer, new DateTime(2026, 1, 2, 3, 4, 5, 6, DateTimeKind.Local));
        string text = buffer.Written.ToString();
        buffer.Dispose();
        Assert.Matches("^2026-01-02T03:04:05\\.006[+-]\\d\\d:\\d\\d$", text);
    }

    [Fact]
    public void LogBuffer_GrowsPastTheStackAndTruncatesAtTheCap()
    {
        var buffer = new LogBuffer(stackalloc char[8]);
        buffer.Append("0123456789");
        buffer.Append('x');
        buffer.Append(1234567890123L);
        Assert.Equal("0123456789x1234567890123", buffer.Written.ToString());
        Assert.False(buffer.Truncated);

        buffer.Append(new string('a', LogBuffer.MaxLength));
        Assert.True(buffer.Truncated);
        Assert.True(buffer.Length <= LogBuffer.MaxLength);
        Assert.EndsWith(" ...[truncated]", buffer.Written.ToString(), StringComparison.Ordinal);
        int length = buffer.Length;
        buffer.Append("ignored");
        Assert.Equal(length, buffer.Length);
        buffer.Dispose();
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void FailingSink_IsCountedAndSkipped_NeverThrowsToTheCaller()
    {
        var output = new StringWriter();
        var broken = new TextLogSink("file", new ThrowingWriter(), color: false);
        (ILoggerFactory factory, ArcaneLoggerProvider provider) = Factory(output, ConsoleMode.Plain, colorAllowed: false, extra: [broken]);
        using (factory)
        using (provider)
        {
            factory.CreateLogger("Cat").LogInformation("still here");
        }

        Assert.Equal(1, broken.Faults);
        Assert.Contains("still here", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class ThrowingWriter : ILineWriter
    {
        public void Write(ReadOnlySpan<char> line) => throw new IOException("disk gone");

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
