using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>Edges of the confirm tokens (plan I7): replay, invoker and action binding through <see cref="ConfirmTokenStore.TryConfirm"/>, attempt accounting and concurrency.</summary>
public sealed class ConfirmTokenFlowTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private static string CodeFrom(IEnumerable<string> replies)
        => replies.Single(l => l.Contains("confirm ", StringComparison.Ordinal)).Split("confirm ")[1][..ConfirmTokenStore.CodeLength];

    [Fact]
    public async Task TryConfirm_CodeIsSingleUse_AndBoundToTheInvokerAndTheAction()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CTFONE", "Ctfone", AccountSecurity.Administrator);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("CTFONE"))!.Id)!;
        var replies = new List<string>();
        List<(bool Ok, string? Failure)> results = [];

        await host.OnWorldAsync(() =>
        {
            CommandContext Console() => CommandContext.ForConsole(session, ChatCommands.CreateTable(), replies.Add);
            CommandContext Player() => new(session, session.Player!, ChatCommands.CreateTable());

            CommandContext issue = Console();
            ConfirmTokenStore.TryConfirm(issue, "purge", "all", "preview", out _, out _);
            string code = CodeFrom(replies);

            CommandContext asPlayer = Player();
            results.Add((ConfirmTokenStore.TryConfirm(asPlayer, "purge", "all confirm " + code, "p", out _, out _), asPlayer.Failure));

            CommandContext otherAction = Console();
            results.Add((ConfirmTokenStore.TryConfirm(otherAction, "wipe", "all confirm " + code, "p", out _, out _), otherAction.Failure));

            CommandContext good = Console();
            results.Add((ConfirmTokenStore.TryConfirm(good, "purge", "all confirm " + code, "p", out _, out _), good.Failure));

            CommandContext replay = Console();
            results.Add((ConfirmTokenStore.TryConfirm(replay, "purge", "all confirm " + code, "p", out _, out _), replay.Failure));
        });

        Assert.Equal((false, "confirm-nopreview"), results[0]);
        Assert.Equal((false, "confirm-nopreview"), results[1]);
        Assert.Equal((true, (string?)null), results[2]);
        Assert.Equal((false, "confirm-nopreview"), results[3]);
    }

    [Fact]
    public async Task TryConfirm_ThreeWrongCodes_BurnThePreview_EvenForTheRightCodeAfterwards()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CTFTWO", "Ctftwo", AccountSecurity.Administrator);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("CTFTWO"))!.Id)!;
        var replies = new List<string>();
        List<string?> failures = [];

        await host.OnWorldAsync(() =>
        {
            CommandContext Console() => CommandContext.ForConsole(session, ChatCommands.CreateTable(), replies.Add);
            ConfirmTokenStore.TryConfirm(Console(), "purge", "x", "preview", out _, out _);
            string code = CodeFrom(replies);
            replies.Clear();

            for (int i = 0; i < ConfirmTokenStore.MaxWrongAttempts; i++)
            {
                CommandContext wrong = Console();
                Assert.False(ConfirmTokenStore.TryConfirm(wrong, "purge", "x confirm QQQQQQ", "p", out _, out _));
                failures.Add(wrong.Failure);
            }

            CommandContext late = Console();
            Assert.False(ConfirmTokenStore.TryConfirm(late, "purge", "x confirm " + code, "p", out _, out _));
            failures.Add(late.Failure);
        });

        Assert.Equal(["confirm-wrongcode", "confirm-wrongcode", "confirm-toomanyattempts", "confirm-nopreview"], failures);
        Assert.Contains(replies, l => l.Contains("Too many wrong confirm codes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryConfirm_ABareConfirmWord_IsAWrongCode_NotANewPreview()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CTFTHR", "Ctfthr", AccountSecurity.Administrator);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("CTFTHR"))!.Id)!;
        var replies = new List<string>();
        string? failure = null;
        bool ok = true;

        await host.OnWorldAsync(() =>
        {
            CommandContext Console() => CommandContext.ForConsole(session, ChatCommands.CreateTable(), replies.Add);
            ConfirmTokenStore.TryConfirm(Console(), "purge", "x", "preview", out _, out _);
            CommandContext bare = Console();
            ok = ConfirmTokenStore.TryConfirm(bare, "purge", "x confirm", "p", out _, out _);
            failure = bare.Failure;
        });

        Assert.False(ok);
        Assert.Equal("confirm-wrongcode", failure);
    }

    [Fact]
    public async Task TryConfirm_ReturnsTheUppercaseToken_ForTheWriteLog()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CTFFOU", "Ctffou", AccountSecurity.Administrator);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("CTFFOU"))!.Id)!;
        var replies = new List<string>();
        string token = string.Empty;
        string code = string.Empty;

        await host.OnWorldAsync(() =>
        {
            CommandContext Console() => CommandContext.ForConsole(session, ChatCommands.CreateTable(), replies.Add);
            ConfirmTokenStore.TryConfirm(Console(), "purge", "x", "preview", out _, out _);
            code = CodeFrom(replies);
            ConfirmTokenStore.TryConfirm(Console(), "purge", "x confirm  " + code.ToLowerInvariant() + " ", "p", out _, out token);
        });

        Assert.Equal(code, token);
    }

    [Fact]
    public void TryConfirm_RejectsANullContext()
        => Assert.Throws<ArgumentNullException>(() => ConfirmTokenStore.TryConfirm(null!, "a", "", "p", out _, out _));

    [Fact]
    public void AMismatch_DoesNotCountAsAWrongAttempt()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string code = store.Issue("Gm", "a", "map 1").Code;
        for (int i = 0; i < 10; i++) Assert.Equal(ConfirmResult.Mismatch, store.TryRedeem("Gm", "a", "map 2", code));

        Assert.Equal(ConfirmResult.Accepted, store.TryRedeem("Gm", "a", "map 1", code));
    }

    [Fact]
    public void TheBinding_IgnoresCaseAndBlanks_ButNotTheWords()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string code = store.Issue("Gm", "a", "Map 1").Code;

        Assert.Equal(ConfirmResult.Mismatch, store.TryRedeem("Gm", "a", "map 2", code));
        Assert.Equal(ConfirmResult.Accepted, store.TryRedeem("Gm", "a", "  MAP    1 ", code));
        Assert.Equal("foo bar", ConfirmTokenStore.NormalizeBinding("  Foo 	 BAR "));
    }

    [Fact]
    public void ARightCodeAfterTwoWrongOnes_IsStillAccepted()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string code = store.Issue("Gm", "a", "x").Code;
        store.TryRedeem("Gm", "a", "x", "AAAAAA");
        store.TryRedeem("Gm", "a", "x", "AAAAAA");

        Assert.Equal(ConfirmResult.Accepted, store.TryRedeem("Gm", "a", "x", code));
    }

    [Fact]
    public void ACodeIssuedForTheSameKeyAfterExpiry_StartsAFreshLifetime()
    {
        var clock = new ManualClock();
        var store = new ConfirmTokenStore(clock);
        store.Issue("Gm", "a", "x");
        clock.Advance(TimeSpan.FromSeconds(61));
        string fresh = store.Issue("Gm", "a", "x").Code;
        clock.Advance(TimeSpan.FromSeconds(59));

        Assert.Equal(ConfirmResult.Accepted, store.TryRedeem("Gm", "a", "x", fresh));
    }

    [Fact]
    public void Redeem_WithANullCode_IsWrong_NotAnError()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        store.Issue("Gm", "a", "x");

        Assert.Equal(ConfirmResult.WrongCode, store.TryRedeem("Gm", "a", "x", null!));
    }

    [Fact]
    public void Issue_RejectsEmptyInvokerAndAction_AndANullBinding()
    {
        var store = new ConfirmTokenStore(new ManualClock());

        Assert.Throws<ArgumentException>(() => store.Issue("", "a", "x"));
        Assert.Throws<ArgumentException>(() => store.Issue("Gm", "", "x"));
        Assert.Throws<ArgumentNullException>(() => store.Issue("Gm", "a", null!));
    }

    [Fact]
    public void AnEmptyBinding_IsAllowed_ForACommandWithoutArguments()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string code = store.Issue("Gm", "flush", "").Code;

        Assert.Equal(ConfirmResult.Accepted, store.TryRedeem("Gm", "flush", "", code));
    }

    [Fact]
    public void WhenFull_TheOldestPreviewGoes_AndTheNewestSurvives()
    {
        var clock = new ManualClock();
        var store = new ConfirmTokenStore(clock);
        string oldest = store.Issue("First", "a", "x").Code;
        for (int i = 0; i < ConfirmTokenStore.MaxPending; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            store.Issue("Gm" + i, "a", "x");
        }

        Assert.Equal(ConfirmTokenStore.MaxPending, store.PendingCount);
        Assert.Equal(ConfirmResult.NoPreview, store.TryRedeem("First", "a", "x", oldest));
    }

    [Fact]
    public void RacingRedeemers_OfOneCode_ProduceExactlyOneAcceptance()
    {
        var store = new ConfirmTokenStore(new ManualClock());
        string code = store.Issue("Gm", "a", "x").Code;
        var results = new ConcurrentBag<ConfirmResult>();

        Parallel.For(0, 32, _ => results.Add(store.TryRedeem("Gm", "a", "x", code)));

        Assert.Equal(1, results.Count(r => r == ConfirmResult.Accepted));
        Assert.Equal(31, results.Count(r => r == ConfirmResult.NoPreview));
    }

    [Fact]
    public void ConcurrentIssuers_NeverExceedTheBound_OrThrow()
    {
        var store = new ConfirmTokenStore(new ManualClock());

        Parallel.For(0, 8, w =>
        {
            for (int i = 0; i < 200; i++) store.Issue("G" + w + "_" + i, "a", "x");
        });

        Assert.Equal(ConfirmTokenStore.MaxPending, store.PendingCount);
    }

    [Fact]
    public async Task TheAuditFeature_ExposesAStore_ThatIssuesCodes()
    {
        await using var host = WorldTestHost.Start();
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();

        ConfirmToken token = audit.ConfirmTokens.Issue("Gm", "a", "x");

        Assert.Equal(ConfirmTokenStore.CodeLength, token.Code.Length);
        Assert.Same(audit.ConfirmTokens, audit.ConfirmTokens);
    }
}
