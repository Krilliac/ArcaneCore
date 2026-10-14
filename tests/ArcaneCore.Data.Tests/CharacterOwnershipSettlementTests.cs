using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Settlement paths that stage core state cannot bypass the captured owner epoch.</summary>
public sealed class CharacterOwnershipSettlementTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OldOwnerCannotCommitEconomyOrQuestReward(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        CharacterState before;
        CharacterQuestStatus quest;
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            var characters = new EfCharacterStore(setup);
            CharacterRecord character = await characters.CreateAsync(new CharacterRecord
            {
                AccountId = 1, Name = "OwnedSettler", Race = 1, Class = 1, Level = 10,
            });
            before = new(character.Id, 0, 12, 1, 2, 3, 0, 10, 50, Money: 100,
                Inventory: new InventorySnapshot([new(0, 23, new ItemInstanceData { Guid = 100, Entry = 117, Count = 4 })]));
            await characters.SaveStateAsync(before);
            quest = new(character.Id, 900001, 1, false, true, 1_900_000_000,
                1, 2, 3, 4, 4, 5, 6, 7, 0);
            await new EfCharacterQuestStore(setup).SaveQuestsAsync(character.Id, [quest]);
        }

        CharacterOwnershipToken old;
        await using (CharacterDbContext claim = TestContexts.Create<CharacterDbContext>(connection))
        {
            old = Assert.IsType<CharacterOwnershipToken>(await new EfCharacterOwnershipStore(claim)
                .TryAcquireAsync("world", "first", Guid.NewGuid()));
        }
        before = before with { Ownership = old };
        CharacterState after = before with
        {
            Money = 999,
            X = 200,
            Inventory = new InventorySnapshot([new(0, 24, new ItemInstanceData { Guid = 101, Entry = 118, Count = 1 })]),
        };
        await using (CharacterDbContext takeover = TestContexts.Create<CharacterDbContext>(connection))
            Assert.NotNull(await new EfCharacterOwnershipStore(takeover).TryTakeoverAsync(old, "second", Guid.NewGuid()));

        await using (CharacterDbContext economy = TestContexts.Create<CharacterDbContext>(connection))
        {
            await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => new EfEconomyStore(economy).CommitAsync(
                new EconomyCommitRequest(Guid.NewGuid(), [new EconomyParticipant(before, after, [100])], [])));
            Assert.Empty(economy.ChangeTracker.Entries());
        }

        await using (CharacterDbContext reward = TestContexts.Create<CharacterDbContext>(connection))
        {
            var request = new CharacterQuestRewardRequest(before, after, quest,
                quest with { Rewarded = true, Timer = 0, RewardChoice = 118 }, LearnedSpells: [902]);
            await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => new EfCharacterQuestRewardStore(reward).CommitAsync(request));
            Assert.Empty(reward.ChangeTracker.Entries());
        }

        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(connection);
        CharacterRecord stored = Assert.IsType<CharacterRecord>(await new EfCharacterStore(check).GetByIdAsync(before.Id));
        Assert.Equal((100u, 1f), (stored.Money, stored.X));
        InventoryItemData item = Assert.Single(await new EfItemStore(check).GetInventoryAsync(before.Id));
        Assert.Equal((100u, 4u, (byte)23), (item.Item.Guid, item.Item.Count, item.Slot));
        Assert.Equal(quest, Assert.Single((await new EfCharacterQuestStore(check).LoadAsync(before.Id)).Quests));
        Assert.False(await check.Set<CharacterSpellRow>().AnyAsync(r => r.CharacterId == before.Id && r.Spell == 902));
    }
}
