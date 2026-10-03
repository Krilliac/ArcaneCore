using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// "Antiundermap2: teleport to graveyard" (vmangos HandleMoverRelocation, MovementHandler.cpp:1132-1161): a player
/// who is below z = -500 (fallen through the world, or jumped into the void) and is not a game master takes
/// <c>FALL_TO_VOID</c> damage of half the current health (the full health on a battleground map), is killed and made a
/// ghost if that is lethal, and is sent to the graveyard. A player that is already a ghost only takes the graveyard
/// again. vmangos repeats this for every packet while the client keeps falling ("this is actually called many times"),
/// so it is idempotent by construction.
/// <para>
/// Not delivered: antiundermap1 (<c>UndermapRecall</c>, :1105-1127: more than 100 yards below the ground height while
/// falling, back to the last safe position and Warsong Gulch below z = 250), which needs a safe-position record and the
/// ground height under the player from the pathfinding and collision lanes.
/// </para>
/// </summary>
[MovementObserver(Order = 40)]
public sealed class UndermapObserver : IClientMovementObserver
{
    /// <summary>The height below which a player is in the void (MovementHandler.cpp:1133).</summary>
    public const float VoidHeight = -500.0f;

    public void AfterApply(MovementObserverContext context, in MovementInfo previous)
    {
        Player player = context.Player;
        if (player.Movement.Z >= VoidHeight || player.IsGameMaster || player.Map is not { } map)
        {
            return;
        }

        bool repopped = false;
        if (player.IsAlive)
        {
            uint damage = map.Template is { IsBattleground: true } ? player.Health : player.Health / 2;
            EnvironmentalDamage.Apply(context.World, player, EnvironmentalDamageType.FallToVoid, damage);

            // "player can be alive if GM and God"
            if (!player.IsAlive)
            {
                // KillPlayer turns the death state to CORPSE so that the death timer does not start in the next update,
                // BuildPlayerRepop makes the ghost (which also sends it to the graveyard in this core's RepopPlayer).
                map.Combat.KillPlayer(player);
                repopped = map.Combat.RepopPlayer(player);
            }
        }

        if (!repopped)
        {
            map.Combat.Hooks.RepopAtGraveyard(player);
        }
    }
}
