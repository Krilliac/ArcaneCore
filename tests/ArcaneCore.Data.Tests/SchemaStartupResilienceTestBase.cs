using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Repeated, interrupted and concurrent startup. DDL on MariaDB is not transactional, so a
/// startup that dies halfway leaves a half-applied step; the next startup must converge on the
/// same schema as an uninterrupted one. A start-up race between the realm and the world daemon
/// (both run the auth bootstrap) must not corrupt or fail either.
/// </summary>
public abstract class SchemaStartupResilienceTestBase : IAsyncLifetime
{
    /// <summary>The environment variable that overrides <see cref="FaultParallelism"/> (an integer, at least 1).</summary>
    internal const string FaultParallelismVariable = "ARCANECORE_TEST_FAULT_PARALLELISM";

    internal const int DefaultFaultParallelism = 4;

    /// <summary>How many injected faults of one theory case run at once; see <see cref="RunFaultsAsync{T}"/>.</summary>
    internal static int FaultParallelism { get; } = ReadFaultParallelism(Environment.GetEnvironmentVariable(FaultParallelismVariable));

    private protected readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    /// <summary>Every (provider, component, step) of the characters and world upgrade lines.</summary>
    public static IEnumerable<object[]> UpgradeSteps()
    {
        foreach (object[] provider in TestDatabases.AvailableProviders())
        {
            // A reserved-gap placeholder (Schema/ReservedSchemaGaps.cs) is an empty step: there is no DDL to interrupt.
            foreach (SchemaStep step in CharacterDbContext.Schema.Steps.Where(s => s.Changes.Count > 0))
            {
                yield return [provider[0], "characters", step.Version];
            }

            foreach (SchemaStep step in WorldDbContext.Schema.Steps)
            {
                yield return [provider[0], "world", step.Version];
            }
        }
    }

    public static IEnumerable<object[]> ProviderComponents()
    {
        foreach (object[] provider in TestDatabases.AvailableProviders())
        {
            foreach (object[] component in SchemaProbe.Components())
            {
                yield return [provider[0], component[0]];
            }
        }
    }

    /// <summary>Fresh databases for every component on every provider; the SQLite-only populated baseline for the two components with upgrades.</summary>
    public static IEnumerable<object[]> ConcurrentCases()
    {
        foreach (object[] pc in ProviderComponents())
        {
            yield return [pc[0], false, pc[1]];
        }

        foreach (string component in new[] { "characters", "world" })
        {
            yield return [DatabaseProvider.Sqlite, true, component];
        }
    }

