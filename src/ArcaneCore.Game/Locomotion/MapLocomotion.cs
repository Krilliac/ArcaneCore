using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// The per-map tick of the movement-change ledgers (vmangos Unit::Update → CheckPendingMovementChanges,
/// Unit.cpp:6619-6665): every player's pending changes age by the tick and the oldest one that the client did not
/// acknowledge in <see cref="LocomotionOptions.PendingAckResponseTimeMs"/> is enforced or dropped. Runs after combat (order 10;
/// combat stays every map's first updater).
/// </summary>
[DefaultMapUpdater(Order = 10)]
internal sealed class MapLocomotion : IMapUpdater
{
    private readonly WorldRuntime _world;

    public MapLocomotion(Map map, WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(map);
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    /// <summary>The environment of this map's world (options, teleport probe).</summary>
    internal LocomotionEnvironment Environment => LocomotionEnvironment.For(_world);

    public void Update(Map map, uint diffMs)
    {
        // Looked up per tick: the daemon registers the configured environment after maps may already exist.
        LocomotionEnvironment environment = LocomotionEnvironment.For(_world);
        foreach (Player player in map.Players)
        {
            if (!LocomotionStates.TryGet(player, out LocomotionState? state) || !state.Pending.HasPending)
            {
                continue;
            }

            state.Pending.Age(diffMs);
            bool teleporting = environment.IsBeingTeleported(player);
            PendingMovementChange? enforce = state.Pending.CheckTimeout(environment.Options.PendingAckResponseTimeMs, teleporting, out _);
            if (enforce is not null)
            {
                // "Enforce the change": the client never acknowledged it (vmangos OnFailedToAckChange, then resolve).
                state.NoteFailedAck();
                MovementControl.Enforce(player, enforce, sendToClient: true);
            }
        }
    }

    /// <summary>
    /// A player that leaves the map can no longer answer (vmangos ResolvePendingMovementChanges(false, ...)): the
    /// changes that are still the latest of their type are applied silently.
    /// </summary>
    public void OnPlayerRemoved(Map map, Player player)
    {
        if (LocomotionStates.TryGet(player, out LocomotionState? state) && state.Pending.HasPending)
        {
            foreach (PendingMovementChange change in state.Pending.ResolveAll())
            {
                MovementControl.Enforce(player, change, sendToClient: false);
            }
        }
    }
}
