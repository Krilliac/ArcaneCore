using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>
/// A fact that runs only when <see cref="Variable"/> is "1". Otherwise it is reported Skipped, never a silent pass.
/// </summary>
internal sealed class SaveQueueBenchFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_SAVE_BENCH";

    public SaveQueueBenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"{Variable}=1 is not set: the save-queue load benchmark did NOT run (docs/areas/persistence.md).";
        }
    }
}

/// <summary>
/// The live 200-bot persistence shape against a real file-backed SQLite characters database (WAL, the live
/// <c>Pooling=False;Default Timeout=5</c> connection settings) behind the real <see cref="CharacterSaveQueue"/> and
/// <see cref="SpellbookCache"/>: 200 characters autosaving every tenth of the run, a burst of 200 level-up snapshots at a
/// quarter of the run, 50 quest-settlement pre-reward saves (hold, save-for-settlement under a 5 s budget, resume) at random
/// times, and a background spellbook writer learning 18 spells per character for 50 characters. Evidence only: the
/// numbers are written to the test output (ARCANECORE_SAVE_BENCH_SECONDS shortens the default 60 s run), nothing is asserted
/// against a threshold except that the run really attempted what it claims.
/// </summary>
public sealed class SaveQueueLoadBenchmarkTests(ITestOutputHelper output) : IDisposable
{
    private const int Characters = 250;
    private const int Autosaved = 200;
    private const int SpellCharacters = 50;
    private const int Settlements = 50;
    private const int SettlementCapacity = 8;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-savebench-" + Guid.NewGuid().ToString("N"));

