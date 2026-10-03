using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Reputation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Fixture = ArcaneCore.World.Tests.Reputation.ReputationWriteDurabilityTests.Fixture;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>
/// Retention semantics of <see cref="ReputationWriteQueue"/> beyond the headline failures in
/// <see cref="ReputationWriteDurabilityTests"/>: the per-character login barrier, coalescing, the
/// logout retry, delete interplay, idempotent stop and the missing-store no-op.
/// </summary>
public sealed class ReputationWriteRetentionTests
{
    private const uint BootyBay = 21;

    [Fact]
    public async Task FlushCharacter_WithUnrecoveredFailure_Throws_ThenSucceedsAfterRecovery()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        int other = fixture.Ids[1];
        fixture.Control.FailWrites = true;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        fixture.Queue.SaveWatchedFaction(id, 0);
        await fixture.Queue.FlushAsync();
        Assert.True(fixture.Queue.HasRetainedFailure(id));
        Assert.Equal([id], fixture.Queue.RetainedCharacters);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Queue.FlushCharacterAsync(id));
        Assert.Contains(id.ToString(), refused.Message);
        Assert.IsType<IOException>(refused.InnerException);
        await fixture.Queue.FlushCharacterAsync(other); // another character is unaffected
        Assert.Empty((await fixture.LoadAsync(id)).Factions);

        fixture.Control.FailWrites = false;
        await fixture.Queue.FlushCharacterAsync(id);
        Assert.False(fixture.Queue.HasRetainedFailure(id));
        Assert.Empty(fixture.Queue.RetainedCharacters);
        CharacterReputationData stored = await fixture.LoadAsync(id);
        Assert.Equal([new CharacterReputationRow(id, BootyBay, 3100, 0x03)], stored.Factions);
        Assert.Equal(0, stored.WatchedFaction);
    }

    [Fact]
    public async Task ChangesDuringAnOutage_AreCoalescedIntoOneQueuedWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.FailWrites = true;
        fixture.Control.HoldNextWrite();
        fixture.Queue.SaveFactions(id, [new(id, 1, 1, 0x01)]);
        await fixture.Control.Entered.Task.WaitAsync(Fixture.Budget);
        for (uint faction = 2; faction <= 21; faction++)
        {
            fixture.Queue.SaveFactions(id, [new(id, faction, (int)faction, 0x01)]);
        }

        Assert.Equal(2, fixture.Queue.Pending); // the write in flight and one coalesced successor
        fixture.Control.Release.TrySetResult();
        await fixture.Queue.FlushAsync().WaitAsync(Fixture.Budget);

        Assert.Equal(0, fixture.Queue.Pending);
        Assert.Equal(6, fixture.Control.AttemptsFor(id)); // 3 attempts in flight + 3 for the single coalesced write
        fixture.Control.FailWrites = false;
        await fixture.Queue.FlushCharacterAsync(id);
        Assert.Equal(21, (await fixture.LoadAsync(id)).Factions.Count);
    }

    [Fact]
    public async Task RequestRetry_PersistsRetainedWrite_WithoutAnotherChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.FailWrites = true;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        await fixture.Queue.FlushAsync();
        fixture.Control.FailWrites = false;

        fixture.Queue.RequestRetry(id);
        fixture.Queue.RequestRetry(fixture.Ids[1]); // nothing retained: a no-op
        await fixture.Queue.FlushAsync();

        Assert.False(fixture.Queue.HasRetainedFailure(id));
        Assert.Single((await fixture.LoadAsync(id)).Factions);
        Assert.Equal(0, fixture.Control.AttemptsFor(fixture.Ids[1]));
    }

    [Fact]
    public async Task DeleteCharacter_DiscardsRetainedRows_AndRetriesItsOwnFailedDelete()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        await using (var db = fixture.NewContext())
        {
            await new ArcaneCore.Data.Reputation.EfCharacterReputationStore(db).SaveFactionsAsync(id, [new(id, 99, 5, 0x01)]);
        }

        fixture.Control.FailWrites = true;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        await fixture.Queue.FlushAsync();
        await fixture.RemoveCharacterRowAsync(id);
        fixture.Queue.DeleteCharacter(id);
        await fixture.Queue.FlushAsync();
        Assert.True(fixture.Queue.HasRetainedFailure(id)); // the delete itself is retained until storage recovers

        fixture.Control.FailWrites = false;
        await fixture.Queue.FlushCharacterAsync(id);

        CharacterReputationData stored = await fixture.LoadAsync(id);
        Assert.Empty(stored.Factions); // the pre-existing row went, and the discarded gain was not written back
        Assert.Equal(-1, stored.WatchedFaction);
        Assert.False(fixture.Queue.HasRetainedFailure(id));
    }

    [Fact]
    public async Task SecondDeleteWhileFirstInFlight_StillRunsItsOwnDelete()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.HoldNextWrite();
        await fixture.RemoveCharacterRowAsync(id);
        fixture.Queue.DeleteCharacter(id);
        await fixture.Control.Entered.Task.WaitAsync(Fixture.Budget);
        await fixture.RemoveCharacterRowAsync(id);
        fixture.Queue.DeleteCharacter(id);
        fixture.Control.Release.TrySetResult();
        await fixture.Queue.FlushAsync().WaitAsync(Fixture.Budget);

        Assert.Equal(2, fixture.Control.AttemptsFor(id));
        Assert.False(fixture.Queue.HasRetainedFailure(id));
    }

    [Fact]
    public async Task ForgetCharacter_DropsWhatWasRetainedForAReusedId()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.FailWrites = true;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        await fixture.Queue.FlushAsync();
        Assert.True(fixture.Queue.HasRetainedFailure(id));

        fixture.Queue.ForgetCharacter(id);
        fixture.Control.FailWrites = false;
        await fixture.Queue.FlushCharacterAsync(id);
        Assert.False(fixture.Queue.HasRetainedFailure(id));
        Assert.Empty((await fixture.LoadAsync(id)).Factions);
        await fixture.Queue.StopAsync();
    }

    [Fact]
    public async Task Stop_IsIdempotent_ReportsTheSameFailureTwice_AndLaterChangesAreRetainedNotThrown()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.FailWrites = true;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        await fixture.Queue.FlushAsync();

        InvalidOperationException first = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Queue.StopAsync);
        InvalidOperationException second = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Queue.StopAsync);
        Assert.Same(first, second);

        fixture.Queue.SaveWatchedFaction(id, 0); // after stop: logged and retained in memory, never thrown on the world thread
        Assert.True(fixture.Queue.HasRetainedFailure(id));
        await fixture.Queue.FlushAsync(); // completes: the channel is closed
    }

    [Fact]
    public async Task Stop_WithNothingRetained_Completes_AndWithoutAStore_WritesAreANoOp()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var queue = new ReputationWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();
        queue.SaveFactions(1, [new(1, BootyBay, 3100, 0x03)]);
        queue.SaveWatchedFaction(1, 0);
        await queue.FlushCharacterAsync(1);
        Assert.False(queue.HasRetainedFailure(1));
        Assert.Equal(0, queue.Pending);
        await queue.StopAsync();
        await queue.StopAsync();
    }
}
