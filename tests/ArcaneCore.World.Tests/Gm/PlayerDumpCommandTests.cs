using System.Diagnostics;
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
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Gm;

/// <summary><c>.pdump write</c> and <c>.pdump load</c> (vmangos HandlePDumpWriteCommand / HandlePDumpLoadCommand) on a running world.</summary>
public sealed class PlayerDumpCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-pdump-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public PlayerDumpCommandTests(ITestOutputHelper output)
    {
        _output = output;
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private CharacterDbContext Db() => new(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(_connection).Options);

    private readonly ITestOutputHelper _output;
    private WorldTestHost? _host;

    /// <summary>
    /// Sends <paramref name="command"/> and reads its reply with the client's default 10 s bound. The step time is written to the test
    /// output. When the reply misses the bound, a second read is started at once (before any probe, so its time is when the line really
    /// arrived, not when the probes finished) while a thread-pool item and a world-thread command are timed; the failure then says how long
    /// each waited and what came late, with the text of the late line, so a stall reads as pool queueing, a stalled world tick, slow import
    /// work or an unrelated line. A connection that ends or a read that is cancelled instead of timing out is reported with its type, the
    /// step time and the command, in the primary read and in the late one.
    /// </summary>
    private async Task<string> RunAsync(WorldTestClient client, string command)
    {
        var step = Stopwatch.StartNew();
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        try
        {
            string text = (await client.ReadChatAsync()).Text;
            _output.WriteLine($"{command}: reply after {step.ElapsedMilliseconds} ms");
            return text;
        }
        catch (TimeoutException ex)
        {
            long waited = step.ElapsedMilliseconds;
            Task<string> lateRead = ReadLateAsync(client, step);
            long pool = await ProbeAsync(() => Task.Run(() => 0));
            long world = _host is null ? -1 : await ProbeAsync(() => _host.World.InvokeAsync(() => 0));
            string late = await lateRead;
            throw new TimeoutException($"{command}: no reply within {waited} ms; a pool item then waited {pool} ms, a world-thread command {world} ms (-1: none); late read: {late}", ex);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            // Not the read bound: the connection ended or the read was cancelled. Say which, when, and for what command.
            throw new InvalidOperationException($"{command}: the read ended with {ex.GetType().Name} after {step.ElapsedMilliseconds} ms ({ex.Message})", ex);
        }
    }

    /// <summary>The next chat line up to 60 s more, described: when it came (ms since the send, taken inside the read), its text, or why none came.</summary>
    private static async Task<string> ReadLateAsync(WorldTestClient client, Stopwatch step)
    {
        try
        {
            string text = (await client.ReadChatAsync(TimeSpan.FromSeconds(60))).Text;
            return $"a line came {step.ElapsedMilliseconds} ms after the send: \"{text}\"";
        }
        catch (TimeoutException)
        {
            return "no chat line within 60 s more";
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            return $"the read ended with {ex.GetType().Name} after {step.ElapsedMilliseconds} ms ({ex.Message})";
        }
    }

    private static async Task<long> ProbeAsync(Func<Task<int>> probe)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await probe().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            return 30_000;
        }

        return watch.ElapsedMilliseconds;
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
        _host = host;
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
        _host = host;
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
