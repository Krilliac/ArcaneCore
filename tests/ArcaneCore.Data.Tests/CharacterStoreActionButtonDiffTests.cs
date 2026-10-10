using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The action bar is saved as a diff against the stored rows: a button whose action and type are unchanged issues no statement
/// (the autosave rewrote every button of every online character every interval), a changed one is updated, a removed one deleted and
/// a new one inserted (docs/areas/persistence.md). The statements are counted on the wire with a command interceptor.
/// </summary>
public sealed partial class CharacterStoreActionButtonDiffTests : IAsyncLifetime
{
    private const byte Spell = 0x00;
    private const byte Macro = 0x40;
    private const byte Item = 0x80;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UnchangedButtonsIssueNoStatements(DatabaseProvider provider)
    {
        (CharacterDbContext db, EfCharacterStore store, StatementLog log, int id) = await OpenAsync(provider);
        await using (db)
        {
            ActionButton[] bar = [new ActionButton(0, 6603, Spell), new ActionButton(1, 133, Macro), new ActionButton(119, 0xFFFFFF, Item)];
            await store.SaveStateAsync(State(id, bar));
            // The first save writes the bar. How many INSERT statements that takes is the provider's batching, not the contract:
            // Pomelo (MariaDB/MySQL) sends the three new rows as ONE multi-row INSERT, SQLite and Npgsql as one per row. The count
            // is therefore bounded (at least one, never more than one per button) and the content is checked by reading it back.
            Assert.InRange(log.Count("INSERT"), 1, bar.Length);
            Assert.Equal(bar.OrderBy(b => b.Button), (await store.GetActionButtonsAsync(id)).OrderBy(b => b.Button));
            log.Clear();

            await store.SaveStateAsync(State(id, bar));
            await store.SaveStateAsync(State(id, [.. bar.Reverse()])); // order of the list is not a change either

            Assert.Equal((0, 0, 0), (log.Count("INSERT"), log.Count("UPDATE"), log.Count("DELETE"))); // delete-and-reinsert issued 3 + 3 per save
            Assert.Equal(bar.OrderBy(b => b.Button), (await store.GetActionButtonsAsync(id)).OrderBy(b => b.Button));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ChangedRemovedAndNewButtonsAreUpdatedDeletedInserted(DatabaseProvider provider)
    {
        (CharacterDbContext db, EfCharacterStore store, StatementLog log, int id) = await OpenAsync(provider);
        await using (db)
        {
            await store.SaveStateAsync(State(id, [new ActionButton(0, 6603, Spell), new ActionButton(1, 133, Macro), new ActionButton(2, 25, Item), new ActionButton(3, 9, Spell)]));
            log.Clear();

            await store.SaveStateAsync(State(id,
            [
                new ActionButton(0, 6603, Spell), // unchanged
                new ActionButton(1, 134, Macro),  // action changed
                new ActionButton(3, 9, Item),     // only the type changed
                new ActionButton(7, 77, Spell),   // new
            ])); // button 2 is gone

            Assert.Equal((1, 2, 1), (log.Count("INSERT"), log.Count("UPDATE"), log.Count("DELETE")));
            db.ChangeTracker.Clear();
            Assert.Equal(
                [new ActionButton(0, 6603, Spell), new ActionButton(1, 134, Macro), new ActionButton(3, 9, Item), new ActionButton(7, 77, Spell)],
                (await store.GetActionButtonsAsync(id)).OrderBy(b => b.Button).ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NullLeavesTheBarAloneAndAnEmptyListClearsIt(DatabaseProvider provider)
    {
        (CharacterDbContext db, EfCharacterStore store, StatementLog log, int id) = await OpenAsync(provider);
        await using (db)
        {
            await store.SaveStateAsync(State(id, [new ActionButton(0, 6603, Spell), new ActionButton(1, 133, Macro)]));
            log.Clear();

            await store.SaveStateAsync(State(id, null)); // an optional section left out is not a clear
            Assert.Equal(0, log.Count("DELETE") + log.Count("INSERT") + log.Count("UPDATE"));
            Assert.Equal(2, (await store.GetActionButtonsAsync(id)).Count);

            await store.SaveStateAsync(State(id, []));
            Assert.Equal(2, log.Count("DELETE"));
            db.ChangeTracker.Clear();
            Assert.Empty(await store.GetActionButtonsAsync(id));

            // And a bar can be written again after being emptied.
            await store.SaveStateAsync(State(id, [new ActionButton(4, 1, Spell)]));
            db.ChangeTracker.Clear();
            Assert.Equal([new ActionButton(4, 1, Spell)], await store.GetActionButtonsAsync(id));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OnlyTheSavedCharactersButtonsAreTouched(DatabaseProvider provider)
    {
        (CharacterDbContext db, EfCharacterStore store, StatementLog log, int id) = await OpenAsync(provider);
        await using (db)
        {
            CharacterRecord other = await store.CreateAsync(new CharacterRecord { AccountId = 5, Name = "Bystander" });
            await store.SaveStateAsync(State(other.Id, [new ActionButton(0, 1, Spell), new ActionButton(1, 2, Spell)]));
            await store.SaveStateAsync(State(id, [new ActionButton(0, 10, Spell)]));
            log.Clear();

            await store.SaveStateAsync(State(id, [new ActionButton(1, 11, Spell)])); // 0 removed, 1 added for this character only
            Assert.Equal((1, 0, 1), (log.Count("INSERT"), log.Count("UPDATE"), log.Count("DELETE")));
            db.ChangeTracker.Clear();
            Assert.Equal([new ActionButton(0, 1, Spell), new ActionButton(1, 2, Spell)], (await store.GetActionButtonsAsync(other.Id)).OrderBy(b => b.Button).ToArray());
        }
    }

    private static CharacterState State(int id, IReadOnlyList<ActionButton>? buttons)
        => new(id, 1, 14, 0, 0, 0, 0, 1, 10, ActionButtons: buttons);

    private async Task<(CharacterDbContext Db, EfCharacterStore Store, StatementLog Log, int Id)> OpenAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var log = new StatementLog();
        var builder = new DbContextOptionsBuilder<CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, cs);
        builder.AddInterceptors(log);
        var db = new CharacterDbContext(builder.Options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);
        CharacterRecord created = await store.CreateAsync(new CharacterRecord { AccountId = 4, Name = "Archer" });
        return (db, store, log, created.Id);
    }

    [GeneratedRegex(@"(?<verb>INSERT INTO|UPDATE|DELETE FROM)\s+[`""\[]?character_action\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActionStatement();

    /// <summary>Counts the statements that touch character_action, however EF batches them into commands.</summary>
    private sealed class StatementLog : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _verbs = new();

        public int Count(string verb) => _verbs.Count(v => v.StartsWith(verb, StringComparison.OrdinalIgnoreCase));

        public void Clear() => _verbs.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            foreach (Match match in ActionStatement().Matches(command.CommandText))
            {
                _verbs.Enqueue(match.Groups["verb"].Value);
            }
        }
    }
}
