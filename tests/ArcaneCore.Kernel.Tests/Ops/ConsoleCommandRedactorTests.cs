using ArcaneCore.Kernel.Ops.ServerConsole;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>The console's log redaction: secrets typed at the console must never reach a log file (docs/areas/server-console.md).</summary>
public sealed class ConsoleCommandRedactorTests
{
    [Theory]
    [InlineData("account create Alice hunter2", "account create Alice ***")]
    [InlineData(".account create Alice hunter2", ".account create Alice ***")]
    [InlineData("  !ACCOUNT  CREATE  Alice   hunter2  ", "  !ACCOUNT  CREATE  Alice   ***  ")]
    [InlineData("account set-password Alice hunter2", "account set-password Alice ***")]
    [InlineData("account setpassword Alice hunter2", "account setpassword Alice ***")]
    [InlineData("account set password Alice hunter2", "account set password Alice ***")]
    [InlineData("account password hunter2 hunter2", "account password *** ***")]
    [InlineData("account create Alice two words", "account create Alice *** ***")]
    public void Redact_MasksPasswordArguments_OfAccountCommands(string line, string expected)
    {
        string redacted = ConsoleCommandRedactor.Redact(line);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("announce password hunter2", "announce password ***")]
    [InlineData("login pass hunter2 now", "login pass *** now")]
    [InlineData("set secret hunter2", "set secret ***")]
    [InlineData("account lock pin 1234", "account lock pin ***")]
    [InlineData("account lock totp JBSWY3DP", "account lock totp ***")]
    [InlineData("SET PASSWORD hunter2", "SET PASSWORD ***")]
    [InlineData("a password x b password y", "a password *** b password ***")]
    public void Redact_MasksTheTokenAfterAnySecretKeyword(string line, string expected)
        => Assert.Equal(expected, ConsoleCommandRedactor.Redact(line));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("server info")]
    [InlineData("levelup Alice 5")]
    [InlineData("account create Alice")]      // no password typed: nothing to mask
    [InlineData("account set-gmlevel Alice 3")]
    [InlineData("kick passport")]             // a keyword only counts as a whole word
    [InlineData("announce the password")]     // keyword with nothing after it
    [InlineData("announce impassword x")]
    [InlineData("account")]
    public void Redact_LeavesOrdinaryLinesUnchanged(string line)
        => Assert.Equal(line, ConsoleCommandRedactor.Redact(line));

    [Fact]
    public void Redact_RejectsNull()
        => Assert.Throws<ArgumentNullException>(() => ConsoleCommandRedactor.Redact(null!));
}
