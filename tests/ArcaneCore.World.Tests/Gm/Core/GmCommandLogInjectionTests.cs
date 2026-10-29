using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Tests.Characters.Creation;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>
/// Log-line hardening (CWE-117) for the GM command audit line: command text typed in chat reaches the log, so control characters in it
/// must come out as visible escapes, never as a raw line break or ANSI escape.
/// </summary>
public sealed class GmCommandLogInjectionTests
{
    private const string Hostile = "a\rb\nc\u0001d\u001b[31me\u2028f\u2029g";

    [Fact]
    public void Describe_EscapesControlCharactersInCommandAndPlayerName()
    {
        string line = GmCommandLog.Describe("kick " + Hostile, 7, "Gm\rName", 1, 1f, 2f, 3f, "sel\nx");

        Assert.All(line, c => Assert.False(LogLineFormatter.NeedsEscape(c), $"raw control character U+{(int)c:X4}"));
        Assert.Contains("kick a\\rb\\nc\\u0001d\\u001B[31me\\u2028f\\u2029g", line, StringComparison.Ordinal);
        Assert.Contains("Player: Gm\\rName", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeConsole_EscapesControlCharacters()
    {
        string line = GmCommandLog.DescribeConsole("kick " + Hostile);

        Assert.All(line, c => Assert.False(LogLineFormatter.NeedsEscape(c), $"raw control character U+{(int)c:X4}"));
    }

    [Fact]
    public async Task ChatCommandWithControlCharacters_IsLoggedOnOneCleanLine()
    {
        var logs = new LogCapture();
        await using var host = WorldTestHost.Start(configureServices: logs.Register);
        await using WorldTestClient admin = await host.EnterWorldAsync("CRADM", "Craadm", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server config get autosaveintervalms\rINFO forged\u001b[31m\u2028tail\u0001");
        await admin.CollectRepliesAsync(TimeSpan.FromMilliseconds(800));

        string[] audit = [.. logs.Lines.Where(l => l.Contains(GmCommandLog.Category, StringComparison.Ordinal))];
        Assert.NotEmpty(audit);
        Assert.Contains(audit, l => l.Contains("Command: server config get", StringComparison.Ordinal));
        Assert.All(audit, l => Assert.All(l, c => Assert.False(LogLineFormatter.NeedsEscape(c), $"raw control character U+{(int)c:X4} in: {l}")));
    }
}
