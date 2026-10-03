using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.HotCode;

/// <summary>Where a refresh commits: the world thread, at the start of a tick (a seam so tests can drive it).</summary>
public interface IHotCodeWorld
{
    /// <summary>Queue <paramref name="command"/> for the world thread. Thread-safe.</summary>
    void Post(Action command);
}

/// <summary><see cref="WorldRuntime.Post"/>: commands run at the start of the next tick, before any map update.</summary>
public sealed class WorldRuntimeHotCodeWorld(WorldRuntime world) : IHotCodeWorld
{
    public void Post(Action command) => world.Post(command);
}

public enum RefreshStatus
{
    /// <summary>New handlers, commands or updaters were found and are now in force.</summary>
    Applied,

    /// <summary>The rescan found nothing the registries do not already have.</summary>
    NothingNew,

    /// <summary>This generation was already handled; nothing was done.</summary>
    AlreadyApplied,

    /// <summary>Refreshing is frozen; nothing was done.</summary>
    Frozen,

    /// <summary>The candidate was invalid or could not be committed; the old registries are still in force.</summary>
    Rejected,
}

public sealed record RefreshResult(
    RefreshStatus Status,
    long Generation,
    string Detail,
    int NewOpcodes = 0,
    int NewCommandRoots = 0,
    int NewMapUpdaters = 0);

/// <summary>
/// After a code edit the runtime has patched method bodies in place, which needs no help. What it
/// cannot do is tell the registries the server built once at startup about <em>new</em> things:
/// an opcode handler, a chat command root or a default map updater that did not exist then.
/// This rescans, builds a candidate off the world thread, and commits it with
/// <see cref="IHotCodeWorld.Post"/> so the swap happens at a tick boundary.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>All or nothing: a duplicate opcode, a clashing chat root or an invalid updater rejects the
/// whole refresh, the old registries stay in force, and the state is marked degraded.</item>
/// <item>Additive only: handlers, roots and updaters that already exist are kept as they are (their
/// delegates run whatever the runtime patched into them). Nothing is ever removed.</item>
/// <item>Idempotent by generation: a generation at or below the last applied one is a no-op.</item>
/// <item>Maps that already exist keep the updaters they have; only maps created later get a new default updater.</item>
/// </list>
/// </remarks>
public sealed class HotCodeRefresh
{
    private readonly HotCodeState _state;
    private readonly IHotCodeWorld _world;
    private readonly OpcodeTable _opcodes;
    private readonly CommandTableSource _commands;
    private readonly IHotCodeCatalog _catalog;
    private readonly HotCodeAudit _audit;
    private readonly ILogger _logger;
    private readonly TimeSpan _commitTimeout;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _serial = new(1, 1);

    public HotCodeRefresh(
        HotCodeState state,
        IHotCodeWorld world,
        OpcodeTable opcodes,
        CommandTableSource commands,
        IHotCodeCatalog catalog,
        HotCodeAudit audit,
        ILogger logger,
        TimeSpan? commitTimeout = null,
        Func<DateTimeOffset>? clock = null)
    {
        _state = state;
        _world = world;
        _opcodes = opcodes;
        _commands = commands;
        _catalog = catalog;
        _audit = audit;
        _logger = logger;
        _commitTimeout = commitTimeout ?? TimeSpan.FromSeconds(10);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public HotCodeState State => _state;

    /// <summary>
    /// The runtime applied a code edit (<c>MetadataUpdateHandler.UpdateApplication</c>, on a thread-pool
    /// thread as measured by tools/hotcode-spike, never assumed to be the world thread). Records it
    /// and refreshes in the background; never throws into the runtime.
    /// </summary>
    public void OnMetadataUpdate(Type[]? updatedTypes)
    {
        _state.RecordMetadataUpdate();
        long generation = _state.NextGeneration();
        string reason = updatedTypes is null ? "metadata update (types unknown)" : $"metadata update ({updatedTypes.Length} types)";
        _ = RunInBackgroundAsync(generation, reason);
    }

    /// <summary>Refresh now under a new generation (<c>.hotcode refresh</c>).</summary>
    public Task<RefreshResult> RefreshNowAsync(string reason) => RunAsync(_state.NextGeneration(), reason);

    public async Task<RefreshResult> RunAsync(long generation, string reason)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            RefreshResult result = await RunCoreAsync(generation, reason).ConfigureAwait(false);
            Record(result, reason);
            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task RunInBackgroundAsync(long generation, string reason)
    {
        try
        {
            await RunAsync(generation, reason).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "code hot reload refresh (generation {Generation}) failed unexpectedly", generation);
        }
    }

    private async Task<RefreshResult> RunCoreAsync(long generation, string reason)
    {
        if (_state.Frozen)
        {
            return new RefreshResult(RefreshStatus.Frozen, generation, "refresh is frozen (.hotcode thaw to resume)");
        }

        if (generation <= _state.AppliedGeneration)
        {
            return new RefreshResult(RefreshStatus.AlreadyApplied, generation, $"generation {generation} was already handled");
        }

        Candidate candidate;
        try
        {
            candidate = BuildCandidate();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Reject(generation, $"candidate rejected: {ex.Message}");
        }

        if (candidate.IsEmpty)
        {
            _state.RecordSuccess(generation, changedRegistries: false, _clock());
            return new RefreshResult(RefreshStatus.NothingNew, generation, "nothing new in the registries");
        }

        string? error = await CommitAsync(candidate).ConfigureAwait(false);
        if (error is not null)
        {
            return Reject(generation, error);
        }

        _state.RecordSuccess(generation, changedRegistries: true, _clock());
        return new RefreshResult(
            RefreshStatus.Applied, generation,
            Describe(candidate),
            candidate.NewOpcodes.Count, candidate.NewRoots.Count, candidate.NewUpdaters.Count);
    }

