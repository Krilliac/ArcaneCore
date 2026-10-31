using System.Globalization;
using System.Text;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Logging;

/// <summary>
/// Json:Path deprecation warning gate (no warning while the file sink is off) and the Unicode Format / vectorised escape scan of
/// <see cref="LogLineFormatter"/>.
/// </summary>
public sealed class LogSinkFollowupTests
{
    private const string Root = "Logging:ArcaneCore";

    private static ArcaneLoggingOptions Bind(params (string Key, string Value)[] pairs)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();
        var options = new ArcaneLoggingOptions();
        configuration.GetSection(Root).Bind(options);
        return options;
    }

    // ---- Json:Path warning gate ----

    [Fact]
    public void Resolve_FormatSet_FileDisabled_JsonPathSet_NoWarning()
    {
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(Bind((Root + ":Format", "Json"), (Root + ":Json:Path", "logs/legacy.jsonl")));

        Assert.False(plan.Enabled);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Resolve_FormatSet_FileEnabled_DifferentJsonPath_StillWarnsOnJsonPath()
    {
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(Bind(
            (Root + ":Format", "Text"), (Root + ":File:Enabled", "true"), (Root + ":File:Path", "logs/a.log"), (Root + ":Json:Path", "logs/b.jsonl")));

        Assert.True(plan.Enabled);
        LogFileSinkWarning warning = Assert.Single(plan.Warnings);
        Assert.Equal(Root + ":Json:Path", warning.Key);
    }

    [Fact]
    public void Resolve_FormatSet_JsonEnabled_WarnsOnJsonEnabled()
    {
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(Bind((Root + ":Format", "Json"), (Root + ":Json:Enabled", "true")));

        Assert.Equal(Root + ":Json:Enabled", Assert.Single(plan.Warnings).Key);
    }

    // ---- Unicode Format escaping ----

    private static string Escaped(string text)
    {
        var buffer = new LogBuffer(stackalloc char[16]);
        LogLineFormatter.AppendEscaped(ref buffer, text);
        string result = buffer.Written.ToString();
        buffer.Dispose();
        return result;
    }

    [Theory]
    [InlineData('\u202E', "\\u202E")]
    [InlineData('\u202D', "\\u202D")]
    [InlineData('\u200B', "\\u200B")]
    [InlineData('\uFEFF', "\\uFEFF")]
    public void AppendEscaped_EscapesUnicodeFormatCharacters(char c, string expected)
    {
        Assert.True(LogLineFormatter.NeedsEscape(c));
        Assert.Equal("a" + expected + "b", Escaped("a" + c + "b"));
    }

    [Theory]
    [InlineData('\u202E', "\\u202e")]
    [InlineData('\u200B', "\\u200b")]
    [InlineData('\uFEFF', "\\ufeff")]
    public void JsonWriteString_EscapesUnicodeFormatCharacters(char c, string expected)
    {
        var buffer = new LogBuffer(stackalloc char[16]);
        JsonLineFormatter.WriteString(ref buffer, "a" + c + "b");
        string result = buffer.Written.ToString();
        buffer.Dispose();
        Assert.Equal("\"a" + expected + "b\"", result);
    }

    [Fact]
    public void AppendEscaped_PlainAsciiAndOrdinaryUnicode_AreCopiedUnchanged()
    {
        const string text = "Hello, world! 0123 ~ caf\u00E9 \u4E2D\u6587 \U0001F600";
        Assert.Equal(text, Escaped(text));
        Assert.False(LogLineFormatter.NeedsEscape('a'));
        Assert.False(LogLineFormatter.NeedsEscape('\u00E9'));
    }

    [Fact]
    public void AppendEscaped_CleanText_Allocates_Nothing()
    {
        string text = new('x', 200);
        Span<char> storage = stackalloc char[512];
        var warm = new LogBuffer(storage);
        LogLineFormatter.AppendEscaped(ref warm, text);
        warm.Dispose();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            var buffer = new LogBuffer(storage);
            LogLineFormatter.AppendEscaped(ref buffer, text);
            buffer.Dispose();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- vectorised scan equals the per-char algorithm ----

    /// <summary>The original per-char loop, with the same escape rules, kept as the reference.</summary>
    private static string Reference(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (!(c < 0x20 || (c >= 0x7F && c <= 0x9F) || c == (char)0x2028 || c == (char)0x2029
                || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            {
                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)); break;
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void NeedsEscape_MatchesReferenceRule_ForEveryBmpChar()
    {
        for (int i = 0; i <= 0xFFFF; i++)
        {
            char c = (char)i;
            bool expected = c < 0x20 || (c >= 0x7F && c <= 0x9F) || c == (char)0x2028 || c == (char)0x2029
                || char.GetUnicodeCategory(c) == UnicodeCategory.Format;
            Assert.True(expected == LogLineFormatter.NeedsEscape(c), $"U+{i:X4}");
        }
    }

    [Fact]
    public void AppendEscaped_MatchesReference_OnMixedCorpus()
    {
        string[] corpus =
        [
            string.Empty,
            "plain",
            "\n",
            "x\u202E",
            "\u200Bstart",
            "mid\rdle\n\tend\u0001\u007F\u0085",
            "ls\u2028ps\u2029 bidi\u202A\u202B\u202C\u202D\u202E iso\u2066\u2067\u2068\u2069 bom\uFEFF shy\u00AD",
            "caf\u00E9 \u4E2D\u6587 \U0001F600 surrogate-pair \U0001F600 end",
            "lone high \uD83D then low \uDE00 x",
            string.Concat(Enumerable.Repeat("clean run of text ", 40)) + "\u200B" + string.Concat(Enumerable.Repeat("tail ", 40)),
        ];

        foreach (string text in corpus)
        {
            Assert.Equal(Reference(text), Escaped(text));
        }

        // every BMP char (surrogates excluded: copied as is by both) alone and between clean text
        for (int i = 0; i <= 0xFFFF; i++)
        {
            if (i is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            string text = "ab" + (char)i + "cd";
            Assert.True(Reference(text) == Escaped(text), $"U+{i:X4}");
        }
    }

    // ---- Truncated record shape ----

    private static readonly DateTime Stamp = new(2026, 10, 4, 12, 34, 56, 789, DateTimeKind.Utc);

    private static string WriteJson(in LogEvent evt)
    {
        var buffer = new LogBuffer(stackalloc char[64]);
        JsonLineFormatter.Write<object?>(ref buffer, in evt, null, null);
        string line = buffer.Written.ToString();
        buffer.Dispose();
        return line;
    }

    [Fact]
    public void JsonTruncated_ExceptionBulk_KeepsMessageUnmarkedAndNamesTheException()
    {
        var exception = new InvalidOperationException(new string('x', LogBuffer.MaxLength * 2));
        var evt = new LogEvent(Stamp, Microsoft.Extensions.Logging.LogLevel.Error, "Cat", new Microsoft.Extensions.Logging.EventId(7, "Boom"), "short message", exception);

        string line = WriteJson(in evt);

        Assert.EndsWith("}\n", line, StringComparison.Ordinal);
        Assert.Equal(1, line.Count(c => c == '\n'));
        using var doc = System.Text.Json.JsonDocument.Parse(line);
        System.Text.Json.JsonElement root = doc.RootElement;
        Assert.Equal("short message", root.GetProperty("message").GetString());
        Assert.Equal(7, root.GetProperty("eventId").GetInt32());
        Assert.Equal("Boom", root.GetProperty("eventName").GetString());
        Assert.Equal(typeof(InvalidOperationException).FullName, root.GetProperty("exceptionType").GetString());
        Assert.False(root.TryGetProperty("exception", out _));
    }

    [Fact]
    public void JsonTruncated_MessageCut_AppendsTheMarker()
    {
        var evt = new LogEvent(Stamp, Microsoft.Extensions.Logging.LogLevel.Information, "Cat", default, new string('a', LogBuffer.MaxLength * 2), null);

        string line = WriteJson(in evt);

        using var doc = System.Text.Json.JsonDocument.Parse(line);
        Assert.EndsWith(" ...[truncated]", doc.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(doc.RootElement.TryGetProperty("exceptionType", out _));
    }

    [Fact]
    public void JsonTruncated_AbsurdCategory_FallsBackToTheMinimalRecord()
    {
        var evt = new LogEvent(Stamp, Microsoft.Extensions.Logging.LogLevel.Information, new string('c', LogBuffer.MaxLength * 2), default, "m", null);

        Assert.Equal("{\"truncated\":true}\n", WriteJson(in evt));
    }

    [Fact]
    public void TextTruncated_ColourMode_EndsWithResetAndNewline()
    {
        var evt = new LogEvent(Stamp, Microsoft.Extensions.Logging.LogLevel.Information, "Cat", default, new string('a', LogBuffer.MaxLength * 2), null);
        var buffer = new LogBuffer(stackalloc char[64]);
        LogLineFormatter.Write(ref buffer, in evt, null, color: true);
        string line = buffer.Written.ToString();
        buffer.Dispose();

        Assert.EndsWith("\u001b[0m\n", line, StringComparison.Ordinal);
        Assert.Equal(1, line.Count(c => c == '\n'));
    }
}
