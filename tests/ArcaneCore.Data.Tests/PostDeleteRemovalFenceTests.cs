using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The removals a deleted character's queues run after its rows are gone (spellbook, spell state,
/// instance binds, social purge) are conditional on the character id still having no
/// <c>characters</c> row, so a late or retried removal cannot wipe a character that was explicitly
/// recreated with the same id. Defense in depth: the deletion ledger and the create fence are the
/// primary guarantee (docs/integration/character-delete.md). This is a conditional gap under
/// explicit id reuse, not an observed client loss.
/// </summary>
public sealed class PostDeleteRemovalFenceTests : IAsyncLifetime
{
    private const int Account = 1;
    private const int Other = 5000;
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LateSpellbookRemoval_KeepsTheRecreatedCharactersSpells(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int id = await RecreateAsync(cs);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfCharacterSpellStore(db).AddAsync(id, [78u, 6603u]);
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfCharacterSpellStore(db).DeleteCharacterAsync(id);
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(2, await read.Set<CharacterSpellRow>().CountAsync(r => r.CharacterId == id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LateSpellStateRemoval_KeepsTheRecreatedCharactersCooldownsAndAuras(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int id = await RecreateAsync(cs);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfCharacterSpellStateStore(db).SaveAsync(id, State());
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfCharacterSpellStateStore(db).DeleteCharacterAsync(id);
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(1, await read.Set<CharacterSpellCooldownRow>().CountAsync(r => r.CharacterId == id));
        Assert.Equal(1, await read.Set<CharacterAuraRow>().CountAsync(r => r.CharacterId == id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LateInstanceRemoval_KeepsTheRecreatedCharactersBindsAndLastInstance(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int id = await RecreateAsync(cs);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            var store = new EfInstanceStore(db);
            await store.SaveBindAsync(new CharacterInstanceBindRecord(id, 77, true));
            await store.SaveLastInstanceAsync(new CharacterLastInstanceRecord(id, 33, 77));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfInstanceStore(db).DeleteCharacterAsync(id);
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(1, await read.Set<CharacterInstanceRow>().CountAsync(r => r.CharacterId == id));
        Assert.Equal(1, await read.Set<CharacterLastInstanceRow>().CountAsync(r => r.CharacterId == id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LateSocialPurge_KeepsTheRecreatedCharactersFriendsAndGuildMembership(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int id = await RecreateAsync(cs);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            var social = new EfSocialStore(db);
            await social.SetSocialAsync(id, Other, SocialFlags.Friend);
            await social.SetSocialAsync(Other, id, SocialFlags.Ignored);
            await social.SaveGuildAsync(Guild(leader: Other, member: id));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfSocialStore(db).PurgeCharacterAsync(id);
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(1, await read.Set<CharacterSocialRow>().CountAsync(r => r.CharacterId == id));
        Assert.Equal(1, await read.Set<CharacterSocialRow>().CountAsync(r => r.OtherId == id));
        Assert.Equal(1, await read.Set<GuildMemberRow>().CountAsync(r => r.CharacterId == id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheSameRemovals_StillDeleteOrphanedRows_WhenNoCharacterHasTheId(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        }

        const int orphan = 777;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfCharacterSpellStore(db).AddAsync(orphan, [78u]);
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfCharacterSpellStateStore(db).SaveAsync(orphan, State());
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            var instances = new EfInstanceStore(db);
            await instances.SaveBindAsync(new CharacterInstanceBindRecord(orphan, 77, false));
            await instances.SaveLastInstanceAsync(new CharacterLastInstanceRecord(orphan, 33, 77));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfSocialStore(db).SetSocialAsync(orphan, Other, SocialFlags.Friend);
        }

        foreach (Func<CharacterDbContext, Task> removal in new Func<CharacterDbContext, Task>[]
        {
            db => new EfCharacterSpellStore(db).DeleteCharacterAsync(orphan),
            db => new EfCharacterSpellStateStore(db).DeleteCharacterAsync(orphan),
            db => new EfInstanceStore(db).DeleteCharacterAsync(orphan),
            db => new EfSocialStore(db).PurgeCharacterAsync(orphan),
        })
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
            await removal(db);
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(0, await read.Set<CharacterSpellRow>().CountAsync(r => r.CharacterId == orphan));
        Assert.Equal(0, await read.Set<CharacterSpellCooldownRow>().CountAsync(r => r.CharacterId == orphan));
        Assert.Equal(0, await read.Set<CharacterAuraRow>().CountAsync(r => r.CharacterId == orphan));
        Assert.Equal(0, await read.Set<CharacterInstanceRow>().CountAsync(r => r.CharacterId == orphan));
        Assert.Equal(0, await read.Set<CharacterLastInstanceRow>().CountAsync(r => r.CharacterId == orphan));
        Assert.Equal(0, await read.Set<CharacterSocialRow>().CountAsync(r => r.CharacterId == orphan));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    /// <summary>Create a character, delete it (no ledger), and recreate the same id explicitly.</summary>
    private static async Task<int> RecreateAsync(DatabaseConnectionOptions cs)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        int id = (await characters.CreateAsync(new CharacterRecord { AccountId = Account, Name = "First" })).Id;
        Assert.True(await characters.DeleteAsync(id, Account));
        CharacterRecord reborn = await characters.CreateAsync(new CharacterRecord { Id = id, AccountId = Account, Name = "Reborn" });
        Assert.Equal(id, reborn.Id);
        return id;
    }

    private static CharacterSpellState State() => new(
        [new CharacterSpellCooldownRow { Kind = 0, Id = 78, EndsAtUnixMs = 4_000_000_000_000 }],
        [new CharacterAuraRow { Spell = 100, RemainingMs = 60_000, MaxDurationMs = 60_000 }]);

    private static GuildData Guild(int leader, int member) => new(1, "Arcane", leader, "motd", "info", 1_700_000_000, 1, 2, 3, 4, 5,
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
        ]);
}