    [SaveQueueBenchFact]
    public async Task Autosave200_Settlements50_LevelBurst200()
    {
        Directory.CreateDirectory(_directory);
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("ARCANECORE_SAVE_BENCH_SECONDS"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double configured) && configured > 0 ? configured : 60;
        var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "characters.sqlite"), Pooling = false, DefaultTimeout = 5,
        }.ToString()).Options;

        var records = new List<CharacterRecord>();
        await using (var seed = new CharacterDbContext(options))
        {
            await SchemaBootstrapper.EnsureAsync(seed, CharacterDbContext.Schema, cancellationToken: default);
            var store = new EfCharacterStore(seed);
            for (int i = 0; i < Characters; i++)
            {
                records.Add(await store.CreateAsync(new CharacterRecord
                {
                    AccountId = 1 + (i / 10), Name = "Bench" + i.ToString("D3", CultureInfo.InvariantCulture), Race = 1, Class = 1,
                    Level = 1, MapId = 0, ZoneId = 12, Z = 83.5f, Money = 100,
                }));
            }
        }

        var counters = new Counters();
        var registrations = new ServiceCollection();
        registrations.AddScoped(_ => new CharacterDbContext(options));
        registrations.AddScoped<ICharacterStore>(sp => new CountingStore(new EfCharacterStore(sp.GetRequiredService<CharacterDbContext>()), counters));
        registrations.AddScoped<ICharacterSpellStore>(sp => new CountingSpellStore(new EfCharacterSpellStore(sp.GetRequiredService<CharacterDbContext>()), counters));
        await using ServiceProvider provider = registrations.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var queue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        queue.Start();
        var spellbook = new SpellbookCache(scopes, NullLogger.Instance);
        spellbook.Start();
        MethodInfo? isPendingMethod = typeof(CharacterSaveQueue).GetMethod("IsPending", [typeof(int)]);
        Func<int, bool> isPending = isPendingMethod is null
            ? static _ => false
            : id => (bool)isPendingMethod.Invoke(queue, [id])!;

        var buttons = Enumerable.Range(0, 12).Select(i => new ActionButton((byte)i, (uint)(100 + i), 0)).ToArray();
        var levels = new int[Characters + 1];
        CharacterState Snapshot(CharacterRecord record, bool withButtons)
        {
            int n = Interlocked.Increment(ref levels[record.Id]);
            return new CharacterState(record.Id, 0, 12, 0, 0, 83.5f, 0, (byte)(1 + (n % 60)), 0, Money: (uint)(100 + n))
            {
                ActionButtons = withButtons ? buttons : null,
            };
        }

        var maxPending = 0;
        var maxAge = TimeSpan.Zero;
        PropertyInfo? ageProperty = typeof(CharacterSaveQueue).GetProperty("OldestPendingAge");
        var settlementWaits = new List<double>();
        int failed = 0, cancelled = 0, refused = 0, succeeded = 0, active = 0;
        var gate = new Lock();
        var wall = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource();

        Task sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                maxPending = Math.Max(maxPending, queue.Pending);
                if (ageProperty?.GetValue(queue) is TimeSpan age)
                {
                    maxAge = age > maxAge ? age : maxAge;
                }

                await Task.Delay(50).ConfigureAwait(false);
            }
        });

        Task spellWriter = Task.Run(() =>
        {
            for (int c = 0; c < SpellCharacters; c++)
            {
                var player = new Player(records[c], new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new NullPlayerSession());
                spellbook.LoadCharacter(records[c].Id, []);
                for (uint spell = 0; spell < 18; spell++)
                {
                    spellbook.LearnSpell(player, 1000 + spell);
                }

                Thread.Sleep((int)(seconds * 1000 / SpellCharacters * 0.8));
            }
        });

        Task autosave = Task.Run(async () =>
        {
            var next = TimeSpan.FromSeconds(seconds / 6);
            while (wall.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                TimeSpan wait = next - wall.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait).ConfigureAwait(false);
                }

                for (int i = 0; i < Autosaved; i++)
                {
                    if (!isPending(records[i].Id))
                    {
                        queue.Enqueue(Snapshot(records[i], withButtons: true));
                    }
                }

                next += TimeSpan.FromSeconds(seconds / 6);
            }
        });

        Task burst = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds * 0.25)).ConfigureAwait(false);
            for (int i = 0; i < Autosaved; i++)
            {
                queue.Enqueue(Snapshot(records[i], withButtons: false));
            }
        });

        var random = new Random(1337);
        var settlementTasks = new List<Task>();
        for (int s = 0; s < Settlements; s++)
        {
            double at = seconds * (0.08 + (0.84 * random.NextDouble()));
            CharacterRecord record = records[random.Next(Autosaved)];
            settlementTasks.Add(Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(at)).ConfigureAwait(false);
                if (Interlocked.Increment(ref active) > SettlementCapacity)
                {
                    Interlocked.Decrement(ref active);
                    Interlocked.Increment(ref refused);
                    return;
                }

                var started = Stopwatch.StartNew();
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                queue.HoldCharacter(record.Id);
                try
                {
                    await queue.SaveForSettlementAsync(Snapshot(record, withButtons: true), budget.Token).ConfigureAwait(false);
                    await queue.FlushCharacterAsync(record.Id, budget.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        settlementWaits.Add(started.Elapsed.TotalMilliseconds);
                        succeeded++;
                    }
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    Interlocked.Increment(ref failed);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    queue.ResumeCharacter(record.Id);
                    Interlocked.Decrement(ref active);
                }
            }));
        }

        await Task.WhenAll(settlementTasks.Append(autosave).Append(burst).Append(spellWriter));
        TimeSpan loadWall = wall.Elapsed;
        var drain = Stopwatch.StartNew();
        await spellbook.FlushAsync();
        await queue.StopAsync();
        drain.Stop();
        await stop.CancelAsync();
        await sampler;
        await spellbook.DisposeAsync();

        double[] waits = [.. settlementWaits.Order()];
        output.WriteLine($"queue implementation: IsPending {(isPendingMethod is null ? "absent (baseline)" : "present")}, OldestPendingAge {(ageProperty is null ? "absent" : "present")}");
        output.WriteLine($"run seconds={seconds:F0} load wall={loadWall.TotalSeconds:F1}s drain after load={drain.Elapsed.TotalMilliseconds:F0}ms");
        output.WriteLine($"max queue depth (Pending, 50 ms samples) = {maxPending}; max oldest-pending age = {(ageProperty is null ? "n/a" : maxAge.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms")}");
        output.WriteLine($"settlements attempted={Settlements}: succeeded={succeeded} failed={failed} (cancelled/budget={cancelled}) refused-at-capacity={refused}");
        output.WriteLine($"settlement wait ms: max={(waits.Length == 0 ? 0 : waits[^1]):F0} p99={(waits.Length == 0 ? 0 : waits[Math.Min(waits.Length - 1, (int)Math.Ceiling(waits.Length * 0.99) - 1)]):F0} median={(waits.Length == 0 ? 0 : waits[waits.Length / 2]):F0}");
        output.WriteLine($"store calls: character SaveStateAsync={counters.CharacterSaves}, spell AddAsync (transactions)={counters.SpellAdds} for {SpellCharacters * 18} learned spells");

        Assert.Equal(Settlements, succeeded + failed + refused);
        Assert.Equal(0, queue.Pending);
    }

    public void Dispose()
    {
        // Every connection string here is Pooling=False, so no pooled handle outlives its connection; never clear the process-wide pools (tests run in parallel).
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }
    }

    private sealed class Counters
    {
        public int CharacterSaves;
        public int SpellAdds;
    }

    private sealed class CountingStore(ICharacterStore inner, Counters counters) : ICharacterStore
    {
        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref counters.CharacterSaves);
            return inner.SaveStateAsync(state, cancellationToken);
        }

        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => inner.IsNameTakenAsync(name, cancellationToken);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => inner.CreateAsync(character, cancellationToken);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, accountId, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => inner.GetAllIdentitiesAsync(cancellationToken);
    }

    private sealed class CountingSpellStore(ICharacterSpellStore inner, Counters counters) : ICharacterSpellStore
    {
        public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default) => inner.GetAllAsync(cancellationToken);
        public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetAsync(characterId, cancellationToken);

        public Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref counters.SpellAdds);
            return inner.AddAsync(characterId, spells, cancellationToken);
        }

        public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default) => inner.RemoveAsync(characterId, spell, cancellationToken);
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => inner.DeleteCharacterAsync(characterId, cancellationToken);
    }
}
