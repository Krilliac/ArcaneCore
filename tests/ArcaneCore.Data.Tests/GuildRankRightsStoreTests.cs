using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Kernel.Social;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Guild columns whose storage differs per engine, round-tripped through the real store. PIN tests:
/// they characterise the mapping and guard it on hosted CI; on this machine only SQLite executes
/// (MariaDB and PostgreSQL are written against their semantics, not run).
/// Rank rights are a uint (vmangos guild_rank.rights is unsigned, Guild.h GR_RIGHT_ALL 0x1FF but a
/// client may send any u32, GuildHandler.cpp HandleGuildRankOpcode): PostgreSQL has no unsigned
/// type, Npgsql maps it to bigint. Emblem values are signed (-1 = unset). VARCHAR lengths are
/// characters on MariaDB and PostgreSQL but unlimited on SQLite.
/// </summary>
public sealed class GuildRankRightsStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RankRights_AtTheUnsignedMaximum_RoundTrip(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        GuildData guild = Guild(uint.MaxValue, "Max");

        await WithStore(cs, store => store.SaveGuildAsync(guild));

        await WithStore(cs, async store =>
        {
            GuildData loaded = Assert.Single(await store.GetGuildsAsync());
            Assert.Equal(uint.MaxValue, loaded.Ranks.Single(r => r.RankId == 1).Rights);
            Assert.Equal(0u, loaded.Ranks.Single(r => r.RankId == 2).Rights);
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UnsetEmblem_AndMultiByteTextAtTheLimits_RoundTrip(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        string rank = string.Concat(Enumerable.Repeat("Д", 15));                 // 15 Cyrillic characters (rank name limit)
        string note = string.Concat(Enumerable.Repeat("é", 31));                 // 31 accented characters (note limit)
        string name = string.Concat(Enumerable.Repeat("漢", 24));                 // 24 CJK characters (guild name limit)
        GuildData guild = Guild(0x1FF, name) with
        {
            EmblemStyle = -1,
            EmblemColor = -1,
            BorderStyle = -1,
            BorderColor = -1,
            BackgroundColor = -1,
        };
        guild = guild with
        {
            Ranks = [.. guild.Ranks.Select(r => r.RankId == 4 ? r with { Name = rank } : r)],
            Members = [new GuildMemberData(10, 0, note, note, 60, 1519, 1_700_000_100)],
        };

        await WithStore(cs, store => store.SaveGuildAsync(guild));

        await WithStore(cs, async store =>
        {
            GuildData loaded = Assert.Single(await store.GetGuildsAsync());
            Assert.Equal(name, loaded.Name);
            Assert.Equal((-1, -1, -1, -1, -1), (loaded.EmblemStyle, loaded.EmblemColor, loaded.BorderStyle, loaded.BorderColor, loaded.BackgroundColor));
            Assert.Equal(rank, loaded.Ranks.Single(r => r.RankId == 4).Name);
            Assert.Equal(note, loaded.Members.Single().PublicNote);
            Assert.Equal(note, loaded.Members.Single().OfficerNote);
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task GuildNames_DifferingOnlyByCase_AreBothStorable(DatabaseProvider provider)
    {
        // Uniqueness is enforced in memory on the world thread (collations differ per engine), so the
        // store must not reject a case variant; GuildManager.GetByName is the case-insensitive check.
        DatabaseConnectionOptions cs = await CreateAsync(provider);

        await WithStore(cs, async store =>
        {
            await store.SaveGuildAsync(Guild(0x1FF, "Arcane", id: 1, leader: 10));
            await store.SaveGuildAsync(Guild(0x1FF, "ARCANE", id: 2, leader: 20));
        });

        await WithStore(cs, async store => Assert.Equal(2, (await store.GetGuildsAsync()).Count));
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

    private static async Task WithStore(DatabaseConnectionOptions cs, Func<EfSocialStore, Task> action)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await action(new EfSocialStore(db));
    }

    private static GuildData Guild(uint officerRights, string name, int id = 1, int leader = 10)
        => new(id, name, leader, "motd", "info", 1_700_000_000, 1, 2, 3, 4, 5,
            [
                new GuildRankData(0, "Guild Master", 0x1FF),
                new GuildRankData(1, "Officer", officerRights),
                new GuildRankData(2, "Veteran", 0),
                new GuildRankData(3, "Member", 0x43),
                new GuildRankData(4, "Initiate", 0x43),
            ],
            [new GuildMemberData(leader, 0, string.Empty, string.Empty, 60, 12, 1_700_000_100)]);
}
