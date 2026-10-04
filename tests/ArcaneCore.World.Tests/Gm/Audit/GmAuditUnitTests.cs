using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.World.Gm.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>The duration grammar of <c>.mute</c> and the write-behind queue of the GM audit lane.</summary>
public sealed class GmAuditUnitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("30", 1800)]
    [InlineData("1", 60)]
    [InlineData("90m", 5400)]
    [InlineData("1d2h30m10s", 95410)]
    [InlineData("1D", 86400)]
    [InlineData("45s", 45)]
    [InlineData("525600", 365L * 24 * 3600)]
    [InlineData("365d", 365L * 24 * 3600)]
    public void Duration_ParsesMinutesAndUnitGroups(string text, long seconds)
    {
        Assert.True(GmMuteDuration.TryParse(text, out long parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0s")]
    [InlineData("abc")]
    [InlineData("m")]
    [InlineData("1h30")]          // a trailing number without a unit
    [InlineData("-5")]
    [InlineData("1.5h")]
    [InlineData("366d")]          // above the one-year ceiling
    [InlineData("525601")]
    [InlineData("99999999999999999999")]
    [InlineData("4294967297s")]   // would wrap to 1 second in vmangos' 32-bit TimeStringToSecs
    public void Duration_RefusesMalformedZeroAndHugeValues(string text) => Assert.False(GmMuteDuration.TryParse(text, out _));

    [Fact]
    public void Duration_LooksLikeDurationOnlyWhenItHasADigit()
    {
        Assert.True(GmMuteDuration.LooksLikeDuration("30m"));
        Assert.True(GmMuteDuration.LooksLikeDuration("5"));
        Assert.False(GmMuteDuration.LooksLikeDuration("Arthas"));
    }

    [Fact]
    public void Texts_CleanedToOneLine_AndCapped()
    {
        Assert.Equal("hello", GmTicketHandlers.CleanText("\u0007 hello \n"));
        Assert.Equal(GmAuditLimits.MaxTextLength, GmTicketHandlers.CleanText(new string('x', 5000)).Length);
        Assert.Equal(string.Empty, GmTicketHandlers.CleanText("\u0007\u0007"));
        Assert.Equal("No reason given", AuditCommands.CleanReason("   "));
        Assert.Equal("one line only", AuditCommands.CleanReason("one\nline\tonly"));
        Assert.Equal(255, AuditCommands.CleanReason(new string('r', 400)).Length);
    }

    // ---- the write queue ----

    private sealed class Rig : IAsyncDisposable
    {
        public InMemoryGmAuditStore Store { get; } = new();

        public GmAuditWriteQueue Queue { get; }

        private Rig(TimeSpan? retainedRetry)
        {
            ServiceProvider provider = new ServiceCollection().AddSingleton<IGmAuditStore>(Store).BuildServiceProvider();
            Queue = new GmAuditWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
            {
                RetryDelay = TimeSpan.FromMilliseconds(1),
                RetainedRetryInterval = retainedRetry ?? TimeSpan.FromHours(1),
            };
            Queue.Start();
        }

        public static Rig Create(TimeSpan? retainedRetry = null) => new(retainedRetry);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Queue.StopAsync();
            }
            catch (InvalidOperationException)
            {
                // a test that ends with a retained write
            }
        }
    }

    private static AccountMuteRecord Mute(int account, long until) => new(account, until, 1, "Gm", (byte)AccountSecurity.Moderator, "r");

    [Fact]
    public async Task Queue_WritesInOrder_AndLeavesNothingPending()
    {
        await using Rig rig = Rig.Create();
        rig.Queue.Save("mute:1", s => s.SaveMuteAsync(Mute(1, 100)));
        rig.Queue.Save("mute:2", s => s.SaveMuteAsync(Mute(2, 200)));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(100, rig.Store.Mute(1)!.MutedUntil);
        Assert.Equal(200, rig.Store.Mute(2)!.MutedUntil);
        Assert.Equal(0, rig.Queue.Pending);
        Assert.Empty(rig.Queue.RetainedKeys);
    }

    [Fact]
    public async Task Queue_ANewerWriteOfAKey_ReplacesOneStillWaiting()
    {
        await using Rig rig = Rig.Create();
        var gate = new TaskCompletionSource();
        rig.Queue.Save("blocker", async _ => await gate.Task);     // keeps the consumer busy so the next two are both waiting
        rig.Queue.Save("mute:1", s => s.SaveMuteAsync(Mute(1, 100)));
        rig.Queue.Save("mute:1", s => s.DeleteMuteAsync(1));       // coalesced: only this one runs
        gate.SetResult();
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(1, rig.Store.Attempts);
        Assert.Null(rig.Store.Mute(1));
    }

    [Fact]
    public async Task Queue_AWriteFailingThreeTimes_IsRetained_ThenPersistedAtTheNextRetry()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(3);
        rig.Queue.Save("mute:1", s => s.SaveMuteAsync(Mute(1, 100)));
        await rig.Queue.FlushAsync().WaitAsync(Wait);          // a pure barrier: attempted, never throws

        Assert.Equal(3, rig.Store.Attempts);
        Assert.Null(rig.Store.Mute(1));
        Assert.Equal(["mute:1"], rig.Queue.RetainedKeys);

        await rig.Queue.RetryRetainedAsync().WaitAsync(Wait);  // storage works again

        Assert.Equal(100, rig.Store.Mute(1)!.MutedUntil);
        Assert.Empty(rig.Queue.RetainedKeys);
    }

    [Fact]
    public async Task Queue_RetryRetained_FaultsWhileStorageIsStillFailing()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(1000);
        rig.Queue.Save("ticket:7", s => s.DeleteTicketAsync(7));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.RetryRetainedAsync().WaitAsync(Wait));
        Assert.Contains("ticket:7", error.Message, StringComparison.Ordinal);
        Assert.Equal(["ticket:7"], rig.Queue.RetainedKeys);
    }

    [Fact]
    public async Task Queue_ANewerValueOfARetainedKey_ReplacesIt()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(3);
        rig.Queue.Save("mute:1", s => s.SaveMuteAsync(Mute(1, 100)));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal(["mute:1"], rig.Queue.RetainedKeys);

        rig.Queue.Save("mute:1", s => s.SaveMuteAsync(Mute(1, 300)));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(300, rig.Store.Mute(1)!.MutedUntil);
        Assert.Empty(rig.Queue.RetainedKeys);
    }

    [Fact]
    public async Task Queue_TheTimerRetriesRetainedWrites_WithoutAnyOtherActivity()
    {
        await using Rig rig = Rig.Create(TimeSpan.FromMilliseconds(20));
        rig.Store.FailNext(3);
        rig.Queue.Save("mute:1", s => s.SaveMuteAsync(Mute(1, 100)));

        await WorldTestHost.WaitForAsync(() => rig.Store.Mute(1) is not null && rig.Queue.RetainedKeys.Count == 0, "the periodic retry to persist the write");
    }

    [Fact]
    public async Task Queue_Stop_RetriesOnceMore_AndThrowsNamingWhatIsStillNotDurable()
    {
        var rig = Rig.Create();
        rig.Store.FailNext(1000);
        rig.Queue.Save("mute:1", s => s.SaveMuteAsync(Mute(1, 100)));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync().WaitAsync(Wait));
        Assert.Contains("mute:1", error.Message, StringComparison.Ordinal);

        // After the stop a write is refused, and stays retained (and visible), never silently dropped.
        rig.Queue.Save("mute:2", s => s.SaveMuteAsync(Mute(2, 100)));
        Assert.Contains("mute:2", rig.Queue.RetainedKeys);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.RetryRetainedAsync().WaitAsync(Wait));
    }

    [Fact]
    public async Task Queue_Stop_WithRecoveredStorage_DrainsRetainedWritesAndSucceeds()
    {
        var rig = Rig.Create();
        rig.Store.FailNext(3);
        rig.Queue.Save("mute:1", s => s.SaveMuteAsync(Mute(1, 100)));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        await rig.Queue.StopAsync().WaitAsync(Wait);

        Assert.Equal(100, rig.Store.Mute(1)!.MutedUntil);
        await rig.Queue.StopAsync(); // idempotent
    }
}
