using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>The confirm code of <c>.playerbot fleet reset</c>: short, random, single use, 60 seconds, bound to the invoker.</summary>
public sealed class FleetResetConfirmationTests
{
    private static readonly FleetResetPromise Promise = new(12, 12);

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    [Fact]
    public void ACode_IsShortRandomAndUsesTheSafeAlphabet()
    {
        var clock = new ManualClock();
        var confirmation = new FleetResetConfirmation(() => clock);
        HashSet<string> seen = [];
        for (int i = 0; i < 200; i++) seen.Add(confirmation.Issue("Console", Promise));

        Assert.True(seen.Count > 190, "codes are random");
        Assert.All(seen, code =>
        {
            Assert.Equal(FleetResetConfirmation.CodeLength, code.Length);
            Assert.All(code, c => Assert.True(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)));
            Assert.DoesNotContain(code, c => "IL01O".Contains(c));
        });
    }

    [Fact]
    public void TheCode_IsAcceptedOnce_WithWhatTheOperatorWasShown_IgnoringCase()
    {
        var clock = new ManualClock();
        var confirmation = new FleetResetConfirmation(() => clock);
        string code = confirmation.Issue("Console", Promise);

        Assert.Equal(FleetResetCodeResult.Accepted, confirmation.TryRedeem("Console", code.ToLowerInvariant(), out FleetResetPromise? promise));
        Assert.Equal(Promise, promise);
        Assert.Equal(FleetResetCodeResult.NoPreview, confirmation.TryRedeem("Console", code, out _));
    }

    [Fact]
    public void TheCode_ExpiresAfterSixtySeconds()
    {
        var clock = new ManualClock();
        var confirmation = new FleetResetConfirmation(() => clock);
        string code = confirmation.Issue("Console", Promise);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(confirmation.HasPending);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(confirmation.HasPending);
        Assert.Equal(FleetResetCodeResult.Expired, confirmation.TryRedeem("Console", code, out FleetResetPromise? promise));
        Assert.Null(promise);
        Assert.Equal(FleetResetCodeResult.NoPreview, confirmation.TryRedeem("Console", code, out _));
    }

    [Fact]
    public void TheCodeJustBeforeTheDeadline_IsStillAccepted()
    {
        var clock = new ManualClock();
        var confirmation = new FleetResetConfirmation(() => clock);
        string code = confirmation.Issue("Console", Promise);
        clock.Advance(TimeSpan.FromSeconds(59.9));

        Assert.Equal(FleetResetCodeResult.Accepted, confirmation.TryRedeem("Console", code, out _));
    }

    [Fact]
    public void AWrongCode_IsRefused_AndThreeOfThemEndThePreview()
    {
        var clock = new ManualClock();
        var confirmation = new FleetResetConfirmation(() => clock);
        string code = confirmation.Issue("Console", Promise);
        string wrong = code[0] == 'A' ? "BBBBBB" : "AAAAAA";

        Assert.Equal(FleetResetCodeResult.WrongCode, confirmation.TryRedeem("Console", wrong, out _));
        Assert.Equal(FleetResetCodeResult.WrongCode, confirmation.TryRedeem("Console", "", out _));
        Assert.Equal(FleetResetCodeResult.TooManyAttempts, confirmation.TryRedeem("Console", wrong + "A", out _));
        Assert.Equal(FleetResetCodeResult.NoPreview, confirmation.TryRedeem("Console", code, out _));
    }

    [Fact]
    public void TheCode_BelongsToTheInvokerWhoAskedForIt_AndANewPreviewReplacesTheOld()
    {
        var clock = new ManualClock();
        var confirmation = new FleetResetConfirmation(() => clock);
        string first = confirmation.Issue("Alice", Promise);

        Assert.Equal(FleetResetCodeResult.OtherInvoker, confirmation.TryRedeem("Bob", first, out _));
        string second = confirmation.Issue("Alice", new FleetResetPromise(3, 3));
        if (first != second) Assert.Equal(FleetResetCodeResult.WrongCode, confirmation.TryRedeem("Alice", first, out _));
        Assert.Equal(FleetResetCodeResult.Accepted, confirmation.TryRedeem("Alice", second, out FleetResetPromise? promise));
        Assert.Equal(new FleetResetPromise(3, 3), promise);
    }
}
