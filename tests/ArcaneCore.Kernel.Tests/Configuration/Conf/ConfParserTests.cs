using System.Text;
using ArcaneCore.Kernel.Configuration.Conf;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Conf;

public sealed class ConfParserTests
{
    private static ConfDocument Parse(string text) => ConfDocument.ParseText(text, "world.conf");

    private static string[] Errors(string text) => Assert.Throws<ConfParseException>(() => Parse(text)).Errors.ToArray();

    [Fact]
    public void Parse_KeyValueLines_CommentsAndBlankLines_AreIgnored()
    {
        ConfDocument document = Parse("# a comment\n; another\n\n   \nWorld.Port = 8085\r\n  World.Name=ArcaneCore  \n");
        Assert.Collection(
            document.Entries,
            e =>
            {
                Assert.Equal("World.Port", e.Key);
                Assert.Equal("8085", e.Value);
                Assert.Equal(5, e.Line);
                Assert.False(e.Quoted);
            },
            e =>
            {
                Assert.Equal("ArcaneCore", e.Value);
                Assert.Equal(6, e.Line);
            });
    }

    [Fact]
    public void Parse_DottedKey_MapsToColonPath_AndColonKeyIsTakenVerbatim()
    {
        ConfDocument document = Parse("World.Playerbots.MaxBots = 5\nLogging:LogLevel:Microsoft.EntityFrameworkCore = Warning\n");
        Assert.Equal("World:Playerbots:MaxBots", document.Entries[0].Path);
        Assert.Equal("Logging:LogLevel:Microsoft.EntityFrameworkCore", document.Entries[1].Path);
    }

    [Fact]
    public void Parse_QuotedValue_UnescapesOnlyQuoteAndBackslash()
    {
        ConfDocument document = Parse("""
            A = "C:\dir\file \"q\" \\x"
            B = ""
            C = "  padded  "
            D = "a = b"
            """);
        Assert.Equal("C:\\dir\\file \"q\" \\x", document.Entries[0].Value);
        Assert.True(document.Entries[0].Quoted);
        Assert.Equal(string.Empty, document.Entries[1].Value);
        Assert.Equal("  padded  ", document.Entries[2].Value);
        Assert.Equal("a = b", document.Entries[3].Value);
    }

    [Fact]
    public void Parse_UnquotedValue_KeepsHashAndSemicolonLiterally()
    {
        ConfDocument document = Parse("Database.ConnectionString = Server=db;Password=a#b;Port=3306 # not a comment\nEmpty =\n");
        Assert.Equal("Server=db;Password=a#b;Port=3306 # not a comment", document.Entries[0].Value);
        Assert.Equal(string.Empty, document.Entries[1].Value);
    }

    [Fact]
    public void Parse_IndexedKeys_BecomeConfigurationArrayPaths()
    {
        ConfDocument document = Parse("Realms.Seed.0.Name = A\nRealms.Seed.1.Name = B\nauth.autopatch.patches.0.build = 5464\n");
        Assert.Equal(["Realms:Seed:0:Name", "Realms:Seed:1:Name", "auth:autopatch:patches:0:build"], document.Entries.Select(e => e.Path));
    }

    [Fact]
    public void Parse_Utf8Bom_AndNonAsciiValues_RoundTrip()
    {
        byte[] bytes = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("Name = Zażółć ü\nOther = x\n")];
        ConfDocument document = ConfDocument.Parse(bytes, "world.conf");
        Assert.Equal("Name", document.Entries[0].Key);
        Assert.Equal("Zażółć ü", document.Entries[0].Value);
        Assert.Equal(2, document.Entries[1].Line);

        ConfDocument unquoted = ConfDocument.Parse(Encoding.UTF8.GetBytes("Name = Zażółć ü\n"), "world.conf");
        Assert.Equal("Zażółć ü", unquoted.Entries[0].Value);
    }

    [Theory]
    [InlineData("World.Port 8085", "no '='")]
    [InlineData(" = 5", "key is empty")]
    [InlineData("World Port = 5", "whitespace")]
    [InlineData("World..Port = 5", "empty segment")]
    [InlineData(".World = 5", "empty segment")]
    [InlineData("World.Name = \"abc", "not closed")]
    [InlineData("World.Name = \"abc\" tail", "after the closing quote")]
    [InlineData("[World]", "section headers")]
    public void Parse_MalformedLine_FailsWithFileAndLine(string line, string expected)
    {
        string[] errors = Errors("# header\n" + line + "\n");
        string error = Assert.Single(errors);
        Assert.StartsWith("world.conf:2: ", error, StringComparison.Ordinal);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DuplicateKey_CaseInsensitiveAndAcrossDotAndColonSpellings_FailsWithBothLineNumbers()
    {
        string[] errors = Errors("World.Port = 1\nOther = 2\nworld:port = 3\nWORLD.PORT = 4\n");
        Assert.Equal(2, errors.Length);
        Assert.Contains("world.conf:3:", errors[0], StringComparison.Ordinal);
        Assert.Contains("line 1", errors[0], StringComparison.Ordinal);
        Assert.Contains("world.conf:4:", errors[1], StringComparison.Ordinal);
        Assert.Contains("line 1", errors[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EveryProblem_IsReportedAtOnce()
    {
        ConfParseException ex = Assert.Throws<ConfParseException>(() => Parse("A = 1\nbroken line\nA = 2\nB = \"open\n[S]\n"));
        Assert.Equal(4, ex.Errors.Count);
        Assert.Contains("world.conf:2:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("world.conf:3:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("world.conf:4:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("world.conf:5:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_InvalidUtf8_FailsWithLine()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("A = 1\nB = "), 0xFF, 0xFE, .. Encoding.UTF8.GetBytes("\nC = 3\n")];
        ConfParseException ex = Assert.Throws<ConfParseException>(() => ConfDocument.Parse(bytes, "world.conf"));
        string error = Assert.Single(ex.Errors);
        Assert.StartsWith("world.conf:2: ", error, StringComparison.Ordinal);
        Assert.Contains("UTF-8", error, StringComparison.Ordinal);
    }
}
