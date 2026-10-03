using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The petition tables (<see cref="PetitionDataModule"/>) on every engine: petitions with signatures
/// round trip, rename and delete cleanly, a name's multi-byte text and case variants are storable
/// (uniqueness is in memory), the one unique owner index holds, the turn-in write is one transaction
/// and character deletion removes a character's petition and signatures. Written against real
/// provider semantics; only SQLite executes on a machine without MariaDB/PostgreSQL servers.
/// </summary>
public sealed class PetitionStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task APetitionWithNineSignatures_RoundTrips_IsRenamed_AndDeletedWithItsSignatures(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        PetitionData nine = Petition(1, owner: 10, charter: 700, "Arcane Order", signers: Enumerable.Range(20, 9));
        PetitionData other = Petition(2, owner: 11, charter: 701, "Other", signers: [30]);
        await WithStore(cs, async store =>
        {
            await store.SavePetitionAsync(nine);
            await store.SavePetitionAsync(other);
        });

        await WithStore(cs, async store =>
        {
            IReadOnlyList<PetitionData> loaded = await store.GetPetitionsAsync();
            Assert.Equal(2, loaded.Count);
            AssertPetition(nine, loaded.Single(p => p.Id == 1));
            AssertPetition(other, loaded.Single(p => p.Id == 2));

            // Rename, drop two signatures, add one: the stored set becomes exactly the saved set.
            PetitionData changed = nine with
            {
                Name = "Renamed Order",
                Signatures = [.. nine.Signatures.Skip(2), new PetitionSignatureData(99, 99)],
            };
            await store.SavePetitionAsync(changed);
        });

        await WithStore(cs, async store =>
        {
            PetitionData reloaded = (await store.GetPetitionsAsync()).Single(p => p.Id == 1);
            Assert.Equal("Renamed Order", reloaded.Name);
            Assert.Equal(8, reloaded.Signatures.Count);
            Assert.Contains(new PetitionSignatureData(99, 99), reloaded.Signatures);
            Assert.DoesNotContain(reloaded.Signatures, s => s.PlayerId is 20 or 21);

            await store.DeletePetitionAsync(1);
            await store.DeletePetitionAsync(404); // a missing petition is a no-op
        });

        await WithStore(cs, async store => AssertPetition(other, Assert.Single(await store.GetPetitionsAsync())));
        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(1, await read.Set<PetitionSignRow>().CountAsync()); // only the other petition's signature is left
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MultiByteNamesAtTheLimit_RoundTrip_AndNamesDifferingOnlyByCase_Coexist(DatabaseProvider provider)
    {
        // VARCHAR(24) counts characters on MariaDB and PostgreSQL, is unlimited on SQLite; the collation
        // differs per engine, so case variants must be storable (the world thread checks uniqueness).
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        string cyrillic = string.Concat(Enumerable.Repeat("Д", 24));
        string accented = string.Concat(Enumerable.Repeat("é", 24));
        await WithStore(cs, async store =>
        {
            await store.SavePetitionAsync(Petition(1, 10, 700, cyrillic, []));
            await store.SavePetitionAsync(Petition(2, 11, 701, accented, []));
            await store.SavePetitionAsync(Petition(3, 12, 702, "Arcane", []));
            await store.SavePetitionAsync(Petition(4, 13, 703, "ARCANE", []));
        });

        await WithStore(cs, async store =>
        {
            IReadOnlyList<PetitionData> loaded = await store.GetPetitionsAsync();
            Assert.Equal([cyrillic, accented, "Arcane", "ARCANE"], loaded.OrderBy(p => p.Id).Select(p => p.Name));
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ASecondPetitionForTheSameOwner_Fails_AndLeavesTheFirstIntact(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        PetitionData first = Petition(1, owner: 10, charter: 700, "First", signers: [20, 21]);
        await WithStore(cs, store => store.SavePetitionAsync(first));

        await Assert.ThrowsAnyAsync<Exception>(() => WithStore(cs, store => store.SavePetitionAsync(Petition(2, owner: 10, charter: 701, "Second", []))));

        // A fresh scope afterwards works (matters on PostgreSQL, where a failed statement poisons the transaction).
        await WithStore(cs, async store =>
        {
            AssertPetition(first, Assert.Single(await store.GetPetitionsAsync()));
            await store.SavePetitionAsync(Petition(3, owner: 11, charter: 702, "Third", []));
        });
        await WithStore(cs, async store => Assert.Equal(2, (await store.GetPetitionsAsync()).Count));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CompletePetition_StoresTheGuildAndRemovesThePetition_InOneCommit(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        PetitionData petition = Petition(1, owner: 10, charter: 700, "Arcane", signers: [20, 21]);
        await WithStore(cs, store => store.SavePetitionAsync(petition));
        GuildData guild = Guild(1, "Arcane", leader: 10, members: [10, 20, 21]);

        await WithStore(cs, store => store.CompletePetitionAsync(guild, 1));

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Empty(await read.Set<PetitionRow>().ToListAsync());
        Assert.Empty(await read.Set<PetitionSignRow>().ToListAsync());
        GuildData stored = Assert.Single(await new EfSocialStore(read).GetGuildsAsync());
        Assert.Equal("Arcane", stored.Name);
        Assert.Equal(5, stored.Ranks.Count);
        Assert.Equal([10, 20, 21], stored.Members.Select(m => m.CharacterId).Order());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CompletePetition_WithAMemberOfAnotherGuild_RollsBackEverything(DatabaseProvider provider)
    {
        // guild_member has a unique character index: the signer who joined another guild meanwhile
        // fails the whole call on every engine, leaving the petition, its signatures and the other guild untouched.
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        PetitionData petition = Petition(1, owner: 10, charter: 700, "Arcane", signers: [20, 21]);
        GuildData taken = Guild(7, "Taken", leader: 21, members: [21]);
        await WithStore(cs, store => store.SavePetitionAsync(petition));
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfSocialStore(db).SaveGuildAsync(taken);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => WithStore(cs, store => store.CompletePetitionAsync(Guild(1, "Arcane", leader: 10, members: [10, 20, 21]), 1)));

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        PetitionData kept = Assert.Single(await new EfPetitionStore(read).GetPetitionsAsync());
        AssertPetition(petition, kept);
        GuildData only = Assert.Single(await new EfSocialStore(read).GetGuildsAsync());
        Assert.Equal("Taken", only.Name);
        Assert.Equal([21], only.Members.Select(m => m.CharacterId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeletingACharacter_RemovesItsPetitionAndItsSignatures_ButKeepsOthers(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        (int owner, int signer, int bystander) = await CreateCharactersAsync(cs);
        await WithStore(cs, async store =>
        {
            await store.SavePetitionAsync(Petition(1, owner, 700, "Arcane", [signer, bystander]));
            await store.SavePetitionAsync(Petition(2, bystander, 701, "Other", [signer]));
        });

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(signer, 1));
        }

        await WithStore(cs, async store =>
        {
            IReadOnlyList<PetitionData> afterSigner = await store.GetPetitionsAsync();
            Assert.Equal([bystander], afterSigner.Single(p => p.Id == 1).Signatures.Select(s => s.PlayerId));
            Assert.Empty(afterSigner.Single(p => p.Id == 2).Signatures);
        });

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(owner, 1));
        }

        await WithStore(cs, async store => Assert.Equal([2], (await store.GetPetitionsAsync()).Select(p => p.Id)));
        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(0, await read.Set<PetitionSignRow>().CountAsync(s => s.PetitionId == 1));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ALatePurge_KeepsTheRecreatedCharactersPetition_AndRemovesAnOrphans(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        (int owner, int signer, _) = await CreateCharactersAsync(cs);
        await WithStore(cs, store => store.SavePetitionAsync(Petition(1, owner, 700, "Arcane", [signer])));

        // The id still has a character row: a queued purge must not wipe it.
        await WithStore(cs, store => store.PurgeCharacterAsync(owner));
        await WithStore(cs, store => store.PurgeCharacterAsync(signer));
        await WithStore(cs, async store => Assert.Single((await store.GetPetitionsAsync()).Single().Signatures));

        // An id with no character row is an orphan: its petition and signatures go.
        await WithStore(cs, store => store.SavePetitionAsync(Petition(2, 9000, 702, "Orphan", [9001])));
        await WithStore(cs, store => store.PurgeCharacterAsync(9000));
        await WithStore(cs, store => store.SavePetitionAsync(Petition(3, 9002, 703, "Orphan sign", [9003])));
        await WithStore(cs, store => store.PurgeCharacterAsync(9003));

        await WithStore(cs, async store =>
        {
            IReadOnlyList<PetitionData> left = await store.GetPetitionsAsync();
            Assert.Equal([1, 3], left.Select(p => p.Id));
            Assert.Empty(left.Single(p => p.Id == 3).Signatures);
        });
        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(0, await read.Set<PetitionSignRow>().CountAsync(s => s.PetitionId == 2));
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
    private static async Task WithStore(DatabaseConnectionOptions cs, Func<EfPetitionStore, Task> action)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await action(new EfPetitionStore(db));
    }

    private static async Task<(int Owner, int Signer, int Bystander)> CreateCharactersAsync(DatabaseConnectionOptions cs)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        var characters = new EfCharacterStore(db);
        int a = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Owner" })).Id;
        int b = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Signer" })).Id;
        int c = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Bystander" })).Id;
        return (a, b, c);
    }

    private static PetitionData Petition(int id, int owner, int charter, string name, IEnumerable<int> signers)
        => new(id, owner, charter, name, [.. signers.Select(s => new PetitionSignatureData(s, 1000 + s))]);

    private static void AssertPetition(PetitionData expected, PetitionData actual)
    {
        Assert.Equal(expected with { Signatures = [] }, actual with { Signatures = [] });
        Assert.Equal(expected.Signatures.OrderBy(s => s.PlayerId), actual.Signatures.OrderBy(s => s.PlayerId));
    }

    private static GuildData Guild(int id, string name, int leader, int[] members)
        => new(id, name, leader, "motd", "info", 1_700_000_000, -1, -1, -1, -1, -1,
            [
                new GuildRankData(0, "Guild Master", 0x1FF),
                new GuildRankData(1, "Officer", 0x1FF),
                new GuildRankData(2, "Veteran", 0x43),
                new GuildRankData(3, "Member", 0x43),
                new GuildRankData(4, "Initiate", 0x43),
            ],
            [.. members.Select(m => new GuildMemberData(m, (byte)(m == leader ? 0 : 4), string.Empty, string.Empty, 1, 0, 1_700_000_000))]);
}
