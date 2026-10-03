using ArcaneCore.Kernel.Honor;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Honor;

/// <summary>Hands the in-memory honor store of the test being built to <see cref="WorldTestHost"/> (the host discovers this class).</summary>
internal sealed class HonorTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<MemoryHonorStore?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is { } store)
        {
            services.AddSingleton<IHonorStore>(store);
        }
    }

    /// <summary>Start a host over <paramref name="store"/> (and optional configuration).</summary>
    public static WorldTestHost Start(MemoryHonorStore store, Action<IServiceCollection>? configureServices = null)
    {
        Current.Value = store;
        try
        {
            return WorldTestHost.Start(configureServices: configureServices);
        }
        finally
        {
            Current.Value = null;
        }
    }
}

/// <summary>Thread-safe in-memory honor storage (writes come from the queue's consumer).</summary>
internal sealed class MemoryHonorStore : IHonorStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, CharacterHonorState> _states = [];
    private readonly Dictionary<int, List<HonorCpRecord>> _rows = [];
    private HonorMaintenanceState? _maintenance;
    private int _writeAttempts;

    /// <summary>While set, every write throws <see cref="IOException"/> (the attempt is still counted).</summary>
    public volatile bool FailWrites;

    /// <summary>Write calls made so far, failed or not.</summary>
    public int WriteAttempts => Volatile.Read(ref _writeAttempts);

    /// <summary>The successful writes in the order they happened ("state:1", "cp:1:2", "reset:1", "delete:1").</summary>
    public List<string> Journal { get; } = [];

    public void Seed(int characterId, CharacterHonorState state, params HonorCpRecord[] rows)
    {
        lock (_lock)
        {
            _states[characterId] = state;
            _rows[characterId] = [.. rows];
        }
    }

    public CharacterHonorState State(int characterId)
    {
        lock (_lock)
        {
            return _states.GetValueOrDefault(characterId, CharacterHonorState.Empty);
        }
    }

    public IReadOnlyList<HonorCpRecord> Rows(int characterId)
    {
        lock (_lock)
        {
            return [.. _rows.GetValueOrDefault(characterId, [])];
        }
    }

    public HonorMaintenanceState? Maintenance
    {
        get
        {
            lock (_lock)
            {
                return _maintenance;
            }
        }
    }

    private void BeforeWrite(string what)
    {
        Interlocked.Increment(ref _writeAttempts);
        if (FailWrites)
        {
            throw new IOException("controlled honor storage failure");
        }

        lock (_lock)
        {
            Journal.Add(what);
        }
    }

    public Task<CharacterHonorData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(new CharacterHonorData(State(characterId), Rows(characterId)));
        }
    }

    public Task SaveStateAsync(int characterId, CharacterHonorState state, CancellationToken cancellationToken = default)
    {
        BeforeWrite($"state:{characterId}");
        lock (_lock)
        {
            _states[characterId] = state with { RankPoints = HonorRounding.OneDecimal(state.RankPoints), LastWeekCp = HonorRounding.OneDecimal(state.LastWeekCp) };
        }

        return Task.CompletedTask;
    }

    public Task AppendCpAsync(int characterId, IReadOnlyList<HonorCpRecord> rows, CancellationToken cancellationToken = default)
    {
        BeforeWrite($"cp:{characterId}:{rows.Count}");
        lock (_lock)
        {
            if (!_rows.TryGetValue(characterId, out List<HonorCpRecord>? list))
            {
                _rows[characterId] = list = [];
            }

            list.AddRange(rows.Select(r => r with { Cp = HonorRounding.OneDecimal(r.Cp) }));
        }

        return Task.CompletedTask;
    }

    public Task ResetAsync(int characterId, CancellationToken cancellationToken = default)
    {
        BeforeWrite($"reset:{characterId}");
        lock (_lock)
        {
            _rows.Remove(characterId);
            CharacterHonorState kept = _states.GetValueOrDefault(characterId, CharacterHonorState.Empty);
            _states[characterId] = CharacterHonorState.Empty with { PvpFlags = kept.PvpFlags, CityProtector = kept.CityProtector };
        }

        return Task.CompletedTask;
    }

    public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        BeforeWrite($"delete:{characterId}");
        lock (_lock)
        {
            _rows.Remove(characterId);
            _states.Remove(characterId);
        }

        return Task.CompletedTask;
    }

    /// <summary>This in-memory store has no characters table, so a deleted character's id never has a live row.</summary>
    public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        => DeleteCharacterAsync(characterId, cancellationToken);

    public Task<HonorMaintenanceState?> GetMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_maintenance);
        }
    }

    public Task SaveMaintenanceAsync(HonorMaintenanceState state, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _maintenance = state;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HonorWeeklyScore>> ListWeeklyScoresAsync(uint weekBeginDay, uint weekEndDay, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<HonorWeeklyScore>>([]);

    public Task ApplyMaintenanceAsync(HonorMaintenanceBatch batch, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the in-memory honor store does not run maintenance");
}
