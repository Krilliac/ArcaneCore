using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Loot;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>The pure rules that decide how a stored chest may change (the store and the game share them).</summary>
public sealed class LootStateRulesTests
{
    private const int Alice = 1;
    private const int Bob = 2;
    private const int Carol = 3;
    private const long Respawn = 1_900_000_600;
    private static readonly LootStateKey Key = new(101, 7);

    private static LootStateItem Shared(byte slot, uint item = 10, uint count = 1)
        => new(slot, item, count, false, false, false, [], []);

    private static LootStateItem PerPlayer(byte slot) => new(slot, 20, 1, false, true, false, [], []);

    private static LootStateItem Quest(byte slot, params int[] allowed) => new(slot, 30, 1, true, false, false, allowed, []);

    private static LootStateRecord Chest(int owner = 0, int[]? recipients = null, params LootStateItem[] items)
        => new(Key, 3000, owner, 1, false, 0, recipients ?? [Alice, Bob], items);

    [Fact]
    public void Equality_ComparesListsAsSets()
    {
        LootStateRecord a = Chest(0, [Alice, Bob], Shared(0), Quest(1, Alice, Bob));
        LootStateRecord b = Chest(0, [Bob, Alice], Quest(1, Bob, Alice), Shared(0));
        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { Generation = 2 });
        Assert.NotEqual(a, Chest(0, [Alice, Bob], Shared(0), Quest(1, Alice)));
    }

    [Fact]
    public void SharedStack_IsTakenOnce_ByTheOwnerWhenThereIsOne()
    {
        LootStateRecord chest = Chest(owner: Alice, items: Shared(0));
        Assert.Null(LootStateRules.Take(chest, Bob, 0, Respawn)); // not the round-robin owner
        LootStateRecord taken = LootStateRules.Take(chest, Alice, 0, Respawn)!;
        Assert.True(taken.Items[0].IsLooted);
        Assert.True(taken.Consumed);
        Assert.Equal(Respawn, taken.RespawnAtUnix);
        Assert.Null(LootStateRules.Take(taken, Alice, 0, Respawn)); // the slot is gone
        Assert.Null(LootStateRules.Take(chest, Alice, 5, Respawn)); // no such slot
    }

    [Fact]
    public void NonRecipient_CannotTake_ButAnEmptyRecipientListMeansAnyone()
    {
        LootStateRecord chest = Chest(items: Shared(0));
        Assert.Null(LootStateRules.Take(chest, Carol, 0, Respawn));
        Assert.NotNull(LootStateRules.Take(chest with { Recipients = [] }, Carol, 0, Respawn));
    }

    [Fact]
    public void PerPlayerStack_IsLootedOnceEveryRecipientTookTheirCopy()
    {
        LootStateRecord chest = Chest(items: PerPlayer(0));
        LootStateRecord afterAlice = LootStateRules.Take(chest, Alice, 0, Respawn)!;
        Assert.False(afterAlice.Items[0].IsLooted);
        Assert.False(afterAlice.Consumed);
        Assert.Null(LootStateRules.Take(afterAlice, Alice, 0, Respawn)); // one copy each
        LootStateRecord afterBob = LootStateRules.Take(afterAlice, Bob, 0, Respawn)!;
        Assert.True(afterBob.Items[0].IsLooted);
        Assert.True(afterBob.Consumed);
    }

    [Fact]
    public void QuestStack_IsOnlyForAllowedCharacters_AndLootedWhenAllTookIt()
    {
        LootStateRecord chest = Chest(items: [Quest(0, Alice, Bob)]);
        Assert.Null(LootStateRules.Take(chest, Carol, 0, Respawn));
        LootStateRecord afterAlice = LootStateRules.Take(chest, Alice, 0, Respawn)!;
        Assert.False(afterAlice.Items[0].IsLooted);
        Assert.True(LootStateRules.Take(afterAlice, Bob, 0, Respawn)!.Consumed);
    }

    [Fact]
    public void Replay_AddsALateOpenerOnlyWhenTheyTake_AndNeverSetsAnOwner()
    {
        LootStateRecord chest = Chest(recipients: [Alice], items: [Shared(0), Shared(1)]);
        LootAward award = new(Carol, 0, 10, 1);

        LootStateRecord joined = LootStateRules.Replay(chest, [Alice, Carol], 0, [award], Respawn)!;
        Assert.Equal([Alice, Carol], joined.Recipients.Order());
        Assert.True(joined.Items[0].IsLooted);

        Assert.Null(LootStateRules.Replay(chest, [Alice, Carol, Bob], 0, [award], Respawn)); // Bob took nothing
        Assert.Null(LootStateRules.Replay(chest, [Carol], 0, [award], Respawn)); // recipients never shrink
        Assert.Null(LootStateRules.Replay(chest, [Alice, Carol], Carol, [award], Respawn)); // an owner cannot be assigned
        Assert.Null(LootStateRules.Replay(chest, [Alice, Carol], 0, [award with { ItemId = 11 }], Respawn)); // wrong item
        Assert.Null(LootStateRules.Replay(chest, [Alice, Carol], 0, [award with { Count = 2 }], Respawn)); // wrong count
        Assert.Null(LootStateRules.Replay(chest with { Consumed = true, RespawnAtUnix = 1 }, [Alice], 0, [], Respawn));
    }

    [Fact]
    public void Replay_ReleasingTheOwner_OpensTheLeftoversToEveryRecipient()
    {
        LootStateRecord chest = Chest(owner: Alice, items: Shared(0));
        Assert.Null(LootStateRules.Take(chest, Bob, 0, Respawn));
        LootStateRecord released = LootStateRules.Replay(chest, chest.Recipients, 0, [], 0)!;
        Assert.Equal(0, released.LootOwnerCharacterId);
        Assert.NotNull(LootStateRules.Take(released, Bob, 0, Respawn));
        Assert.True(LootStateRules.IsLegalSuccessor(chest, released, []));
    }

    [Fact]
    public void IsLegalSuccessor_AcceptsOnlyTheExactReplayOfTheAwards()
    {
        LootStateRecord chest = Chest(items: [Shared(0), Shared(1)]);
        LootAward award = new(Alice, 0, 10, 1);
        LootStateRecord taken = LootStateRules.Replay(chest, chest.Recipients, 0, [award], Respawn)!;
        Assert.True(LootStateRules.IsLegalSuccessor(chest, taken, [award]));

        Assert.False(LootStateRules.IsLegalSuccessor(chest, taken, [])); // a take without its award
        Assert.False(LootStateRules.IsLegalSuccessor(chest, chest, [award])); // an award without the take
        Assert.False(LootStateRules.IsLegalSuccessor(taken, taken, [award])); // re-taking the same slot
        Assert.False(LootStateRules.IsLegalSuccessor(chest, taken with { Generation = 2 }, [award]));
        Assert.False(LootStateRules.IsLegalSuccessor(chest, taken with { SourceEntry = 1 }, [award]));
        Assert.False(LootStateRules.IsLegalSuccessor(chest, taken with { Key = new LootStateKey(102, 7) }, [award]));
        LootStateRecord both = taken with { Items = [.. taken.Items.Select(i => i with { IsLooted = true })], Consumed = true, RespawnAtUnix = Respawn };
        Assert.False(LootStateRules.IsLegalSuccessor(chest, both, [award])); // an extra slot nobody was awarded
        Assert.False(LootStateRules.IsLegalSuccessor(chest, taken with { Consumed = true, RespawnAtUnix = Respawn }, [award]));
    }

    [Fact]
    public void IsLegalSuccessor_GenerationsAreFreshAndSequential()
    {
        LootStateRecord fresh = Chest(items: [Shared(0)]);
        Assert.True(LootStateRules.IsLegalSuccessor(null, fresh, []));
        Assert.False(LootStateRules.IsLegalSuccessor(null, fresh with { Generation = 2 }, []));
        Assert.False(LootStateRules.IsLegalSuccessor(null, fresh, [new LootAward(Alice, 0, 10, 1)]));
        Assert.False(LootStateRules.IsLegalSuccessor(null, fresh with { Items = [Shared(0) with { IsLooted = true }] }, []));
        Assert.False(LootStateRules.IsLegalSuccessor(null, fresh with { Items = [Shared(0), Shared(0)] }, []));
        Assert.False(LootStateRules.IsLegalSuccessor(null, fresh with { Key = new LootStateKey(0, 7) }, []));
        Assert.False(LootStateRules.IsLegalSuccessor(null, fresh with { LootOwnerCharacterId = Carol }, []));

        // An empty chest is generated consumed, with a respawn time.
        LootStateRecord empty = fresh with { Items = [], Consumed = true, RespawnAtUnix = Respawn };
        Assert.True(LootStateRules.IsLegalSuccessor(null, empty, []));
        Assert.False(LootStateRules.IsLegalSuccessor(null, empty with { RespawnAtUnix = 0 }, []));
        Assert.False(LootStateRules.IsLegalSuccessor(null, fresh with { Consumed = true, RespawnAtUnix = Respawn }, []));

        // Only a consumed chest regenerates, to the next generation of the same key.
        Assert.False(LootStateRules.IsLegalSuccessor(fresh, fresh with { Generation = 2 }, []));
        Assert.True(LootStateRules.IsLegalSuccessor(empty, fresh with { Generation = 2 }, []));
        Assert.False(LootStateRules.IsLegalSuccessor(empty, fresh, []));
        Assert.False(LootStateRules.IsLegalSuccessor(empty, fresh with { Generation = 3 }, []));
        Assert.False(LootStateRules.IsLegalSuccessor(empty, fresh with { Generation = 2, Key = new LootStateKey(101, 8) }, []));
    }

    [Fact]
    public void AwardsMatchParticipants_RequiresTheExactInventoryDelta()
    {
        CharacterState before = State(Alice, 100, Stack(1, 10, 3));
        LootAward award = new(Alice, 0, 10, 2);

        // New stack, and a stack that grew.
        Assert.True(LootStateRules.AwardsMatchParticipants([award], [Participant(before, Stack(1, 10, 3), Stack(2, 10, 2))]));
        Assert.True(LootStateRules.AwardsMatchParticipants([award], [Participant(before, Stack(1, 10, 5))]));

        Assert.False(LootStateRules.AwardsMatchParticipants([award], [Participant(before, Stack(1, 10, 3))]));
        Assert.False(LootStateRules.AwardsMatchParticipants([award], [Participant(before, Stack(1, 10, 6))]));
        Assert.False(LootStateRules.AwardsMatchParticipants([award], [Participant(before, Stack(1, 10, 3), Stack(2, 11, 2))]));
        Assert.False(LootStateRules.AwardsMatchParticipants([award], [Participant(before, Stack(1, 10, 1))])); // something was removed
        Assert.False(LootStateRules.AwardsMatchParticipants([award], [])); // the awarded character does not take part
        Assert.False(LootStateRules.AwardsMatchParticipants([], [Participant(before, Stack(1, 10, 3))])); // a participant without an award

        EconomyParticipant paid = Participant(before, Stack(1, 10, 5)) with { After = before with { Money = 99, Inventory = Inventory(Stack(1, 10, 5)) } };
        Assert.False(LootStateRules.AwardsMatchParticipants([award], [paid]));
    }

    private static InventoryItemData Stack(uint guid, uint entry, uint count)
        => new(0, (byte)(23 + guid), new ItemInstanceData { Guid = guid, Entry = entry, Count = count });

    private static InventorySnapshot Inventory(params InventoryItemData[] items) => new(items);

    private static CharacterState State(int id, uint money, params InventoryItemData[] items)
        => new(id, 0, 12, 1, 2, 3, 0, 10, 50, Money: money, ActionButtons: [], Home: new(0, 12, 1, 2, 3), Inventory: Inventory(items));

    private static EconomyParticipant Participant(CharacterState before, params InventoryItemData[] after)
        => new(before, before with { Inventory = Inventory(after) });
}
