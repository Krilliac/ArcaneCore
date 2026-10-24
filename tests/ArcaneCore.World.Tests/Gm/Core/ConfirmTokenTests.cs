using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>The confirm tokens of destructive commands (plan I7): short, random, single use, 60 seconds, bound to the invoker, action and arguments.</summary>
public sealed class ConfirmTokenTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    [Fact]
    public void ACode_IsShortRandomAndUsesTheSafeAlphabet()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        HashSet<string> seen = [];
        for (int i = 0; i < 200; i++) seen.Add(store.Issue("Console", "a", "x").Code);

        Assert.True(seen.Count > 190, "codes are random");
        Assert.All(seen, code =>
        {
            Assert.Equal(ConfirmTokenStore.CodeLength, code.Length);
            Assert.All(code, c => Assert.True(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)));
            Assert.DoesNotContain(code, c => "IL01O".Contains(c));
        });
    }

    [Fact]
    public void TheLifetime_IsSixtySeconds()
    {
        var clock = new ManualClock();
        var store = new ConfirmTokenStore(clock);
        ConfirmToken token = store.Issue("Gm", "a", "x");

        Assert.Equal(TimeSpan.FromSeconds(60), ConfirmTokenStore.Lifetime);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 12, 1, 0, TimeSpan.Zero), token.Expires);
    }

    [Fact]
    public void TheCode_IsAcceptedOnce_IgnoringCaseAndBlanks()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string code = store.Issue("Gm", "unload", "map 1").Code;

        Assert.Equal(ConfirmResult.Accepted, store.TryRedeem("Gm", "unload", "map 1", "  " + code.ToLowerInvariant() + " "));
        Assert.Equal(ConfirmResult.NoPreview, store.TryRedeem("Gm", "unload", "map 1", code));
    }

    [Fact]
    public void ACode_ExpiresAfterSixtySeconds_AndIsGone()
    {
        var clock = new ManualClock();
        var store = new ConfirmTokenStore(clock);
        string code = store.Issue("Gm", "a", "x").Code;
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, store.PendingCount);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, store.PendingCount);
        Assert.Equal(ConfirmResult.Expired, store.TryRedeem("Gm", "a", "x", code));
        Assert.Equal(ConfirmResult.NoPreview, store.TryRedeem("Gm", "a", "x", code));
    }

    [Fact]
    public void ACode_IsBoundToItsInvoker_AndItsAction()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string code = store.Issue("GmOne", "unload", "x").Code;

        Assert.Equal(ConfirmResult.NoPreview, store.TryRedeem("GmTwo", "unload", "x", code));
        Assert.Equal(ConfirmResult.NoPreview, store.TryRedeem("GmOne", "purge", "x", code));
        Assert.Equal(ConfirmResult.Accepted, store.TryRedeem("GmOne", "unload", "x", code));
    }

    [Fact]
    public void ACode_IsBoundToTheArgumentsItWasShown()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string code = store.Issue("Gm", "unload", "map 1").Code;

        Assert.Equal(ConfirmResult.Mismatch, store.TryRedeem("Gm", "unload", "map 2", code));
        Assert.Equal(ConfirmResult.Accepted, store.TryRedeem("Gm", "unload", "map 1", code));
    }

    [Fact]
    public void WrongCodes_AreCounted_ThenThePreviewIsGone()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string code = store.Issue("Gm", "a", "x").Code;

        for (int i = 0; i < ConfirmTokenStore.MaxWrongAttempts - 1; i++) Assert.Equal(ConfirmResult.WrongCode, store.TryRedeem("Gm", "a", "x", "ZZZZZZ"));
        Assert.Equal(ConfirmResult.TooManyAttempts, store.TryRedeem("Gm", "a", "x", "ZZZZZZ"));
        Assert.Equal(ConfirmResult.NoPreview, store.TryRedeem("Gm", "a", "x", code));
    }

    [Fact]
    public void ACodeOfTheWrongLength_IsWrong_NotAnError()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        store.Issue("Gm", "a", "x");

        Assert.Equal(ConfirmResult.WrongCode, store.TryRedeem("Gm", "a", "x", "AB"));
        Assert.Equal(ConfirmResult.WrongCode, store.TryRedeem("Gm", "a", "x", string.Empty));
    }

    [Fact]
    public void ANewIssue_ReplacesThePending_ForTheSameInvokerAndAction_ButNotForOthers()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string first = store.Issue("Gm", "a", "x").Code;
        string other = store.Issue("Gm", "b", "x").Code;
        string second = store.Issue("Gm", "a", "x").Code;

        Assert.Equal(2, store.PendingCount);
        if (first != second) Assert.Equal(ConfirmResult.WrongCode, store.TryRedeem("Gm", "a", "x", first));
        Assert.Equal(ConfirmResult.Accepted, store.TryRedeem("Gm", "a", "x", second));
        Assert.Equal(ConfirmResult.Accepted, store.TryRedeem("Gm", "b", "x", other));
    }

    [Fact]
    public void Clear_DropsThePreview()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string code = store.Issue("Gm", "a", "x").Code;
        store.Clear("Gm", "a");

        Assert.Equal(ConfirmResult.NoPreview, store.TryRedeem("Gm", "a", "x", code));
    }

    [Fact]
    public void ThePendingSet_IsBounded_AndExpiredEntriesAreSwept()
    {
        var clock = new ManualClock();
        var store = new ConfirmTokenStore(clock);
        for (int i = 0; i < ConfirmTokenStore.MaxPending + 50; i++)
        {
            store.Issue("Gm" + i, "a", "x");
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.Equal(ConfirmTokenStore.MaxPending, store.PendingCount);
        clock.Advance(TimeSpan.FromMinutes(2));
        store.Issue("Late", "a", "x");
        Assert.Equal(1, store.PendingCount);
    }

    [Theory]
    [InlineData("map 1", "map 1", null)]
    [InlineData("map 1 confirm K7QX2M", "map 1", "K7QX2M")]
    [InlineData("map   1   CONFIRM   k7qx2m", "map 1", "k7qx2m")]
    [InlineData("map 1 confirm", "map 1", "")]
    [InlineData("confirm", "", "")]
    [InlineData("", "", null)]
    [InlineData("reconfirm", "reconfirm", null)]
    [InlineData("confirm map 1", "confirm map 1", null)]
    public void SplitCode_TakesATrailingConfirmWordPair(string args, string rest, string? code)
    {
        ConfirmTokenStore.SplitCode(args, out string actualRest, out string? actualCode);

        Assert.Equal(rest, actualRest);
        Assert.Equal(code, actualCode);
    }

    [Fact]
    public void Describe_HasATextForEveryRefusal_AndNoneForAccepted()
    {
        foreach (ConfirmResult result in Enum.GetValues<ConfirmResult>().Where(r => r != ConfirmResult.Accepted))
        {
            Assert.False(string.IsNullOrWhiteSpace(ConfirmTokenStore.Describe(result)), result.ToString());
        }

        Assert.Equal(string.Empty, ConfirmTokenStore.Describe(ConfirmResult.Accepted));
        Assert.Contains("60 s", ConfirmTokenStore.Describe(ConfirmResult.Expired), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryConfirm_RunsThePreviewThenTheConfirmedCommand_AgainstARealHost()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CTKONE", "Ctkone", AccountSecurity.Administrator);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("CTKONE"))!.Id)!;
        var replies = new List<string>();
        List<(bool Ok, string Args, string Token, string? Failure)> results = [];

        await host.OnWorldAsync(() =>
        {
            CommandContext Fresh() => CommandContext.ForConsole(session, ChatCommands.CreateTable(), replies.Add);

            CommandContext preview = Fresh();
            bool ok = ConfirmTokenStore.TryConfirm(preview, "purge", "all  now", "This would purge 12 rows.", out string args, out string token);
            results.Add((ok, args, token, preview.Failure));
            string code = replies.Single(l => l.Contains("confirm ", StringComparison.Ordinal)).Split("confirm ")[1][..ConfirmTokenStore.CodeLength];

            CommandContext wrong = Fresh();
            ok = ConfirmTokenStore.TryConfirm(wrong, "purge", "all now confirm ZZZZZZ", "", out args, out token);
            results.Add((ok, args, token, wrong.Failure));

            CommandContext other = Fresh();
            ok = ConfirmTokenStore.TryConfirm(other, "purge", "some confirm " + code, "", out args, out token);
            results.Add((ok, args, token, other.Failure));

            CommandContext good = Fresh();
            ok = ConfirmTokenStore.TryConfirm(good, "purge", "all now confirm " + code.ToLowerInvariant(), "", out args, out token);
            results.Add((ok, args, token, good.Failure));
        });

        Assert.Contains("This would purge 12 rows.", replies);
        Assert.Equal((false, "all now", string.Empty, "confirm-required"), results[0]);
        Assert.Equal((false, "all now", string.Empty, "confirm-wrongcode"), results[1]);
        Assert.Equal((false, "some", string.Empty, "confirm-mismatch"), results[2]);
        Assert.True(results[3].Ok);
        Assert.Equal("all now", results[3].Args);
        Assert.Equal(6, results[3].Token.Length);
        Assert.Null(results[3].Failure);
    }
}
