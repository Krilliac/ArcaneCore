using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Kernel.Social;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The social tables (characters schema step of <see cref="SocialDataModule"/>) on every
/// engine: friend/ignore entries and guilds with their ranks and members round trip, update
/// in place and delete cleanly.
/// </summary>
public sealed class SocialStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SocialEntries_AreSetUpdatedAndRemoved_PerCharacter(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SetSocialAsync(1, 2, SocialFlags.Friend);
            await store.SetSocialAsync(1, 3, SocialFlags.Ignored);
            await store.SetSocialAsync(4, 1, SocialFlags.Friend);
        });

        await WithStore(cs, async store =>
        {
            Assert.Equal(
                [new SocialEntry(2, SocialFlags.Friend), new SocialEntry(3, SocialFlags.Ignored)],
                (await store.GetSocialAsync(1)).OrderBy(e => e.OtherId));
            await store.SetSocialAsync(1, 2, SocialFlags.Friend | SocialFlags.Ignored);
            await store.SetSocialAsync(1, 3, SocialFlags.None);
        });

        await WithStore(cs, async store =>
        {
            Assert.Equal([new SocialEntry(2, SocialFlags.Friend | SocialFlags.Ignored)], await store.GetSocialAsync(1));
            Assert.Equal([new SocialEntry(1, SocialFlags.Friend)], await store.GetSocialAsync(4));
            Assert.Empty(await store.GetSocialAsync(9));
            await store.SetSocialAsync(9, 1, SocialFlags.None); // removing a missing entry is a no-op
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Guilds_RoundTrip_UpdateInPlace_AndDelete(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        GuildData arcane = Guild(1, "Arcane", leader: 10, members: [(10, 0, "boss", "secret"), (11, 4, string.Empty, string.Empty)]);
        GuildData other = Guild(2, "Other", leader: 20, members: [(20, 0, string.Empty, string.Empty)]);
        await WithStore(cs, async store =>
        {
            await store.SaveGuildAsync(arcane);
            await store.SaveGuildAsync(other);
        });

        await WithStore(cs, async store =>
        {
            IReadOnlyList<GuildData> loaded = await store.GetGuildsAsync();
            Assert.Equal(2, loaded.Count);
            AssertGuild(arcane, loaded.Single(g => g.Id == 1));
            AssertGuild(other, loaded.Single(g => g.Id == 2));
        });

        // Rename a rank, drop a member, promote another, change the MOTD and the leader.
        GuildData changed = arcane with
        {
            LeaderId = 11,
            Motd = "new motd",
            Ranks = [.. arcane.Ranks.Select(r => r.RankId == 4 ? r with { Name = "Recruit" } : r)],
            Members = [new GuildMemberData(11, 0, "now leader", string.Empty, 60, 1519, 0)],
        };
        await WithStore(cs, store => store.SaveGuildAsync(changed));
        await WithStore(cs, async store => AssertGuild(changed, (await store.GetGuildsAsync()).Single(g => g.Id == 1)));

        await WithStore(cs, store => store.DeleteGuildAsync(1));
        await WithStore(cs, async store =>
        {
            GuildData remaining = Assert.Single(await store.GetGuildsAsync());
            AssertGuild(other, remaining);
            await store.DeleteGuildAsync(99); // a missing guild is a no-op
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<DatabaseConnectionOptions> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return cs;
    }

    /// <summary>Run against a fresh context so every read comes from the database, not the change tracker.</summary>
    private static async Task WithStore(DatabaseConnectionOptions cs, Func<EfSocialStore, Task> action)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await action(new EfSocialStore(db));
    }

    private static GuildData Guild(int id, string name, int leader, (int Id, byte Rank, string Note, string OfficerNote)[] members)
        => new(id, name, leader, "motd of " + name, "info of " + name, 1_700_000_000, 1, 2, 3, 4, 5,
            [
                new GuildRankData(0, "Guild Master", 0x1FF),
                new GuildRankData(1, "Officer", 0x1FF),
                new GuildRankData(2, "Veteran", 0x43),
                new GuildRankData(3, "Member", 0x43),
                new GuildRankData(4, "Initiate", 0x43),
            ],
            [.. members.Select(m => new GuildMemberData(m.Id, m.Rank, m.Note, m.OfficerNote, 12, 12, 1_700_000_100))]);

    private static void AssertGuild(GuildData expected, GuildData actual)
    {
        Assert.Equal(expected with { Ranks = [], Members = [] }, actual with { Ranks = [], Members = [] });
        Assert.Equal(expected.Ranks, actual.Ranks.OrderBy(r => r.RankId));
        Assert.Equal(expected.Members.OrderBy(m => m.CharacterId), actual.Members.OrderBy(m => m.CharacterId));
    }
}
