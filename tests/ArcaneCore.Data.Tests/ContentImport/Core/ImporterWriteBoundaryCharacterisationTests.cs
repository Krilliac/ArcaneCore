using System.Transactions;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Data.World.PlayerStats;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Core;

/// <summary>
/// Characterises the hand-written savepoint/commit/rollback boundary each dump importer's
/// <c>WriteAsync</c> carries today: the per-importer precondition wording (exception type and text),
/// and that a failed write leaves the tracker empty and <c>AutoDetectChangesEnabled</c> restored to the
/// caller's value. Pins what must survive routing the five importers through
/// <see cref="ArcaneCore.Data.Content.Import.ImportTransaction"/>. Dump rows are synthetic.
/// </summary>
public sealed class ImporterWriteBoundaryCharacterisationTests : IAsyncLifetime
{
    private const string TrackerSuffix = "; save caller changes and clear tracking, or use a dedicated context.";
    private const string AmbientSuffix = " when a caller owns the transaction; ambient transactions are not supported.";

    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    public static IEnumerable<object[]> Importers() => new[] { "conditions", "npc", "creature", "gameobjectloot", "playerstats" }.Select(n => new object[] { n });

    private static Func<WorldDbContext, bool, CancellationToken, Task> Writer(string name)
    {
        switch (name)
        {
            case "conditions":
                var conditions = new ConditionsDumpImporter();
                conditions.Read(new StringReader("INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES (1,14,0,2,0,0,0);"));
                return (db, replace, ct) => conditions.WriteAsync(db, replace, ct);
            case "npc":
                var npc = new NpcDumpImporter();
                npc.Read(new StringReader("INSERT INTO `npc_vendor_template` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (133,2901,2,60,102,0);"));
                return (db, replace, ct) => npc.WriteAsync(db, replace, ct);
            case "creature":
                var creature = new CreatureDumpImporter();
                creature.Read(new StringReader("INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (901,'Imported',1,1);"));
                return (db, replace, ct) => creature.WriteAsync(db, replace, ct);
            case "gameobjectloot":
                var gameObject = new GameObjectLootDumpImporter();
                gameObject.Read(new StringReader("INSERT INTO `gameobject_template` (`entry`,`type`,`displayId`,`name`) VALUES (902,3,1,'Imported object');"));
                return (db, replace, ct) => gameObject.WriteAsync(db, replace, ct);
            case "playerstats":
                var stats = new PlayerStatsDumpImporter();
                stats.Read(new StringReader("INSERT INTO `player_xp_for_level` (`lvl`,`xp_for_next_level`) VALUES (1,555);"));
                return (db, replace, ct) => stats.WriteAsync(db, replace, ct);
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private static (string Tracker, string Ambient) Wording(string name) => name switch
    {
        "conditions" => ("Conditions imports require an empty change tracker" + TrackerSuffix, "Conditions imports require an explicit EF transaction" + AmbientSuffix),
        "npc" => ("NPC imports require an empty change tracker.", "NPC imports require an explicit transaction."),
        "creature" => ("Creature imports require an empty change tracker" + TrackerSuffix, "Creature imports require an explicit EF transaction" + AmbientSuffix),
        "gameobjectloot" => ("Game object/loot imports require an empty change tracker" + TrackerSuffix, "Game object/loot imports require an explicit EF transaction" + AmbientSuffix),
        "playerstats" => ("Player stats imports require an empty change tracker" + TrackerSuffix, "Player stats imports require an explicit EF transaction" + AmbientSuffix),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Importers))]
    public async Task NullContext_IsAnArgumentNullException(string importer)
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Writer(importer)(null!, false, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Importers))]
    public async Task TrackedCallerState_IsRefusedWithTheImportersOwnWording(string importer)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        db.ClassInfo.Add(new ClassInfoRow { Class = 2, BaseHealth = 222 });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Writer(importer)(db, false, CancellationToken.None));

        Assert.Equal(Wording(importer).Tracker, error.Message);
        Assert.Single(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [MemberData(nameof(Importers))]
    public async Task AmbientTransaction_IsRefusedWithTheImportersOwnWording(string importer)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);

        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Writer(importer)(db, false, CancellationToken.None));

        Assert.Equal(Wording(importer).Ambient, error.Message);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [MemberData(nameof(Importers))]
    public async Task FailedWrite_RethrowsTheOriginalError_ClearsTheTracker_AndRestoresAutoDetectChanges(string importer)
    {
        foreach (bool autoDetect in new[] { true, false })
        {
            DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
            Func<WorldDbContext, bool, CancellationToken, Task> write = Writer(importer);
            await using (WorldDbContext seed = TestContexts.Create<WorldDbContext>(connection))
            {
                await SchemaBootstrapper.EnsureAsync(seed, WorldDbContext.Schema);
                await write(seed, false, CancellationToken.None);
            }

            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
            db.ChangeTracker.AutoDetectChangesEnabled = autoDetect;

            // The same keys again without replace collide; the original error must surface, not an AggregateException.
            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => write(db, false, CancellationToken.None));

            Assert.IsNotType<AggregateException>(error);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(autoDetect, db.ChangeTracker.AutoDetectChangesEnabled);
        }
    }

    [Theory]
    [MemberData(nameof(Importers))]
    public async Task SuccessfulWrite_LeavesNoTransactionOrTrackedRows_AndRestoresAutoDetectChanges(string importer)
    {
        foreach (bool autoDetect in new[] { true, false })
        {
            DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.ChangeTracker.AutoDetectChangesEnabled = autoDetect;

            await Writer(importer)(db, false, CancellationToken.None);

            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(autoDetect, db.ChangeTracker.AutoDetectChangesEnabled);
        }
    }

    [Theory]
    [MemberData(nameof(Importers))]
    public async Task CallerTransaction_StaysOpenAndTheirs_AfterSuccessAndAfterFailure(string importer)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        Func<WorldDbContext, bool, CancellationToken, Task> write = Writer(importer);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        await using var caller = await db.Database.BeginTransactionAsync();

        await write(db, false, CancellationToken.None);
        Assert.Same(caller, db.Database.CurrentTransaction);

        await Assert.ThrowsAnyAsync<Exception>(() => write(db, false, CancellationToken.None));
        Assert.Same(caller, db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());

        // The savepoint rolled the failed attempt back only: a replace import now succeeds inside the same transaction.
        await write(db, true, CancellationToken.None);
        Assert.Same(caller, db.Database.CurrentTransaction);
        await caller.RollbackAsync();
    }
}
