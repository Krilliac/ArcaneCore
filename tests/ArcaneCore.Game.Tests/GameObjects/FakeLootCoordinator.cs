using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Loot;
using Xunit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// A scriptable <see cref="ILootStateCoordinator"/> that follows the contract of the world's
/// settlement runner: the cache holds committed state only, a key is pending from
/// <see cref="TryStart"/> until its operation completes, an actor is frozen with the settlement
/// hold, and the operation's publication runs inside the actor's publication window. Every
/// started operation must be a legal successor of the committed state (the store's rule), or
/// the test fails. Operations complete at once with <see cref="NextOutcome"/> (default After),
/// or stay pending until <see cref="Complete"/> when <see cref="Manual"/> is set.
/// </summary>
internal sealed class FakeLootCoordinator(WorldRuntime world) : ILootStateCoordinator
{
    private readonly Dictionary<LootStateKey, LootOperation> _pending = [];
    private readonly HashSet<LootStateKey> _blocked = [];

    public long Now { get; set; } = 1_900_000_000;

    public bool Persist { get; set; } = true;

    public bool Manual { get; set; }

    public bool RefuseActors { get; set; }

    public bool RefuseStarts { get; set; }

    public LootOutcome NextOutcome { get; set; } = LootOutcome.After;

    public Dictionary<LootStateKey, LootStateRecord> Cache { get; } = [];

    public List<LootOperation> Started { get; } = [];

    public List<(LootOperation Operation, Guid Id)> Open { get; } = [];

    public long UnixNow => Now;

    public bool CanPersist(Map map) => Persist && map.InstanceId != 0;

    public LootStateRecord? Find(LootStateKey key) => Cache.GetValueOrDefault(key);

    public bool IsPending(LootStateKey key) => _pending.ContainsKey(key);

    public bool IsBlocked(LootStateKey key) => _pending.ContainsKey(key) || _blocked.Contains(key);

    public LootActor? CreateActor(Player player, InventorySnapshot before, InventorySnapshot after)
    {
        if (RefuseActors || player.IsQuestSettlementPending)
        {
            return null;
        }

        CharacterState state = player.CreateSnapshot(world.NowMs) with { Inventory = before };
        return new LootActor(player, state, state with { Inventory = after });
    }

    public bool TryStart(LootOperation operation)
    {
        Assert.True(LootStateRules.IsLegalSuccessor(operation.Expected, operation.Updated, operation.Awards),
            "the game planned a transition the store would refuse");
        Assert.Equal(Find(operation.Key), operation.Expected);
        if (RefuseStarts || IsBlocked(operation.Key))
        {
            return false;
        }

        var id = Guid.NewGuid();
        if (operation.Actor is { } actor && !actor.Player.BeginQuestSettlement(id))
        {
            return false;
        }

        _pending[operation.Key] = operation;
        Open.Add((operation, id));
        Started.Add(operation);
        if (!Manual)
        {
            Complete(operation, NextOutcome);
        }

        return true;
    }

    /// <summary>Complete the oldest operation still pending (manual mode).</summary>
    public void Complete(LootOutcome outcome = LootOutcome.After)
        => Complete(Open.First().Operation, outcome);

    public void Complete(LootOperation operation, LootOutcome outcome)
    {
        Guid id = Open.Single(o => ReferenceEquals(o.Operation, operation)).Id;
        Open.RemoveAll(o => ReferenceEquals(o.Operation, operation));
        _pending.Remove(operation.Key);
        if (outcome == LootOutcome.After)
        {
            Cache[operation.Key] = operation.Updated;
        }
        else if (outcome == LootOutcome.Unknown)
        {
            _blocked.Add(operation.Key);
        }

        bool current = false;
        if (operation.Actor is { } actor)
        {
            Player player = actor.Player;
            current = player.IsInWorld && player.QuestSettlementOperationId == id;
            if (outcome == LootOutcome.After && current)
            {
                using (player.BeginQuestSettlementPublication(id))
                {
                    operation.PublishActor?.Invoke();
                }

                player.EndQuestSettlement(id);
            }
            else if (outcome == LootOutcome.Unknown)
            {
                player.Session.Kick();
            }
            else if (current)
            {
                player.EndQuestSettlement(id);
            }
        }

        operation.Finished(outcome, true, current && outcome != LootOutcome.Unknown);
    }

    /// <summary>Drop the committed state of an instance (a real reset).</summary>
    public void Reset(uint instanceId)
    {
        foreach (LootStateKey key in Cache.Keys.Where(k => k.InstanceId == instanceId).ToArray())
        {
            Cache.Remove(key);
        }
    }
}