    /// <summary>
    /// The body of the interrupted-upgrade-step theory; the slice classes each run a share of <see cref="UpgradeSteps"/> through it, so the rows run in parallel.
    /// The legacy database and the steps before this one are replayed once, and every fault runs on its own clone of the result, because the replay is the same for every fault of the step.
    /// For every step a clone is compared with the replay, and after the faults the replay itself runs the step and must issue the DDL count the fault list was built from.
    /// </summary>
    private protected async Task RunInterruptedUpgradeStepAsync(DatabaseProvider provider, string component, int stepVersion)
    {
        DatabaseConnectionOptions replay = await PrepareLegacyAsync(provider, component, stepVersion - 1, stepVersion);
        try
        {
            TestDatabases.DatabaseImage image = await TestDatabases.CaptureAsync(replay);
            DatabaseConnectionOptions probe = await _databases.CloneAsync(image);
            await AssertSameDatabaseAsync(replay, probe, $"{provider} {component} clone before step {stepVersion}");

            // How many DDL statements does this step issue? Measured, not assumed.
            var counter = new CommandTap();
            await EnsureAsync(component, probe, stepVersion, counter);
            await _databases.ReleaseAsync(probe);
            int ddl = counter.Ddl;
            // A placeholder step that holds a reserved version open (a lane schema gap class) has no changes, and a content step (world 64, the shipped bot ladder)
            // only inserts missing rows and issues no DDL: in both only the row writes and the version write can be interrupted.
            SchemaStep step = (component == "world" ? WorldDbContext.Schema : CharacterDbContext.Schema).Steps.Single(s => s.Version == stepVersion);
            Assert.True(ddl > 0 || step.Changes.All(c => c is EnsureRowChange), $"{component} step {stepVersion} issued no DDL");

            Func<string, bool> isVersionWrite = sql => sql.Contains(component + "_schema", StringComparison.Ordinal)
                && CommandTap.IsWrite(sql, "UPDATE", "INSERT");

            var faults = new List<(string Label, Func<string, bool> When, int On)>();
            for (int k = 1; k <= ddl; k++)
            {
                faults.Add(($"after DDL #{k}", CommandTap.IsDdl, k));
            }

            faults.Add(("after the version write", isVersionWrite, 1));

            await RunFaultsAsync(faults, FaultParallelism, async fault =>
            {
                (string label, Func<string, bool> when, int on) = fault;
                DatabaseConnectionOptions connection = await _databases.CloneAsync(image);
                try
                {
                    var tap = new CommandTap(when, on);
                    await AssertInjectedAsync(() => EnsureAsync(component, connection, stepVersion, tap), $"{provider} {component} step {stepVersion} {label} (probe saw {ddl} DDL)", tap);

                    // A restart under the current definition must reach the same schema as an uninterrupted start.
                    try
                    {
                        await SchemaProbe.EnsureCurrentAsync(component, connection);
                    }
                    catch (Exception ex)
                    {
                        throw new Xunit.Sdk.XunitException($"{component} step {stepVersion} interrupted {label}: restart failed: {ex.GetType().Name}: {ex.Message}");
                    }

                    await AssertConvergedAsync(component, connection, $"{component} step {stepVersion} {label}");
                }
                finally
                {
                    await _databases.ReleaseAsync(connection);
                }
            });

            // The source stayed untouched for the clones. A clone that took another path through the step than the replay had other crash points than the real step, so the list above would be short.
            var onReplay = new CommandTap();
            await EnsureAsync(component, replay, stepVersion, onReplay);
            Assert.True(ddl == onReplay.Ddl, $"{provider} {component} step {stepVersion}: the clone issued {ddl} DDL statements, the replay {onReplay.Ddl}");
        }
        finally
        {
            await _databases.ReleaseAsync(replay);
        }
    }

    /// <summary>Both databases must hold exactly the same definitions and rows (<see cref="TestDatabases.DescribeAsync"/>). Two empty descriptions prove nothing, so an empty one is refused.</summary>
    private protected static async Task AssertSameDatabaseAsync(DatabaseConnectionOptions expected, DatabaseConnectionOptions actual, string label)
    {
        IReadOnlyList<string> want = await TestDatabases.DescribeAsync(expected);
        IReadOnlyList<string> got = await TestDatabases.DescribeAsync(actual);
        Assert.True(want.Count > 0, $"{label}: nothing to compare");
        string[] missing = [.. want.Except(got)];
        string[] extra = [.. got.Except(want)];
        Assert.True(
            want.Count == got.Count && missing.Length == 0 && extra.Length == 0,
            $"{label}: {want.Count} lines against {got.Count}. missing: [{string.Join("; ", missing.Take(5).Select(l => Shorten(l, 300)))}] unexpected: [{string.Join("; ", extra.Take(5).Select(l => Shorten(l, 300)))}]");
    }

    /// <summary>How many classes share the interrupted-upgrade-step theory (<c>SchemaStartupResilienceUpgradeSlice*Tests</c>).</summary>
    internal const int UpgradeSliceCount = 16;

    /// <summary>How many classes share the interrupted-fresh-create theory (<c>SchemaStartupResilienceTests</c> and <c>SchemaStartupResilienceFreshCreateShare*Tests</c>).</summary>
    internal const int FreshCreateClassCount = 6;

    /// <summary>
    /// How many test cases one world fresh create is cut into. A fresh create of the world component issues one DDL statement per table and
    /// the theory replays the whole create once per injected fault, so the cost of one case grows with the square of the table count (727 s for
    /// MariaDB world on gate 57a1c0c21). Each shard owns every
    /// <c>WorldFaultShards</c>-th fault, so the faults still all run, in cases that the classes spread.
    /// </summary>
    internal const int WorldFaultShards = 12;

