using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
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

    /// <summary>Threshold below loaded terrain/WMO floor beyond which a player is considered under the map.</summary>
    public const float UnderTerrainThresholdYards = 50.0f;

    /// <summary>Depth below loaded terrain/WMO floor beyond which an ungrounded player is snapped back to the surface.</summary>
    public const float UnderTerrainRecoveryYards = 8.0f;

    /// <summary>Minimum interval (ms) between terrain-floor samples for <c>move/under-map</c>.</summary>
    public const uint TerrainSampleIntervalMs = 1000;

    private const float SurfaceSnapOffset = 0.05f;

    public void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming)
    {
        Player player = context.Player;
        LocomotionState state = player.Locomotion;
        if (state.SuppressAnomalies)
        {
            return;
        }

        if (!float.IsFinite(incoming.X) || !float.IsFinite(incoming.Y) || !float.IsFinite(incoming.Z) || !float.IsFinite(incoming.Orientation))
        {
            uint nowMs = context.World.NowMs;
            if (state.LastNonFiniteReportServerMs != nowMs)
            {
                state.LastNonFiniteReportServerMs = nowMs;
                AnomalyLedger.Shared.Report(
                    "move",
                    "non-finite",
                    $"player {player.Name} (guid {player.Guid.Low})",
                    $"opcode={context.Opcode} map={player.MapId} pos=({incoming.X},{incoming.Y},{incoming.Z}) o={incoming.Orientation}",
                    AnomalySeverity.Error);
            }
        }
    }

    public void AfterApply(MovementObserverContext context, in MovementInfo previous)
    {
        Player player = context.Player;
        if (player.IsGameMaster || player.Map is not { } map)
        {
            return;
        }

        LocomotionState state = player.Locomotion;
        if (player.Movement.Z >= VoidHeight)
        {
            if (!state.SuppressAnomalies
                && player.Transport is null
                && (player.UnitFlags & UnitFlags.TaxiFlight) == 0
                && (player.Movement.Flags & (MovementFlags.OnTransport | MovementFlags.Flying | MovementFlags.Swimming)) == 0)
            {
                uint nowMs = context.World.NowMs;
                if (state.LastUndermapSampleMs is null || unchecked(nowMs - state.LastUndermapSampleMs.Value) >= TerrainSampleIntervalMs)
                {
                    state.LastUndermapSampleMs = nowMs;
                    // Terrain alone convicts a player in a cave or a city; the models decide (MapCollision.ClassifyUnderMap; Unknown never convicts).
                    UnderMapVerdict verdict = map.Collision.ClassifyUnderMap(player.X, player.Y, player.Movement.Z, UnderTerrainRecoveryYards);
                    if (verdict.Kind == UnderMapKind.UnderMap
                        && !map.Collision.LevelWithAnyNearbyFloor(player.X, player.Y, player.Movement.Z))
                    {
                        float floorZ = player.Movement.Z + verdict.DepthYards;
                        if (verdict.DepthYards > UnderTerrainThresholdYards)
                        {
                            if (!state.UndermapReportedInEpisode)
                            {
                                state.UndermapReportedInEpisode = true;
                                AnomalyLedger.Shared.Report(
                                    "move",
                                    "under-map",
                                    $"player {player.Name} (guid {player.Guid.Low})",
                                    $"reason=below-terrain map={map.MapId} pos=({player.X:F2},{player.Y:F2},{player.Movement.Z:F2}) floor={floorZ:F2}",
                                    AnomalySeverity.Error,
                                    map.WorldIdentity);
                            }
                        }

                        if (float.IsFinite(floorZ) && floorZ > VoidHeight)
                        {
                            MovementInfo recovered = player.Movement with
                            {
                                Z = floorZ + SurfaceSnapOffset,
                                Flags = player.Movement.Flags & ~(MovementFlags.Jumping | MovementFlags.FallingFar),
                                FallTime = 0,
                            };
                            player.ApplyClientMovement(recovered, nowMs);
                        }
                    }
                    else
                    {
                        state.UndermapReportedInEpisode = false;
                    }
                }
            }
            else
            {
                state.UndermapReportedInEpisode = false;
            }

            return;
        }

        if (!state.SuppressAnomalies && !state.UndermapReportedInEpisode)
        {
            state.UndermapReportedInEpisode = true;
            AnomalyLedger.Shared.Report(
                "move",
                "under-map",
                $"player {player.Name} (guid {player.Guid.Low})",
                $"reason=below-void-height map={map.MapId} pos=({player.X:F2},{player.Y:F2},{player.Movement.Z:F2}) voidHeight={VoidHeight}",
                AnomalySeverity.Error,
                map.WorldIdentity);
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
