using ArcaneCore.AccountTool;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>AC-PI-003: password source selection for arcane-account create / set-password.</summary>
public sealed class AccountToolPasswordSourceTests
{
    private static string? NoEnv(string _) => null;

    private static string? Env(string name) => name == PasswordSource.EnvironmentVariable ? "from-env" : null;

    [Fact]
    public void LegacyArgv_StillWorks_AndIsFlagged()
    {
        PasswordRequest r = PasswordSource.Parse(["create", "bob", "hunter2"], NoEnv, stdinRedirected: false);
        Assert.True(r.IsValid);
        Assert.Equal(PasswordMode.Argv, r.Mode);
        Assert.Equal("hunter2", r.ArgvPassword);
        Assert.Equal("bob", r.Username);
        Assert.Contains("command line", PasswordSource.ArgvWarning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StdinFlag_Wins_OverEnvironmentAndTerminalState(bool redirected)
    {
        PasswordRequest r = PasswordSource.Parse(["set-password", "bob", "--password-stdin"], Env, redirected);
        Assert.True(r.IsValid);
        Assert.Equal(PasswordMode.Stdin, r.Mode);
        Assert.Null(r.ArgvPassword);
    }

    [Fact]
    public void StdinFlag_BeforeUsername_IsAccepted()
    {
        PasswordRequest r = PasswordSource.Parse(["create", "--password-stdin", "bob"], NoEnv, false);
        Assert.Equal(("bob", PasswordMode.Stdin), (r.Username, r.Mode));
    }

    [Fact]
    public void StdinFlag_WithArgvPassword_IsAnError()
    {
        Assert.False(PasswordSource.Parse(["create", "bob", "pw", "--password-stdin"], NoEnv, false).IsValid);
    }

    [Fact]
    public void Environment_IsUsed_WhenNoArgvAndNoFlag()
    {
        PasswordRequest r = PasswordSource.Parse(["create", "bob"], Env, stdinRedirected: false);
        Assert.Equal(PasswordMode.Environment, r.Mode);
        Assert.Equal("from-env", PasswordSource.Read(r, Env, new StringReader(""), _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void PipedStdin_IsImplicit_WithoutFlag()
    {
        PasswordRequest r = PasswordSource.Parse(["create", "bob"], NoEnv, stdinRedirected: true);
        Assert.Equal(PasswordMode.Stdin, r.Mode);
        Assert.Equal("pw 1", PasswordSource.Read(r, NoEnv, new StringReader("pw 1\r\nrest"), _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void Terminal_UsesNoEchoPrompt_AndRequiresMatchingConfirmation()
    {
        PasswordRequest r = PasswordSource.Parse(["create", "bob"], NoEnv, stdinRedirected: false);
        Assert.Equal(PasswordMode.Prompt, r.Mode);

        var answers = new Queue<string>(["secret", "secret"]);
        Assert.Equal("secret", PasswordSource.Read(r, NoEnv, TextReader.Null, _ => answers.Dequeue()));

        var mismatch = new Queue<string>(["secret", "other"]);
        Assert.Null(PasswordSource.Read(r, NoEnv, TextReader.Null, _ => mismatch.Dequeue()));
    }

    [Fact]
    public void EmptyStdin_YieldsNoPassword()
    {
        PasswordRequest r = PasswordSource.Parse(["create", "bob", "--password-stdin"], NoEnv, true);
        Assert.Null(PasswordSource.Read(r, NoEnv, new StringReader(""), _ => null));
        Assert.Null(PasswordSource.Read(r, NoEnv, new StringReader("\n"), _ => null));
    }

    [Fact]
    public void WrongArity_IsAnError()
    {
        foreach (string[] args in new[] { new[] { "create" }, new[] { "create", "a", "b", "c" } })
        {
            PasswordRequest r = PasswordSource.Parse(args, NoEnv, false);
            Assert.False(r.IsValid);
            Assert.Contains("usage", r.Error);
        }
    }
}
