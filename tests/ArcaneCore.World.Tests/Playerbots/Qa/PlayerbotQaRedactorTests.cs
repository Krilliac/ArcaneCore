using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>What must not leave the server: secrets, addresses, guids, names, user folders; and the shape of GitHub titles and bodies.</summary>
public sealed class PlayerbotQaRedactorTests
{
    private static string Redact(string text, params string[] names)
    {
        var redactor = new PlayerbotQaRedactor();
        redactor.SetNames(names);
        return redactor.Redact(text);
    }

    [Theory]
    [InlineData("ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8")]
    [InlineData("gho_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijkl_mnopqrstuvwxyz0123456789")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz0123456789")]
    public void TokenShapes_AreRemoved(string token)
    {
        string text = Redact($"failed with {token} in the middle");

        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.Contains("<token>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BearerHeader_IsRemoved()
    {
        string text = Redact("Authorization: Bearer abcdefgh12345678.xyz");

        Assert.DoesNotContain("abcdefgh12345678", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("api_key: s3cr3tvalue")]
    [InlineData("secret = topsecretvalue")]
    [InlineData("token=abc123def")]
    public void KeyValueSecrets_AreRemoved(string input)
    {
        string text = Redact("config " + input + " end");

        string value = input.Split(['=', ':'], 2)[1].Trim();
        Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        Assert.Contains("<redacted>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKnownToken_IsRemovedEvenWithoutAKnownShape()
    {
        const string token = "plain-secret-without-a-known-shape-123456";
        var redactor = new PlayerbotQaRedactor().WithToken(token);

        Assert.DoesNotContain(token, redactor.Redact("oops " + token + " oops"), StringComparison.Ordinal);
        Assert.DoesNotContain(token, PlayerbotQaRedactor.RedactSecrets("oops " + token, token), StringComparison.Ordinal);
        Assert.DoesNotContain(token, redactor.Body("oops " + token, "<!-- m -->"), StringComparison.Ordinal);
        Assert.DoesNotContain(token, redactor.Title("stall", "oops " + token), StringComparison.Ordinal);
        Assert.Contains(token, new PlayerbotQaRedactor().Redact("oops " + token), StringComparison.Ordinal); // only the redactor that knows it can: the guard is the literal match
    }

    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.250")]
    public void Ipv4_IsRemoved(string address)
    {
        string text = Redact($"connect from {address} ok, again {address}:8085");

        Assert.DoesNotContain(address, text, StringComparison.Ordinal);
        Assert.Contains("<ip>", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2001:db8:85a3:0:0:8a2e:370:7334")]
    [InlineData("2001:db8::8a2e:370:7334")]
    [InlineData("fe80::1")]
    public void Ipv6_IsRemoved(string address)
    {
        string text = Redact($"peer {address} gone");

        Assert.DoesNotContain(address, text, StringComparison.Ordinal);
        Assert.Contains("<ip>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Email_IsRemoved()
    {
        string text = Redact("report by someone.w+bots@example.co.uk today");

        Assert.DoesNotContain("someone", text, StringComparison.Ordinal);
        Assert.DoesNotContain("example.co.uk", text, StringComparison.Ordinal);
        Assert.Contains("<email>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Guids_AreRemoved()
    {
        string text = Redact($"Playerbot {QaData.Bot1} and {QaData.Bot2:N} stalled");

        Assert.DoesNotContain(QaData.Bot1.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<id>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownNames_AreRemoved_CaseInsensitively_AsWholeWordsOnly()
    {
        string text = Redact("Ironwander died; IRONWANDERER lives; ironwander again; (Graveweaver)", "Ironwander", "Graveweaver");

        Assert.DoesNotContain("Ironwander ", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Graveweaver", text, StringComparison.Ordinal);
        Assert.Equal("<name> died; IRONWANDERER lives; <name> again; (<name>)", text);
    }

    [Fact]
    public void ShortNames_AreIgnored_AndTheNameSetCanBeReplaced()
    {
        var redactor = new PlayerbotQaRedactor();
        redactor.SetNames(["ab", "Longname"]);
        Assert.Equal("ab saw <name>", redactor.Redact("ab saw Longname"));

        redactor.SetNames(["Other"]);
        Assert.Equal("Longname saw <name>", redactor.Redact("Longname saw Other"));
    }

    [Theory]
    [InlineData("C:\\Users\\someone\\AppData\\x.log", "someone")]
    [InlineData("C:/Users/someone/x.log", "someone")]
    [InlineData("/home/someone/x.log", "someone")]
    [InlineData("/Users/someone/x.log", "someone")]
    public void UserFolders_AreRemoved(string path, string user)
    {
        string text = Redact("opened " + path);

        Assert.DoesNotContain(user, text, StringComparison.Ordinal);
        Assert.Contains("<user>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GameFacts_AreKept()
    {
        const string line = "stalled 120s: goal=Quest target=6747 quest=1656 at -5608.8,-439.4,401.2 map 0";

        Assert.Equal(line, Redact(line));
    }

    [Fact]
    public void NullAndEmpty_GiveEmpty()
    {
        var redactor = new PlayerbotQaRedactor();

        Assert.Equal(string.Empty, redactor.Redact(null));
        Assert.Equal(string.Empty, redactor.Redact(string.Empty));
        Assert.Equal(string.Empty, PlayerbotQaRedactor.RedactSecrets(string.Empty));
    }

    [Fact]
    public void Title_HasThePrefix_TheKind_AndOnlySafeCharacters()
    {
        string title = new PlayerbotQaRedactor().Title("death loop", "quest 1656 <b>bold</b>\r\n`tick` | pipe");

        Assert.StartsWith("[bot-qa] death loop: quest 1656", title, StringComparison.Ordinal);
        Assert.DoesNotContain('<', title);
        Assert.DoesNotContain('`', title);
        Assert.DoesNotContain('|', title);
        Assert.DoesNotContain('\n', title);
        Assert.DoesNotContain('\r', title);
        Assert.DoesNotContain("  ", title, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_IsCutToTheLimit_AndRedacted()
    {
        var redactor = new PlayerbotQaRedactor();
        redactor.SetNames(["Ironwander"]);

        string title = redactor.Title("stall", "Ironwander at 203.0.113.9 " + new string('z', 500));

        Assert.True(title.Length <= PlayerbotQaRedactor.MaxTitleChars);
        Assert.EndsWith("...", title, StringComparison.Ordinal);
        Assert.DoesNotContain("Ironwander", title, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", title, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_NonAscii_BecomesAQuestionMark()
    {
        string title = new PlayerbotQaRedactor().Title("stall", "caf\u00e9 \u2603");

        Assert.Equal("[bot-qa] stall: caf? ?", title);
    }

    [Fact]
    public void Body_AddsTheMarkerWhenMissing_AndKeepsItWhenPresent()
    {
        const string marker = "<!-- arcanecore-bot-qa:aaaaaaaaaaaaaaaa -->";
        var redactor = new PlayerbotQaRedactor();

        Assert.StartsWith(marker + "\n", redactor.Body("plain", marker), StringComparison.Ordinal);
        string present = marker + "\ntext";
        Assert.Equal(present, redactor.Body(present, marker));
    }

    [Fact]
    public void Body_IsCutOnALineBoundary_AndStillStartsWithTheMarker()
    {
        const string marker = "<!-- arcanecore-bot-qa:aaaaaaaaaaaaaaaa -->";
        string huge = marker + "\n" + string.Join('\n', Enumerable.Repeat(new string('a', 99), 2000));

        string body = new PlayerbotQaRedactor().Body(huge, marker);

        Assert.True(body.Length <= PlayerbotQaRedactor.MaxBodyChars);
        Assert.StartsWith(marker, body, StringComparison.Ordinal);
        Assert.EndsWith("_(truncated)_\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_RemovesSecretsOnceMore()
    {
        const string marker = "<!-- m -->";

        string body = new PlayerbotQaRedactor().Body(marker + "\nleak " + QaData.FakeToken, marker);

        Assert.DoesNotContain(QaData.FakeToken, body, StringComparison.Ordinal);
    }
}
