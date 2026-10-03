using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.World.Tests.Instances;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// An in-memory <see cref="ILootStateStore"/> with the real store's rules (the shared
/// <see cref="LootStateRules"/>, the instance scope check, the ledger, expected-state
/// comparison) whose instance deletion is linked to <see cref="InMemoryInstanceStore"/>, as
/// <c>EfInstanceStore.DeleteInstanceAsync</c> deletes the chest rows in the same transaction.
/// A commit applies the participants' After states through the host's character store (so an
/// awarded inventory reaches the in-memory item store) and can be held, refused or made to
/// fail after it was applied, to drive the runner's reconciliation.
/// </summary>
internal sealed class InMemoryLootStateStore : ILootStateStore
{
    private readonly IServiceProvider _services;
    private readonly InMemoryInstanceStore _instances;
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _ledger = [];

    /// <summary>The chests the next host's store holds at startup (a world restarted over stored loot); set by the test that starts the host.</summary>
    public static readonly AsyncLocal<IReadOnlyList<LootStateRecord>?> Seed = new();

    public InMemoryLootStateStore(IServiceProvider services, InMemoryInstanceStore instances)
    {
        _services = services;
        _instances = instances;
        foreach (LootStateRecord record in Seed.Value ?? [])
        {
            States[record.Key] = record;
        }

        instances.Deleted += instanceId =>
        {
            lock (_gate)
            {
                foreach (LootStateKey key in States.Keys.Where(k => k.InstanceId == instanceId).ToArray())
                {
                    States.TryRemove(key, out _);
                }
            }
        };
    }

    public ConcurrentDictionary<LootStateKey, LootStateRecord> States { get; } = new();

    public ConcurrentQueue<(LootCommitRequest Request, LootCommitResult Result)> Commits { get; } = new();

    /// <summary>Signalled when a commit starts (before it is held).</summary>
    public TaskCompletionSource CommitEntered { get; set; } = NewSignal();

    /// <summary>While set, a commit waits here before it is applied.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>The commit throws before anything is applied.</summary>
    public bool ThrowBeforeApplying { get; set; }

    /// <summary>The commit is applied, then the acknowledgement is lost (an exception).</summary>
    public bool ThrowAfterApplying { get; set; }

    /// <summary>The commit ignores whether the instance row still exists (a delete that raced the commit).</summary>
    public bool SkipScopeCheck { get; set; }

    /// <summary>The read-back that resolves a failed commit also fails.</summary>
    public bool FailReconciliation { get; set; }

    public static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<LootCommitResult> CommitAsync(LootCommitRequest request, CancellationToken cancellationToken = default)
    {
        CommitEntered.TrySetResult();
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        if (ThrowBeforeApplying)
        {
            throw new InvalidOperationException("injected failure before the commit");
        }

        LootCommitResult result;
        lock (_gate)
        {
            result = Decide(request);
            if (result == LootCommitResult.Committed)
            {
                States[request.Key] = request.Updated;
                _ledger.Add(request.OperationId);
            }
        }

        if (result == LootCommitResult.Committed)
        {
            ICharacterStore characters = _services.GetRequiredService<ICharacterStore>();
            foreach (EconomyParticipant participant in request.Participants)
            {
                await characters.SaveStateAsync(participant.After, cancellationToken);
            }
        }

        Commits.Enqueue((request, result));
        if (ThrowAfterApplying && result == LootCommitResult.Committed)
        {
            throw new InvalidOperationException("injected loss of the commit acknowledgement");
        }

        return result;
    }

    public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        if (FailReconciliation)
        {
            throw new InvalidOperationException("injected failure of the read-back");
        }

        lock (_gate)
        {
            return Task.FromResult(_ledger.Contains(operationId));
        }
    }

    public Task<IReadOnlyList<LootStateRecord>> LoadInstanceStatesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LootStateRecord>>([.. States.Values.Where(r => _instances.Live.ContainsKey(r.Key.InstanceId))]);

    private LootCommitResult Decide(LootCommitRequest request)
    {
        if (_ledger.Contains(request.OperationId))
        {
            return LootCommitResult.AlreadyCommitted;
        }

        if (!SkipScopeCheck && !_instances.Live.ContainsKey(request.Key.InstanceId))
        {
            return LootCommitResult.ScopeMissing;
        }

        if (!LootStateRules.IsLegalSuccessor(request.Expected, request.Updated, request.Awards)
            || ((request.Participants.Count > 0 || request.Awards.Count > 0)
                && !LootStateRules.AwardsMatchParticipants(request.Awards, request.Participants)))
        {
            return LootCommitResult.InvalidTransition;
        }

        return Equals(States.GetValueOrDefault(request.Key), request.Expected) ? LootCommitResult.Committed : LootCommitResult.Conflict;
    }
}

/// <summary>Registers <see cref="InMemoryLootStateStore"/> (one per host) in every <see cref="WorldTestHost"/>.</summary>
internal sealed class LootTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<InMemoryLootStateStore>(sp => new InMemoryLootStateStore(sp, sp.GetRequiredService<InMemoryInstanceStore>()));
        services.AddSingleton<ILootStateStore>(sp => sp.GetRequiredService<InMemoryLootStateStore>());
    }
}
