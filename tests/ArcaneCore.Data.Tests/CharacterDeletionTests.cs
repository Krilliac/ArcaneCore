using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Character deletion across every characters-database module (<see cref="ICharacterDataCleanup"/>)
/// on every engine: one transaction removes the character's own rows in each module and the
/// friend/ignore entries and guild membership that point at it; a guild leader, a wrong account and
/// a failing module all leave every row in place (vmangos Player::DeleteFromDB).
/// </summary>
public sealed class CharacterDeletionTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void EveryCharactersModule_DeclaresHowItsRowsAreDeleted()
    {
        Assert.Empty(CharacterDataCleanups.Missing.Select(m => m.GetType().FullName));
        Assert.Equal(DataModules.For(DatabaseComponent.Characters).Count(), CharacterDataCleanups.All.Count);
        Assert.Contains(CharacterDataCleanups.All, c => c is SocialDataModule);
        Assert.Contains(CharacterDataCleanups.All, c => c is CharacterSpellDataModule);
        Assert.Contains(CharacterDataCleanups.All, c => c is QuestNpcCharactersModule);
        Assert.Contains(CharacterDataCleanups.All, c => c is ItemCharacterDataModule);
        Assert.Contains(CharacterDataCleanups.All, c => c is CharacterLifeDataModule);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Delete_RemovesEveryPerCharacterRow_AndReferencesFromOthers(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, Seeded ids) = await SeedAsync(provider, aliceLeadsGuild: false);
        Counts bobBefore = await CountAsync(cs, ids.Bob);

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(ids.Alice, accountId: 1));
        }

        Assert.Equal(Counts.None, await CountAsync(cs, ids.Alice));
        // Bob keeps everything except his entry for Alice and hers for him.
        Assert.Equal(bobBefore with { SocialOwned = 1, SocialPointing = 0 }, await CountAsync(cs, ids.Bob));
        Assert.Equal((2, 1), (bobBefore.SocialOwned, bobBefore.SocialPointing));
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            var social = new EfSocialStore(db);
            Assert.Equal([new SocialEntry(ids.Carol, SocialFlags.Friend)], await social.GetSocialAsync(ids.Bob));
            Assert.Empty(await social.GetSocialAsync(ids.Carol)); // its ignore of Alice went with Alice
            GuildData guild = Assert.Single(await social.GetGuildsAsync());
            Assert.Equal(ids.Bob, guild.LeaderId);
            Assert.Equal([ids.Bob], guild.Members.Select(m => m.CharacterId));
            Assert.Equal(5, guild.Ranks.Count);
            Assert.Equal([ids.Bob, ids.Carol], await db.Characters.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Delete_GuildLeader_IsRefused_AndRollsBackEveryModule(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, Seeded ids) = await SeedAsync(provider, aliceLeadsGuild: true);
        Counts before = await CountAsync(cs, ids.Alice);

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            // The spell and quest modules run (set-based deletes) before the social module refuses.
            Assert.False(await new EfCharacterStore(db).DeleteAsync(ids.Alice, accountId: 1));
            Assert.Empty(db.ChangeTracker.Entries());
        }

        Assert.Equal(before, await CountAsync(cs, ids.Alice));
        Assert.True(before.Character == 1 && before.Spells == 2 && before.Quests == 1 && before.SocialOwned == 1);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Delete_ByAnotherAccount_RemovesNothing(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, Seeded ids) = await SeedAsync(provider, aliceLeadsGuild: false);
        Counts before = await CountAsync(cs, ids.Alice);

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.False(await new EfCharacterStore(db).DeleteAsync(ids.Alice, accountId: 2));
            Assert.False(await new EfCharacterStore(db).DeleteAsync(9999, accountId: 1));
        }

        Assert.Equal(before, await CountAsync(cs, ids.Alice));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Delete_WhenALaterModuleFails_RollsBackEarlierModules(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, Seeded ids) = await SeedAsync(provider, aliceLeadsGuild: false);
        Counts before = await CountAsync(cs, ids.Alice);

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            ICharacterDataCleanup[] cleanups = [.. CharacterDataCleanups.All, new FailingCleanup()];
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new EfCharacterStore(db).DeleteAsync(ids.Alice, 1, cleanups, CancellationToken.None));
            Assert.Empty(db.ChangeTracker.Entries());
        }

        Assert.Equal(before, await CountAsync(cs, ids.Alice));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PurgeCharacter_RemovesLateSocialRowsAndMembership(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, Seeded ids) = await SeedAsync(provider, aliceLeadsGuild: false);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(ids.Alice, accountId: 1));
        }

        // An older queued write lands after the deletion and names Alice again.
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            var social = new EfSocialStore(db);
            await social.SetSocialAsync(ids.Bob, ids.Alice, SocialFlags.Friend);
            GuildData guild = Assert.Single(await social.GetGuildsAsync());
            await social.SaveGuildAsync(guild with
            {
                Members = [.. guild.Members, new GuildMemberData(ids.Alice, 4, string.Empty, string.Empty, 1, 12, 0)],
            });
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfSocialStore(db).PurgeCharacterAsync(ids.Alice);
            await new EfSocialStore(db).PurgeCharacterAsync(ids.Alice); // idempotent
        }

        Assert.Equal(Counts.None, await CountAsync(cs, ids.Alice));
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.Equal([new SocialEntry(ids.Carol, SocialFlags.Friend)], await new EfSocialStore(db).GetSocialAsync(ids.Bob));
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<(DatabaseConnectionOptions, Seeded)> SeedAsync(DatabaseProvider provider, bool aliceLeadsGuild)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);

        var characters = new EfCharacterStore(db);
        int alice = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Alice" })).Id;
        int bob = (await characters.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Bob" })).Id;
        int carol = (await characters.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Carol" })).Id;

        foreach ((int id, uint item) in new[] { (alice, 100u), (bob, 200u) })
        {
            await characters.SaveStateAsync(new CharacterState(id, 0, 12, 0, 0, 0, 0, 1, 0, 0, 0, 0,
                [new ActionButton(0, 6603, 0)], new HomeBind(0, 12, 0, 0, 0)));
            await new EfItemStore(db).SaveInventoryAsync(id, new InventorySnapshot(
                [new(0, 15, new ItemInstanceData { Guid = item, Entry = 25, Count = 1 }),
                 new(0, 23, new ItemInstanceData { Guid = item + 1, Entry = 117, Count = 4 })]));
            await new EfCharacterSpellStore(db).AddAsync(id, [78u, 6603u]);
            await new EfCharacterQuestStore(db).SaveQuestsAsync(id, [Quest(id, 7)]);
            await new EfCharacterQuestStore(db).SaveTaxiMaskAsync(id, [3u, 0, 0, 0, 0, 0, 0, 0]);
        }

        var social = new EfSocialStore(db);
        await social.SetSocialAsync(alice, bob, SocialFlags.Friend);
        await social.SetSocialAsync(bob, alice, SocialFlags.Friend | SocialFlags.Ignored);
        await social.SetSocialAsync(carol, alice, SocialFlags.Ignored);
        await social.SetSocialAsync(bob, carol, SocialFlags.Friend);
        int leader = aliceLeadsGuild ? alice : bob;
        int member = aliceLeadsGuild ? bob : alice;
        await social.SaveGuildAsync(new GuildData(1, "Arcane", leader, "motd", "info", 1_700_000_000, 1, 2, 3, 4, 5,
            [
                new GuildRankData(0, "Guild Master", 0x1FF),
                new GuildRankData(1, "Officer", 0x1FF),
                new GuildRankData(2, "Veteran", 0x43),
                new GuildRankData(3, "Member", 0x43),
                new GuildRankData(4, "Initiate", 0x43),
            ],
            [
                new GuildMemberData(leader, 0, string.Empty, string.Empty, 1, 12, 0),
                new GuildMemberData(member, 4, string.Empty, string.Empty, 1, 12, 0),
            ]));
        return (cs, new Seeded(alice, bob, carol));
    }

    /// <summary>Rows that belong to or point at one character, per table, read on a fresh context.</summary>
    private static async Task<Counts> CountAsync(DatabaseConnectionOptions cs, int id)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        return new Counts(
            await db.Characters.CountAsync(c => c.Id == id),
            await db.ActionButtons.CountAsync(b => b.CharacterId == id),
            await db.Set<ItemInstanceRow>().CountAsync(r => r.OwnerGuid == id),
            await db.Set<CharacterInventoryRow>().CountAsync(r => r.Guid == id),
            await db.Set<CharacterSpellRow>().CountAsync(r => r.CharacterId == id),
            await db.Set<CharacterQuestStatusRow>().CountAsync(r => r.CharacterId == id),
            await db.Set<CharacterTaxiRow>().CountAsync(r => r.CharacterId == id),
            await db.Set<CharacterSocialRow>().CountAsync(r => r.CharacterId == id),
            await db.Set<CharacterSocialRow>().CountAsync(r => r.OtherId == id),
            await db.Set<GuildMemberRow>().CountAsync(r => r.CharacterId == id));
    }

    private static CharacterQuestStatus Quest(int characterId, uint quest)
        => new(characterId, quest, 3, false, false, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed record Seeded(int Alice, int Bob, int Carol);

    private sealed record Counts(
        int Character, int Buttons, int Items, int Slots, int Spells, int Quests, int Taxi,
        int SocialOwned, int SocialPointing, int GuildMember)
    {
        public static Counts None { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class FailingCleanup : ICharacterDataCleanup
    {
        public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("injected cleanup failure");
    }
}
