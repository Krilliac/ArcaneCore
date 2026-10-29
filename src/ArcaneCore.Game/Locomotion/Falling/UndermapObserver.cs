using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Teleport;
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
/// Antiundermap1 (<c>UndermapRecall</c>, MovementHandler.cpp:1115-1128, Player.h:1789-1801): only while the client says it is
/// falling far (<see cref="MovementFlags.FallingFar"/>) and is more than <see cref="RecallDepthYards"/> below a model-aware ground
/// (<see cref="MapCollision.ClassifyUnderMapForRecovery"/>; terrain alone never convicts, so an underground city on a map without
/// models is left alone), or on Warsong Gulch below z = 250, the player is returned to <see cref="Player.LastSafePosition"/> through
/// the near teleport (MSG_MOVE_TELEPORT_ACK, so the client and the server agree on where it is) when that position is on the same
/// map and within <see cref="RecallRangeYards"/> in the plane. A recall disarms <see cref="Player.UndermapRecallArmed"/> (Player.h:1795,1799), as does a safe position out of range or on
/// another map. Deviation: the recall inherits this observer's exclusions (game masters, transports, taxi, flying, swimming, below the
/// void height), which vmangos antiundermap1 does not have (MovementHandler.cpp:1116). Nothing here rewrites a living player's position
/// behind the client's back; the terrain-based
/// <c>move/under-map</c> report (deeper than <see cref="UnderTerrainThresholdYards"/>) only reports.
/// </para>
/// </summary>
[MovementObserver(Order = 40)]
public sealed class UndermapObserver : IClientMovementObserver
{
    /// <summary>The height below which a player is in the void (MovementHandler.cpp:1133).</summary>
    public const float VoidHeight = -500.0f;

    /// <summary>Threshold below loaded terrain/WMO floor beyond which a player is considered under the map.</summary>
    public const float UnderTerrainThresholdYards = 50.0f;

    /// <summary>Depth below the model-aware ground beyond which a far-falling player is recalled (MovementHandler.cpp:1121).</summary>
    public const float RecallDepthYards = 100.0f;

    /// <summary>Largest 2D distance to <see cref="Player.LastSafePosition"/> for a recall (Player.h:1793).</summary>
    public const float RecallRangeYards = 100.0f;

    /// <summary>Warsong Gulch recall height: a far-falling player below it is recalled whatever the ground (MovementHandler.cpp:1123).</summary>
    public const float WarsongRecallHeight = 250.0f;

    /// <summary>Minimum interval (ms) between terrain-floor samples for <c>move/under-map</c>.</summary>
    public const uint TerrainSampleIntervalMs = 1000;

    /// <summary>A report episode ends once the player is no deeper than this below the terrain (the depth that used to start a recovery).</summary>
    private const float ReportEpisodeEndYards = 8.0f;

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

    /// <summary>
    /// vmangos <c>UndermapRecall</c> (Player.h:1789-1801) behind the antiundermap1 test (MovementHandler.cpp:1116-1127). Returns whether a
    /// near teleport to <see cref="Player.LastSafePosition"/> was started. Cheap exits first: it only looks at the ground while far-falling.
    /// </summary>
    private static bool TryRecall(MovementObserverContext context, Player player, Maps.Map map)
    {
        if ((player.Movement.Flags & MovementFlags.FallingFar) == 0 || !player.UndermapRecallArmed || player.LastSafePosition is not { } safe)
        {
            return false;
        }

        float z = player.Movement.Z;
        bool undermap = z < WarsongRecallHeight && map.MapId == BattlegroundManager.MapOfType(BattlegroundType.WarsongGulch);
        if (!undermap)
        {
            undermap = map.Collision.ClassifyUnderMapForRecovery(player.X, player.Y, z, RecallDepthYards).Kind == UnderMapKind.UnderMap;
        }

        if (!undermap)
        {
            return false;
        }

        TeleportService? teleports = TeleportService.Of(context.World);
        if (teleports is null || teleports.IsBeingTeleported(player))
        {
            return false;
        }

        float dx = safe.X - player.X;
        float dy = safe.Y - player.Y;
        if (safe.MapId != map.MapId || !float.IsFinite(dx * dx + dy * dy) || dx * dx + dy * dy > RecallRangeYards * RecallRangeYards)
        {
            player.UndermapRecallArmed = false;
            return false;
        }

        // TELE_TO_NOT_LEAVE_COMBAT | TELE_TO_NOT_UNSUMMON_PET (Player.h:1798). LastSafePosition itself stays: the unstuck spell reads it.
        bool started = teleports.TeleportTo(player, safe.MapId, safe.X, safe.Y, safe.Z, safe.O, TeleportOptions.NotLeaveCombat | TeleportOptions.NotUnsummonPet);
        player.UndermapRecallArmed = false;
        return started;
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
                if (TryRecall(context, player, map))
                {
                    return;
                }

                uint nowMs = context.World.NowMs;
                if (state.LastUndermapSampleMs is null || unchecked(nowMs - state.LastUndermapSampleMs.Value) >= TerrainSampleIntervalMs)
                {
                    state.LastUndermapSampleMs = nowMs;
                    // Terrain alone convicts a player in a cave or a city; the models decide (MapCollision.ClassifyUnderMap; Unknown never convicts).
                    UnderMapVerdict verdict = map.Collision.ClassifyUnderMap(player.X, player.Y, player.Movement.Z, ReportEpisodeEndYards);
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
