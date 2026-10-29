using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using GridDefines = ArcaneCore.Game.Maps.Grid.GridDefines;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Runtime outcome checks and <see cref="AnomalyLedger"/> reporting for creatures, creature movement,
/// creature AI, scripted encounters, and respawns.
/// </summary>
public static class CreatureAnomalies
{
    public const string CreatureSubsystem = "creature";
    public const string CreatureAiSubsystem = "creature-ai";
    public const string CreatureScriptSubsystem = "creature-script";
    public const string RespawnSubsystem = "respawn";

    public const string EvadeLoopCode = "evade-loop";
    public const string LeashResetLoopCode = "leash-reset-loop";
    public const string UnderMapCode = "under-map";
    public const string NonFiniteCode = "non-finite";
    public const string PathFailedCode = "path-failed";
    public const string StuckCode = "stuck";
    public const string StuckChasingCode = "stuck-chasing";
    public const string HomeUnreachableCode = "home-unreachable";
    public const string StuckInCombatCode = "stuck-in-combat";
    public const string WaypointPathMissingCode = "waypoint-path-missing";

    public const string ScriptActionUnsupportedCode = "script-action-unsupported";

    public const string BossStateInconsistentCode = "boss-state-inconsistent";

    public const string RespawnStuckCode = "stuck";
    public const string RespawnDuplicateCode = "duplicate";

    /// <summary>
    /// The evade reason of a scripted evade (EventAI ACTION_T_EVADE, SmartAI SMART_ACTION_EVADE): a fight a script ends on purpose is no evade loop. A shared constant,
    /// not a caller name, so the exemption cannot drift from what production passes (<c>GetType().Name + "." + caller</c> never read "Execute").
    /// Only those two script actions pass it, and no fight against a player, or a unit a player owns or charms, is ever exempt.
    /// </summary>
    public const string ScriptedEvadeReason = "EventAi.Evade";

    /// <summary>Rolling window (ms) in which repeated evades or leash resets trigger loop anomalies.</summary>
    public const long LoopWindowMs = 60_000;

    /// <summary>Number of evades or leash resets within <see cref="LoopWindowMs"/> that triggers an anomaly.</summary>
    public const int LoopThreshold = 3;

    /// <summary>Minimum interval (ms) between terrain/mesh height samples for the under-map check per creature.</summary>
    public const long UnderMapSampleIntervalMs = 2_000;

    /// <summary>Hard world Z floor below which any creature (including flyers/swimmers) is considered under the map.</summary>
    public const float HardUnderMapZ = -5_000f;

    /// <summary>Tolerance (yards) below sampled ground/mesh height before a ground creature is reported as under the map.</summary>
    public const float UnderMapMarginYards = 15f;

    /// <summary>Duration (ms) of active movement intent with less than <see cref="StuckMinProgressYards"/> progress before reporting stuck.</summary>
    public const uint StuckThresholdMs = 6_000;

    /// <summary>Minimum 3D distance (yards) a moving creature must cover within <see cref="StuckThresholdMs"/> to count as making progress.</summary>
    public const float StuckMinProgressYards = 0.5f;

    /// <summary>Duration (ms) a creature may remain in combat with no valid target/threat before reporting stuck-in-combat.</summary>
    public const uint StuckInCombatEmptyMs = 10_000;

    /// <summary>Extra grace (ms) beyond <see cref="CreatureOptions.UnreachableTargetEvadeMs"/> before an un-evaded unreachable target is reported.</summary>
    public const uint StuckInCombatUnreachableGraceMs = 5_000;

    /// <summary>Overdue elapsed time (ms) past a due respawn deadline on a loaded, unblocked grid before reporting respawn/stuck.</summary>
    public const uint RespawnStuckGraceMs = 60_000;

    /// <summary>Stable per-creature subject string (<c>creature entry E spawn S</c>; no coordinates or timestamps).</summary>
    public static string Subject(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return creature.GetAnomalySubject();
    }

    /// <summary>Stable subject string for a database spawn (<c>creature entry E spawn S</c>).</summary>
    public static string Subject(CreatureSpawn spawn)
    {
        ArgumentNullException.ThrowIfNull(spawn);
        return $"creature entry {spawn.Entry} spawn {spawn.Guid}";
    }

    /// <summary>Stable subject string for an entry and spawn id pair (<c>creature entry E spawn S</c>).</summary>
    public static string Subject(uint entry, uint spawnGuid)
        => $"creature entry {entry} spawn {spawnGuid}";

    public static void ReportNonFinite(Creature creature, string detail)
    {
        if (creature.TryReportNonFinite())
        {
            AnomalyLedger.Shared.Report(
                CreatureSubsystem,
                NonFiniteCode,
                Subject(creature),
                detail,
                AnomalySeverity.Error);
        }
    }

    public static void ReportUnderMap(Creature creature, float groundZ, string detail)
    {
        if (creature.TryReportUnderMap())
        {
            AnomalyLedger.Shared.Report(
                CreatureSubsystem,
                UnderMapCode,
                Subject(creature),
                detail,
                AnomalySeverity.Error);
        }
    }

