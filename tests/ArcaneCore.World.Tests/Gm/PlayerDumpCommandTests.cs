using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Character;
using ArcaneCore.World.RealmList;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary><c>.pdump write</c> and <c>.pdump load</c> (vmangos HandlePDumpWriteCommand / HandlePDumpLoadCommand) on a running world.</summary>
public sealed class PlayerDumpCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-pdump-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public PlayerDumpCommandTests() => _connection.Open();

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private CharacterDbContext Db() => new(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(_connection).Options);

    /// <summary>
    /// Hang guard of one reply. <c>.pdump</c> replies come from pool-thread database and file work posted back to the world thread, so a
    /// host starved by a full gate (gate 1c7134022: this test took 84 s while minute-long scenario tests ran beside it; alone it takes
    /// under a second) can exceed the client's 10 s default with nothing wrong. The reply text is still asserted exactly.
    /// </summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(60);

    private static async Task<string> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync(ReplyTimeout)).Text;
    }

    [Fact]
    public void TheCommands_NeedAnAdministrator()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("pdump load", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("pdump load", AccountSecurity.Administrator));
        Assert.NotNull(table.Resolve("pdump write", AccountSecurity.Administrator));
    }

    [Fact]
    public async Task WriteThenLoad_ClonesTheCharacterIntoTheAccount()
    {
        await using (CharacterDbContext db = Db())
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        }

        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddDbContext<CharacterDbContext>(o => o.UseSqlite(_connection));
            services.AddSingleton(new PlayerDumpOptions { Directory = _dir });
        });
        await using WorldTestClient gm = await host.EnterWorldAsync("PDGM", "Pdgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        int source;
        await using (CharacterDbContext db = Db())
        {
            var row = new CharacterRecord { AccountId = 77, Name = "Dumpme", Level = 30, Race = 1, Class = 1 };
            db.Characters.Add(row);
            await db.SaveChangesAsync();
            source = row.Id;
            db.Add(new CharacterSpellRow { CharacterId = source, Spell = 133 });
            await db.SaveChangesAsync();
        }

        CharacterDirectory directory = host.WorldServices.GetRequiredService<CharacterDirectory>();
        directory.Add(new CharacterIdentity(source, 77, "Dumpme", 1, 0, 1, 30));

        Assert.Equal("Failed to open file: ../x", await RunAsync(gm, ".pdump write ../x Dumpme"));
        Assert.Equal("Character dumped successfully!", await RunAsync(gm, ".pdump write dumpme.txt Dumpme"));
        Assert.True(File.Exists(Path.Combine(_dir, "dumpme.txt")));

        Assert.Equal("Invalid character name!", await RunAsync(gm, ".pdump load dumpme.txt PDGM x1"));
        Assert.Equal("Account NOBODY does not exist.", await RunAsync(gm, ".pdump load dumpme.txt nobody Clone"));
        Assert.Equal("Failed to open file: missing.txt", await RunAsync(gm, ".pdump load missing.txt PDGM Clone"));
        Assert.Equal("Character loaded successfully!", await RunAsync(gm, ".pdump load dumpme.txt PDGM Clone"));

        int account = (await host.Accounts.FindByUsernameAsync("PDGM"))!.Id;
        CharacterIdentity clone = directory.FindByName("Clone")!;
        Assert.Equal((account, (byte)30), (clone.AccountId, clone.Level));
        await using (CharacterDbContext db = Db())
        {
            Assert.Equal([133u], await db.Set<CharacterSpellRow>().Where(r => r.CharacterId == clone.Id).Select(r => r.Spell).ToListAsync());
        }

        // The dump's own name is taken by now, and so is the requested one: refused, nothing stored.
        Assert.Equal("Invalid character name!", await RunAsync(gm, ".pdump load dumpme.txt PDGM"));
        File.WriteAllText(Path.Combine(_dir, "broken.txt"), "characters\t{oops\n");
        Assert.Equal("Dump file have broken data!", await RunAsync(gm, ".pdump load broken.txt PDGM Other"));
    }

    /// <summary>
    /// A successful import recounts the owner and stores it, so a stale 0 (which the realm's irreversible <c>account delete</c> trusts) cannot
    /// survive it. The host's character store is the in-memory one, which holds the GM's own character on the account, so the recount is 1; the
    /// seeded 0 stands for the count the world recorded before the import. Without the refresh the 0 stays.
    /// </summary>
    [Theory]
    [InlineData(7, true)]
    [InlineData(0, false)]
    public async Task Load_RefreshesTheOwnersRealmCharacterCount(int realmId, bool expectRefresh)
    {
        await using (CharacterDbContext db = Db())
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        }

        var counts = new CountStore();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddDbContext<CharacterDbContext>(o => o.UseSqlite(_connection));
            services.AddSingleton(new PlayerDumpOptions { Directory = _dir });
            services.AddSingleton<IRealmCharacterCountStore>(counts);
            services.AddSingleton(Options.Create(new RealmStatusOptions { Id = realmId }));
        });
        await using WorldTestClient gm = await host.EnterWorldAsync("PDGM", "Pdgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        int account = (await host.Accounts.FindByUsernameAsync("PDGM"))!.Id;
        Assert.Equal(1, await host.Characters.CountByAccountAsync(account));

        await using (CharacterDbContext db = Db())
        {
            var row = new CharacterRecord { AccountId = 77, Name = "Dumpme", Level = 30, Race = 1, Class = 1 };
            db.Characters.Add(row);
            await db.SaveChangesAsync();
            host.WorldServices.GetRequiredService<CharacterDirectory>().Add(new CharacterIdentity(row.Id, 77, "Dumpme", 1, 0, 1, 30));
        }

        Assert.Equal("Character dumped successfully!", await RunAsync(gm, ".pdump write dumpme.txt Dumpme"));
        await counts.SetCountAsync(7, account, 0); // the stale 0
        int writesBefore = counts.Writes;

        Assert.Equal("Character loaded successfully!", await RunAsync(gm, ".pdump load dumpme.txt PDGM Clone"));

        Assert.Equal(expectRefresh ? 1 : 0, (await counts.GetCountsAsync(account))[7]);
        Assert.Equal(expectRefresh ? writesBefore + 1 : writesBefore, counts.Writes);
    }

    private sealed class CountStore : IRealmCharacterCountStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Realm, int Account), int> _counts = new();
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public Task<IReadOnlyDictionary<int, int>> GetCountsAsync(int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<int, int>>(_counts.Where(c => c.Key.Account == accountId).ToDictionary(c => c.Key.Realm, c => c.Value));

        public Task SetCountAsync(int realmId, int accountId, int count, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            _counts[(realmId, accountId)] = count;
            return Task.CompletedTask;
        }

        public Task<int> DeleteByAccountAsync(int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(_counts.Keys.Where(k => k.Account == accountId).Count(k => _counts.TryRemove(k, out _)));
    }
}