    /// <summary>
    /// How many test cases one characters fresh create is cut into, the same way as <see cref="WorldFaultShards"/>: MariaDB characters took 553 s on gate 57a1c0c21,
    /// three quarters of the world case that was cut 12 ways, and as one case it made one share class the heaviest.
    /// </summary>
    internal const int CharactersFaultShards = 4;

    /// <summary>
    /// Every <paramref name="slices"/>-th row of <see cref="UpgradeSteps"/>, starting at row <paramref name="slice"/>: the slices together cover each row exactly once. Each provider's rows
    /// are one run of consecutive rows, so a slice carries the same number of each provider's rows, within one (a MariaDB row costs far more than a SQLite one; the runner test pins this).
    /// While a provider's block is not a multiple of the slice count, a step's rows of the different providers land in different slices, so a heavy step does not stack in one class.
    /// </summary>
    public static IEnumerable<object[]> UpgradeStepsSlice(int slice, int slices) => UpgradeSteps().Where((_, index) => index % slices == slice);

    /// <summary>
    /// Every (provider, component, shard, shards) case of the interrupted-fresh-create theory: the world component is cut into <see cref="WorldFaultShards"/> shards, characters into <see cref="CharactersFaultShards"/>, the others are one case.
    /// A world case is never empty (see <see cref="RunInterruptedFreshCreateAsync"/>), and a SQLite world is sharded too, so every class gets a row even when only SQLite is configured.
    /// </summary>
    public static IEnumerable<object[]> FreshCreateCases()
    {
        foreach (object[] pc in ProviderComponents())
        {
            int shards = (string)pc[1] switch
            {
                "world" => WorldFaultShards,
                "characters" => CharactersFaultShards,
                _ => 1,
            };
            for (int shard = 0; shard < shards; shard++)
            {
                yield return [pc[0], pc[1], shard, shards];
            }
        }
    }

    /// <summary>Every <paramref name="slices"/>-th row of <see cref="FreshCreateCases"/>, starting at row <paramref name="slice"/>: the slices together cover each row exactly once.</summary>
    public static IEnumerable<object[]> FreshCreateSlice(int slice, int slices) => FreshCreateCases().Where((_, index) => index % slices == slice);

    /// <summary>The faults shard <paramref name="shard"/> of <paramref name="shards"/> runs: every <paramref name="shards"/>-th one starting at <paramref name="shard"/>. The shards together hold each fault exactly once.</summary>
    internal static List<T> FaultShard<T>(IReadOnlyList<T> faults, int shard, int shards)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shards, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(shard);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(shard, shards);
        return faults.Where((_, index) => index % shards == shard).ToList();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(DatabaseProvider Provider, string Component), Lazy<Task<int>>> FreshCreateDdl = new();

    /// <summary>
    /// How many DDL statements a fresh create of <paramref name="component"/> issues on <paramref name="provider"/>, measured once per process and shared by every shard:
    /// each shard would otherwise repeat the whole probe create, and a count that differed between shards would skip or repeat a fault silently.
    /// The probe runs on its own databases, which are dropped when it is done.
    /// </summary>
    private static Task<int> FreshCreateDdlCountAsync(DatabaseProvider provider, string component)
        => MeasureOnceAsync<(DatabaseProvider Provider, string Component)>(FreshCreateDdl, (provider, component), async key =>
        {
            await using var databases = new TestDatabases();
            var counter = new CommandTap();
            await SchemaProbe.EnsureCurrentAsync(key.Component, await databases.CreateAsync(key.Provider), counter);
            return counter.Ddl;
        });

    /// <summary>
    /// The value of <paramref name="key"/>, measured by the first caller and shared by the callers after it. A measurement that fails fails the callers waiting on it and is then forgotten:
    /// a transient error of a shared database must fail one wave of callers, not every later shard of the process with the same old exception.
    /// </summary>
    internal static async Task<int> MeasureOnceAsync<TKey>(System.Collections.Concurrent.ConcurrentDictionary<TKey, Lazy<Task<int>>> cache, TKey key, Func<TKey, Task<int>> measure)
        where TKey : notnull
    {
        Lazy<Task<int>> entry = cache.GetOrAdd(key, k => new Lazy<Task<int>>(() => measure(k)));
        try
        {
            return await entry.Value;
        }
        catch
        {
            cache.TryRemove(new KeyValuePair<TKey, Lazy<Task<int>>>(key, entry));
            throw;
        }
    }

