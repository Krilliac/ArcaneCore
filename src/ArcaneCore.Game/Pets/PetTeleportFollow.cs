using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// A player's pet goes with it through teleports (vmangos Player::TeleportTo / ExecuteTeleportFar and the teleport acks):
/// <list type="bullet">
/// <item>A far teleport unsummons the pet temporarily before the player leaves its map ("remove pet on map change",
/// Player.cpp:2045-2048) and brings it back once the player is in the new map (HandleMoveWorldportAckOpcode, "resummon pet",
/// MovementHandler.cpp:197-198).</item>
/// <item>A same-map teleport does so only when the pet is farther from the destination than the map's grid activation distance
/// (Player.cpp:1911-1921); the pet comes back when the client acknowledges (HandleMoveTeleportAck, MovementHandler.cpp:274-284).</item>
/// </list>
/// Totems, guardians and mini pets do not come along: the player's removal from its map unsummons them (vmangos
/// Player::RemoveFromWorld: UnsummonAllTotems, RemoveMiniPet; Unit::RemoveFromWorld: RemoveGuardians), as it already does here
/// (<see cref="PetMapSystem.OnPlayerRemoved"/>, the totem system's map updater).
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class PetTeleportFollow
{
    private readonly SummonService _summons;

    public PetTeleportFollow(SummonService summons, TeleportService teleports, WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(summons);
        ArgumentNullException.ThrowIfNull(teleports);
        ArgumentNullException.ThrowIfNull(world);
        _summons = summons;
        teleports.FarTeleportExecuting += OnFarTeleportExecuting;
        teleports.NearTeleportStarting += OnNearTeleportStarting;
        teleports.TeleportCompleted += OnTeleportCompleted;
        world.PlayerLoggingOut += _summons.ForgetTemporarilyUnsummonedPet;
    }

    private void OnFarTeleportExecuting(Player player) => _summons.UnsummonPetTemporarily(player);

    private void OnNearTeleportStarting(Player player, TeleportDestination destination)
    {
        if (player.PetGuid.IsEmpty || player.Map is not { } map || map.FindObject(player.PetGuid) is not Unit pet)
        {
            return;
        }

        // vmangos "same map, only remove pet if out of range for new position": WorldObject::IsWithinDist3d (Object.cpp:1712-1721), the 3D
        // distance against the grid activation distance plus the pet's bounding radius.
        float dx = pet.X - destination.X;
        float dy = pet.Y - destination.Y;
        float dz = pet.Z - destination.Z;
        float reach = map.Grids.Options.GridActivationDistance + pet.BoundingRadius;
        if ((dx * dx) + (dy * dy) + (dz * dz) >= reach * reach)
        {
            _summons.UnsummonPetTemporarily(player);
        }
    }

    private void OnTeleportCompleted(Player player) => _summons.ResummonTemporarilyUnsummonedPet(player);
}
