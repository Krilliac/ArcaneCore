using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Character;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private static async Task<string> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync()).Text;
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
}