    public static void ReportPathFailed(Creature creature, Vector3 start, Vector3 destination, string context)
    {
        if (!creature.CharmerOrOwnerGuid.IsEmpty && !creature.Combat.IsInCombat)
        {
            return;
        }

        if (creature.TryReportPathFailed())
        {
            AnomalyLedger.Shared.Report(
                CreatureSubsystem,
                PathFailedCode,
                Subject(creature),
                $"No path for {context} from ({start.X:F1}, {start.Y:F1}, {start.Z:F1}) to ({destination.X:F1}, {destination.Y:F1}, {destination.Z:F1}) on map {creature.MapId}");
        }
    }

    public static void ReportHomeUnreachable(Creature creature, Vector3 home, string reason)
    {
        if (creature.TryReportHomeUnreachable())
        {
            AnomalyLedger.Shared.Report(
                CreatureSubsystem,
                HomeUnreachableCode,
                Subject(creature),
                $"Home position ({home.X:F1}, {home.Y:F1}, {home.Z:F1}) unreachable from ({creature.X:F1}, {creature.Y:F1}, {creature.Z:F1}) on map {creature.MapId}: {reason}");
        }
    }

    /// <summary>
    /// A stuck creature. <paramref name="chasing"/> (the chase generator is on top and the creature makes no progress: either standing out of
    /// melee reach of a reachable victim, or on a chase spline that goes nowhere) is <c>creature/stuck-chasing</c>; any other spline or a
    /// waypoint walk without progress (bad path data) stays <c>creature/stuck</c>.
    /// </summary>
    public static void ReportStuck(Creature creature, uint elapsedMs, string context, bool chasing = false)
    {
        if (creature.TryReportStuck())
        {
            AnomalyLedger.Shared.Report(
                CreatureSubsystem,
                chasing ? StuckChasingCode : StuckCode,
                Subject(creature),
                $"Creature made < {StuckMinProgressYards:F1} yd progress over {elapsedMs} ms while {context} at ({creature.X:F1}, {creature.Y:F1}, {creature.Z:F1}) on map {creature.MapId}");
        }
    }

    public static void ReportStuckInCombat(Creature creature, uint elapsedMs, string reason)
    {
        if (creature.TryReportStuckInCombat())
        {
            AnomalyLedger.Shared.Report(
                CreatureSubsystem,
                StuckInCombatCode,
                Subject(creature),
                $"Creature stuck in combat for {elapsedMs} ms on map {creature.MapId} at ({creature.X:F1}, {creature.Y:F1}, {creature.Z:F1}): {reason}");
        }
    }

    public static void ReportWaypointPathMissing(Creature creature, uint pathId, string source)
    {
        if (creature.TryReportMissingWaypoint(pathId))
        {
            AnomalyLedger.Shared.Report(
                CreatureSubsystem,
                WaypointPathMissingCode,
                Subject(creature),
                $"Waypoint movement requested ({source}, pathId={pathId}) on map {creature.MapId}, but resolved path has 0 points");
        }
    }

    public static void ReportScriptActionUnsupported(Creature creature, string detail)
    {
        if (creature.TryReportUnsupportedScript(detail))
        {
            AnomalyLedger.Shared.Report(
                CreatureAiSubsystem,
                ScriptActionUnsupportedCode,
                Subject(creature),
                detail);
        }
    }

    public static void ReportScriptActionUnsupported(uint entry, uint spawnGuid, string detail)
    {
        AnomalyLedger.Shared.Report(
            CreatureAiSubsystem,
            ScriptActionUnsupportedCode,
            Subject(entry, spawnGuid),
            detail);
    }

    public static void ReportBossStateInconsistent(Creature creature, string stateKey, string detail)
    {
        if (creature.TryReportBossStateInconsistent(stateKey))
        {
            AnomalyLedger.Shared.Report(
                CreatureScriptSubsystem,
                BossStateInconsistentCode,
                Subject(creature),
                detail);
        }
    }

    public static void ReportRespawnStuck(Creature creature, long overdueMs, string detail)
    {
        if (creature.TryReportRespawnStuck())
        {
            AnomalyLedger.Shared.Report(
                RespawnSubsystem,
                RespawnStuckCode,
                Subject(creature),
                $"Respawn overdue by {overdueMs} ms on map {creature.MapId}: {detail}");
        }
    }

    public static void ReportRespawnStuck(CreatureSpawn spawn, long overdueMs, string detail)
    {
        AnomalyLedger.Shared.Report(
            RespawnSubsystem,
            RespawnStuckCode,
            Subject(spawn),
            $"Respawn overdue by {overdueMs} ms on map {spawn.MapId}: {detail}");
    }

    public static void ReportDuplicateRespawn(Creature incoming, Creature existing, string detail)
    {
        AnomalyLedger.Shared.Report(
            RespawnSubsystem,
            RespawnDuplicateCode,
            Subject(incoming),
            $"Duplicate creature in world for {Subject(incoming)} (incoming={incoming.Guid}, existing={existing.Guid}): {detail}");
    }
}