    private static string Describe(Candidate candidate)
        => $"{candidate.NewOpcodes.Count} opcode handlers ({string.Join(", ", candidate.NewOpcodes.Select(WorldOpcodeNames.GetName))}), "
            + $"{candidate.NewRoots.Count} command roots ({string.Join(", ", candidate.NewRoots.Select(c => "." + c.Name))}), "
            + $"{candidate.NewUpdaters.Count} map updaters ({string.Join(", ", candidate.NewUpdaters.Select(t => t.Name))}) added";

    private RefreshResult Reject(long generation, string detail)
    {
        _state.RecordRejected(detail, _clock());
        return new RefreshResult(RefreshStatus.Rejected, generation, detail);
    }

    private void Record(RefreshResult result, string reason)
    {
        string line = $"generation={result.Generation}; reason={reason}; {result.Detail}";
        switch (result.Status)
        {
            case RefreshStatus.Applied:
                _logger.LogWarning("Code hot reload applied (generation {Generation}): {Detail}", result.Generation, result.Detail);
                break;
            case RefreshStatus.Rejected:
                _logger.LogError(
                    "Code hot reload refresh REJECTED (generation {Generation}); the previous registries stay in force: {Detail}",
                    result.Generation, result.Detail);
                break;
            default:
                _logger.LogInformation("Code hot reload refresh {Status} (generation {Generation}): {Detail}", result.Status, result.Generation, result.Detail);
                break;
        }

        try
        {
            _audit.Record("refresh-" + result.Status.ToString().ToLowerInvariant(), line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "could not write the hot code audit log");
        }
    }

    /// <summary>Everything the refresh would change, built without touching anything live.</summary>
    private Candidate BuildCandidate()
    {
        // Opcodes: register every group into an empty table (two groups claiming one opcode fail
        // here, as at startup), then add only the opcodes the live table does not have.
        var fresh = new OpcodeTable();
        foreach (Type type in _catalog.OpcodeGroupTypes())
        {
            ((IOpcodeHandlerGroup)Create(type)).Register(fresh);
        }

        OpcodeTable merged = _opcodes.WithNewHandlersFrom(fresh, out IReadOnlyList<WorldOpcode> newOpcodes);

        // Chat commands: roots whose name the live table does not have, checked as TryAdd will check them.
        IReadOnlyList<ChatCommand> liveRoots = _commands.Current.Roots;
        var liveNames = new HashSet<string>(liveRoots.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var newRoots = new List<ChatCommand>();
        foreach (Type type in _catalog.CommandGroupTypes())
        {
            newRoots.AddRange(((ICommandGroup)Create(type)).Commands.Where(c => !liveNames.Contains(c.Name)));
        }

        if (CommandTableSource.Validate(liveRoots, newRoots) is { } commandError)
        {
            throw new InvalidOperationException(commandError);
        }

        // Default map updaters: rescan (an invalid marked type throws here).
        HashSet<Type> liveUpdaters = [.. _catalog.CurrentMapUpdaterTypes()];
        MapUpdaterScan scan = _catalog.ScanMapUpdaters();
        return new Candidate(merged, newOpcodes, newRoots, scan, scan.Types.Where(t => !liveUpdaters.Contains(t)).ToArray());
    }

    private static object Create(Type type)
        => Activator.CreateInstance(type) ?? throw new InvalidOperationException($"could not create {type.FullName}");

    /// <summary>Post the commit to the world thread and wait for it. Null on success, else the reason it did not apply.</summary>
    private async Task<string?> CommitAsync(Candidate candidate)
    {
        const int Pending = 0;
        const int Running = 1;
        const int Abandoned = 2;
        int phase = Pending;
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _world.Post(() =>
        {
            // A commit that arrives after the caller gave up must not apply behind its back.
            if (Interlocked.CompareExchange(ref phase, Running, Pending) != Pending)
            {
                return;
            }

            done.SetResult(Apply(candidate));
        });

        Task finished = await Task.WhenAny(done.Task, Task.Delay(_commitTimeout)).ConfigureAwait(false);
        if (finished != done.Task && Interlocked.CompareExchange(ref phase, Abandoned, Pending) == Pending)
        {
            return $"the world thread did not run the commit within {_commitTimeout.TotalSeconds:0.#}s; nothing was applied";
        }

        return await done.Task.ConfigureAwait(false);
    }

    /// <summary>World thread, start of a tick. Pointer flips only; on a failure the previous registries are restored.</summary>
    private string? Apply(Candidate candidate)
    {
        OpcodeTable? previous = null;
        try
        {
            if (candidate.NewOpcodes.Count > 0)
            {
                previous = _opcodes.Copy();
                _opcodes.Replace(candidate.Opcodes);
            }

            if (candidate.NewRoots.Count > 0)
            {
                CommandAddResult added = _commands.TryAdd(candidate.NewRoots);
                if (!added.Applied)
                {
                    throw new InvalidOperationException(added.Error);
                }
            }

            if (candidate.NewUpdaters.Count > 0)
            {
                candidate.Updaters.Commit();
            }

            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (previous is not null)
            {
                _opcodes.Replace(previous);
            }

            return $"commit failed and was rolled back: {ex.Message}";
        }
    }

    private sealed record Candidate(
        OpcodeTable Opcodes,
        IReadOnlyList<WorldOpcode> NewOpcodes,
        IReadOnlyList<ChatCommand> NewRoots,
        MapUpdaterScan Updaters,
        IReadOnlyList<Type> NewUpdaters)
    {
        public bool IsEmpty => NewOpcodes.Count == 0 && NewRoots.Count == 0 && NewUpdaters.Count == 0;
    }
}