    /// <summary>The body of the interrupted-fresh-create theory; the share classes each run a share of <see cref="FreshCreateCases"/> through it, so the rows run in parallel. Runs shard <paramref name="shard"/> of <paramref name="shards"/> of the faults.</summary>
    private protected async Task RunInterruptedFreshCreateAsync(DatabaseProvider provider, string component, int shard, int shards)
    {
        int ddl = await FreshCreateDdlCountAsync(provider, component);
        Assert.True(ddl > 0);

        // Die after each DDL statement, and just before the statement that records the version
        // (the crash the historic bootstrapper left an empty version table for).
        Func<string, bool> isVersionInsert = sql => sql.Contains(component + "_schema", StringComparison.Ordinal)
            && CommandTap.IsWrite(sql, "INSERT");
        var faults = new List<(string Label, Func<string, bool> When, int On, bool Before)>();
        for (int k = 1; k <= ddl; k++)
        {
            faults.Add(($"after DDL #{k}", CommandTap.IsDdl, k, false));
        }

        faults.Add(("before the version write", isVersionInsert, 1, true));

        // A shard with nothing to run would pass without testing anything: the shard count must not exceed the fault count.
        Assert.True(faults.Count >= shards, $"{provider} {component}: {faults.Count} faults cannot fill {shards} shards");
        await RunFaultsAsync(FaultShard(faults, shard, shards), FaultParallelism, async fault =>
        {
            (string label, Func<string, bool> when, int on, bool before) = fault;
            DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
            try
            {
                var tap = new CommandTap(when, on, faultBeforeStatement: before);
                await AssertInjectedAsync(() => SchemaProbe.EnsureCurrentAsync(component, connection, tap), $"{provider} {component} fresh create {label} (probe saw {ddl} DDL)", tap);
                try
                {
                    await SchemaProbe.EnsureCurrentAsync(component, connection);
                }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException($"{component} fresh create interrupted {label}: restart failed: {ex.GetType().Name}: {ex.Message}");
                }

                await AssertConvergedAsync(component, connection, $"{component} fresh create {label}");
            }
            finally
            {
                await _databases.ReleaseAsync(connection);
            }
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    /// <summary>
    /// Every injected fault runs against its own database, so they are independent: run <paramref name="parallelism"/>
    /// at a time. Every fault runs even when an earlier one fails (each releases its own database in its own
    /// <c>finally</c>); the first failure is rethrown once all have finished.
    /// </summary>
    internal static async Task RunFaultsAsync<T>(IReadOnlyList<T> faults, int parallelism, Func<T, Task> run)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        using var slots = new SemaphoreSlim(parallelism);
        await Task.WhenAll(faults.Select(async fault =>
        {
            await slots.WaitAsync();
            try
            {
                await run(fault);
            }
            finally
            {
                slots.Release();
            }
        }));
    }

    /// <summary>
    /// <see cref="DefaultFaultParallelism"/> unless <paramref name="value"/> (the environment variable) is a positive
    /// integer. Anything else, including 0 and negative numbers, means the default: a misspelt override must not turn
    /// the theory serial or unbounded silently.
    /// </summary>
    internal static int ReadFaultParallelism(string? value)
        => int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed >= 1
            ? parsed
            : DefaultFaultParallelism;

    /// <summary>The startup must die of the injected fault (EF wraps it in a DbUpdateException for SaveChanges), not of anything else.</summary>
    private protected static async Task AssertInjectedAsync(Func<Task> startup, string label, CommandTap tap)
    {
        Exception? thrown = null;
        try
        {
            await startup();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        if (thrown is null)
        {
            throw new Xunit.Sdk.XunitException(
                $"{label}: startup completed without the injected fault. EF commands seen ({tap.Commands.Count}): " +
                string.Join(" || ", tap.Commands.Select(c => Shorten(c, 90))));
        }

        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            if (e is InjectedFaultException)
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("startup failed, but not of the injected fault: " + thrown);
    }

    /// <summary>One line, at most <paramref name="max"/> characters. The length is taken after the line endings are replaced: they can change it.</summary>
    internal static string Shorten(string text, int max)
    {
        string line = text.ReplaceLineEndings(" ");
        return line[..Math.Min(max, line.Length)];
    }

    private protected static async Task AssertConvergedAsync(string component, DatabaseConnectionOptions connection, string label)
    {
        await using DbContext db = SchemaProbe.CreateContext(component, connection);
        Assert.Equal(SchemaProbe.SchemaOf(component).CurrentVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
        await SchemaProbe.AssertIndexParityAsync(db, label);
        await SchemaProbe.AssertColumnParityAsync(db, label);
    }

    /// <summary>Run one prefix of the current definition (versions up to <paramref name="version"/>) through a context carrying <paramref name="tap"/>.</summary>
    private protected static async Task EnsureAsync(string component, DatabaseConnectionOptions connection, int version, CommandTap tap)
    {
        SchemaDefinition prefix = SchemaProbe.ThroughVersion(SchemaProbe.SchemaOf(component), version);
        await using DbContext db = component == "characters"
            ? SchemaProbe.CreateWith<CharacterDbContext>(connection, tap)
            : SchemaProbe.CreateWith<WorldDbContext>(connection, tap);
        await SchemaBootstrapper.EnsureAsync(db, prefix);
    }

    /// <summary>A database of <paramref name="component"/> at <paramref name="version"/>, started from its historical version-1 model.</summary>
    /// <param name="provider">The engine.</param>
    /// <param name="component">characters or world.</param>
    /// <param name="version">The version the database is left at.</param>
    /// <param name="nextStep">The step about to run. When it only repairs indexes, the indexes it repairs are dropped first: a database prepared by the current bootstrapper would not need the repair. The columns it adds are dropped first for the same reason.</param>
    private protected async Task<DatabaseConnectionOptions> PrepareLegacyAsync(DatabaseProvider provider, string component, int version, int nextStep)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        if (component == "characters")
        {
            await using (CharactersM5Context legacy = TestContexts.Create<CharactersM5Context>(connection))
            {
                await SchemaBootstrapper.EnsureAsync(legacy, CharactersM5Context.Schema);
                legacy.Characters.Add(new CharacterV1Row { AccountId = 7, Name = "Existing", PlayedTime = 123 });
                await legacy.SaveChangesAsync();
            }
        }
        else
        {
            await using MapWorldV1Context legacy = TestContexts.Create<MapWorldV1Context>(connection);
            await SchemaBootstrapper.EnsureAsync(legacy, MapWorldV1Context.Schema);
            legacy.Set<ClassInfoRow>().Add(new ClassInfoRow { Class = 1, BaseHealth = 60, PowerType = 1 });
            await legacy.SaveChangesAsync();
        }

        if (version > 1)
        {
            await EnsureAsync(component, connection, version, new CommandTap());
        }

        SchemaStep next = SchemaProbe.SchemaOf(component).Steps.Single(s => s.Version == nextStep);
        foreach (EnsureIndexesChange repair in next.Changes.OfType<EnsureIndexesChange>())
        {
            await using DbContext db = SchemaProbe.CreateContext(component, connection);
            await SchemaProbe.DropIndexesAsync(db, repair.Table);
        }

        // A table created by an earlier step of this harness already has the columns the current model gives it,
        // including those a later step adds. A real database upgraded across that step lacks them, so remove them.
        foreach (AddColumnChange add in next.Changes.OfType<AddColumnChange>())
        {
            await using DbContext db = SchemaProbe.CreateContext(component, connection);
            if (await SchemaCatalog.ColumnExistsAsync(db, add.Table, add.Column))
            {
                await SchemaProbe.ExecuteAsync(db, $"ALTER TABLE {TestContexts.Quote(db, add.Table)} DROP COLUMN {TestContexts.Quote(db, add.Column)}");
            }
        }

        return connection;
    }
}
