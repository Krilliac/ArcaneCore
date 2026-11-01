using System.Data.Common;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Content;

/// <summary>
/// The shipped content files <c>0004_item_enchant_procs.sql</c> (spell_proc_item_enchant) and <c>0005_spell_enchant_charges.sql</c> (spell_enchant_charges):
/// a clean world database gets the rows, a second run leaves the same rows, rows of other spell ids are not touched, and the PPM rows reach the catalog
/// the combat proc code reads.
/// </summary>
public sealed class EnchantSeedContentTests : IAsyncLifetime
{
    private static readonly (uint Entry, float Ppm)[] s_ppm = [(8034, 9f), (13897, 6f), (20004, 6f), (20005, 1.6f)];

    /// <summary>The 19 rows of the vmangos migration 20180830022134_world.sql.</summary>
    private static readonly (uint Entry, uint Charges)[] s_charges =
    [
        (8679, 40), (8686, 55), (8688, 70), (11338, 85), (11339, 100), (11340, 115), (5761, 50), (8693, 75), (11399, 100), (2823, 60),
        (2824, 75), (11355, 90), (11356, 105), (25351, 120), (13219, 60), (13225, 75), (13226, 90), (13227, 105), (14792, 15),
    ];

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static string ContentFile(DatabaseProvider provider, string name)
    {
        string root = RepositorySource.FindRoot() ?? throw new Xunit.Sdk.XunitException("the ArcaneCore checkout was not found");
        string folder = provider switch
        {
            DatabaseProvider.Sqlite => "sqlite",
            DatabaseProvider.MariaDb or DatabaseProvider.MySql => "mariadb",
            DatabaseProvider.PostgreSql => "postgresql",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        return Path.Combine(root, "sql", "content", "world", folder, name);
    }

    private static async Task RunAsync(WorldDbContext db, string script)
    {
        DbConnection connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<DatabaseConnectionOptions> CleanWorldAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        return cs;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ProcFile_OnACleanInstall_HasTheFourPpmRows_AndARerunIsANoOp(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CleanWorldAsync(provider);
        string script = await File.ReadAllTextAsync(ContentFile(provider, "0004_item_enchant_procs.sql"));
        for (int pass = 0; pass < 2; pass++)
        {
            await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
            {
                await RunAsync(db, script);
            }

            await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
            ItemEnchantProc[] rows = (await new EfItemEnchantProcStore(read).LoadAsync()).OrderBy(r => r.SpellId).ToArray();
            Assert.Equal(s_ppm.Select(p => new ItemEnchantProc(p.Entry, p.Ppm)).OrderBy(r => r.SpellId), rows);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ChargesFile_OnACleanInstall_HasTheVmangosRows_AndARerunIsANoOp(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CleanWorldAsync(provider);
        string script = await File.ReadAllTextAsync(ContentFile(provider, "0005_spell_enchant_charges.sql"));
        for (int pass = 0; pass < 2; pass++)
        {
            await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
            {
                await RunAsync(db, script);
            }

            await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
            SpellEnchantCharges[] rows = (await new EfSpellEnchantChargesStore(read).LoadAsync()).OrderBy(r => r.SpellId).ToArray();
            Assert.Equal(s_charges.Select(c => new SpellEnchantCharges(c.Entry, c.Charges)).OrderBy(r => r.SpellId), rows);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task BothFiles_LeaveRowsOfOtherSpellIdsAlone_AndReplaceAnEditedShippedRow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CleanWorldAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            db.Add(new ItemEnchantProcRow { Entry = 99001, PpmRate = 3f });
            db.Add(new ItemEnchantProcRow { Entry = 20005, PpmRate = 50f });
            db.Add(new SpellEnchantChargesRow { Entry = 99002, Charges = 7 });
            db.Add(new SpellEnchantChargesRow { Entry = 2823, Charges = 1 });
            await db.SaveChangesAsync();
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await RunAsync(db, await File.ReadAllTextAsync(ContentFile(provider, "0004_item_enchant_procs.sql")));
            await RunAsync(db, await File.ReadAllTextAsync(ContentFile(provider, "0005_spell_enchant_charges.sql")));
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        Dictionary<uint, float> ppm = await read.Set<ItemEnchantProcRow>().AsNoTracking().ToDictionaryAsync(r => r.Entry, r => r.PpmRate);
        Assert.Equal(3f, ppm[99001]);
        Assert.Equal(1.6f, ppm[20005]);
        Dictionary<uint, uint> charges = await read.Set<SpellEnchantChargesRow>().AsNoTracking().ToDictionaryAsync(r => r.Entry, r => r.Charges);
        Assert.Equal(7u, charges[99002]);
        Assert.Equal(60u, charges[2823]);
    }

    [Fact]
    public async Task ShippedPpmRows_ReachTheCatalog_SoACombatProcUsesPpmNotTheDefaultChance()
    {
        DatabaseConnectionOptions cs = await CleanWorldAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await RunAsync(db, await File.ReadAllTextAsync(ContentFile(DatabaseProvider.Sqlite, "0004_item_enchant_procs.sql")));
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        var catalog = new ItemEnchantmentCatalog(procs: await new EfItemEnchantProcStore(read).LoadAsync());
        foreach ((uint entry, float ppm) in s_ppm)
        {
            // SpellSystem.ItemCombatProcs: a positive override gives chance = weapon delay * ppm / 600 (percent); only a null or zero
            // override falls back to the enchant amount or delay / 600.
            float? rate = catalog.PpmRate(entry);
            Assert.True(rate is > 0, $"spell {entry} has no PPM override");
            Assert.Equal(ppm, rate!.Value);
        }

        // Crusader (20007) carries no row: it keeps the default chance, it is not given a PPM by accident.
        Assert.Null(catalog.PpmRate(20007));
        // A 2.6 s weapon: Icy Chill 1.6 PPM is 2600 * 1.6 / 600 = 6.93 percent per swing, Frostbrand 9 PPM is 39 percent.
        Assert.Equal(6.9333d, 2600f * catalog.PpmRate(20005)!.Value / 600f, 3);
        Assert.Equal(39d, 2600f * catalog.PpmRate(8034)!.Value / 600f, 3);
    }
}
