using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

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
    public const string HomeUnreachableCode = "home-unreachable";
    public const string StuckInCombatCode = "stuck-in-combat";
    public const string WaypointPathMissingCode = "waypoint-path-missing";

    public const string ScriptActionUnsupportedCode = "script-action-unsupported";

    public const string BossStateInconsistentCode = "boss-state-inconsistent";

    public const string RespawnStuckCode = "stuck";
    public const string RespawnDuplicateCode = "duplicate";

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

    public static void ReportStuck(Creature creature, uint elapsedMs, string context)
    {
        if (creature.TryReportStuck())
        {
            AnomalyLedger.Shared.Report(
                CreatureSubsystem,
                StuckCode,
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

public sealed partial class Creature
{
    private string? _anomalySubject;
    private uint _anomalySubjectEntry;

    private long _evadeWindowStartMs = -1;
    private int _evadeCountInWindow;
    private bool _evadeLoopReported;
    private string? _evadeReasons;

    private long _leashWindowStartMs = -1;
    private int _leashCountInWindow;
    private bool _leashLoopReported;

    private long _nextUnderMapCheckMs;
    private bool _underMapReported;
    private bool _nonFiniteReported;
    private bool _pathFailedReported;
    private bool _homeUnreachableReported;

    private bool _stuckSampling;
    private float _stuckSampleX;
    private float _stuckSampleY;
    private float _stuckSampleZ;
    private uint _stuckElapsedMs;
    private bool _stuckReported;

    private uint _stuckInCombatElapsedMs;
    private bool _stuckInCombatReported;

    private uint _overdueRespawnElapsedMs;
    private CreatureDeathState _lastOverdueDeathState = CreatureDeathState.Alive;
    private bool _respawnStuckReported;

    private HashSet<uint>? _reportedMissingWaypointPaths;
    private HashSet<string>? _reportedUnsupportedScripts;
    private HashSet<string>? _reportedInconsistentStates;

    internal string GetAnomalySubject()
    {
        uint entry = Entry;
        if (_anomalySubject is null || _anomalySubjectEntry != entry)
        {
            uint spawnId = Spawn?.Guid ?? Guid.Counter;
            _anomalySubjectEntry = entry;
            _anomalySubject = $"creature entry {entry} spawn {spawnId}";
        }

        return _anomalySubject;
    }

    internal bool RecordEvadeForAnomaly(long clockMs, out int count, out long windowElapsedMs)
        => RecordEvadeForAnomaly(clockMs, null, out count, out windowElapsedMs, out _);

    /// <param name="reason">Why this evade happened (<see cref="CreatureMapSystem.EnterEvadeMode"/>); the window's reasons come back in
    /// <paramref name="reasons"/>, oldest first, so a loop report says which path keeps sending the creature home.</param>
    internal bool RecordEvadeForAnomaly(long clockMs, string? reason, out int count, out long windowElapsedMs, out string? reasons)
    {
        reasons = null;
        if (!this.CharmerOrOwnerGuid.IsEmpty)
        {
            count = 0;
            windowElapsedMs = 0;
            return false;
        }

        if (_evadeWindowStartMs < 0 || clockMs - _evadeWindowStartMs > CreatureAnomalies.LoopWindowMs)
        {
            _evadeWindowStartMs = clockMs;
            _evadeCountInWindow = 1;
            _evadeLoopReported = false;
            _evadeReasons = reason;
        }
        else
        {
            _evadeCountInWindow++;
            if (reason is not null && _evadeCountInWindow <= CreatureAnomalies.LoopThreshold)
            {
                _evadeReasons = _evadeReasons is null ? reason : _evadeReasons + "; " + reason;
            }
        }

        reasons = _evadeReasons;

        count = _evadeCountInWindow;
        windowElapsedMs = clockMs - _evadeWindowStartMs;
        if (_evadeCountInWindow >= CreatureAnomalies.LoopThreshold && !_evadeLoopReported)
        {
            _evadeLoopReported = true;
            return true;
        }

        return false;
    }

    internal bool RecordLeashResetForAnomaly(long clockMs, out int count, out long windowElapsedMs)
    {
        if (!this.CharmerOrOwnerGuid.IsEmpty)
        {
            count = 0;
            windowElapsedMs = 0;
            return false;
        }

        if (_leashWindowStartMs < 0 || clockMs - _leashWindowStartMs > CreatureAnomalies.LoopWindowMs)
        {
            _leashWindowStartMs = clockMs;
            _leashCountInWindow = 1;
            _leashLoopReported = false;
        }
        else
        {
            _leashCountInWindow++;
        }

        count = _leashCountInWindow;
        windowElapsedMs = clockMs - _leashWindowStartMs;
        if (_leashCountInWindow >= CreatureAnomalies.LoopThreshold && !_leashLoopReported)
        {
            _leashLoopReported = true;
            return true;
        }

        return false;
    }

    internal bool ShouldSampleUnderMap(long clockMs)
    {
        if (Z < CreatureAnomalies.HardUnderMapZ || _nextUnderMapCheckMs == 0 || clockMs >= _nextUnderMapCheckMs)
        {
            _nextUnderMapCheckMs = clockMs + CreatureAnomalies.UnderMapSampleIntervalMs;
            return true;
        }

        return false;
    }

    internal bool TryReportUnderMap()
    {
        if (_underMapReported)
        {
            return false;
        }

        _underMapReported = true;
        return true;
    }

    internal void ClearUnderMapEpisode() => _underMapReported = false;

    internal bool TryReportNonFinite()
    {
        if (_nonFiniteReported)
        {
            return false;
        }

        _nonFiniteReported = true;
        return true;
    }

    internal void ClearNonFiniteEpisode() => _nonFiniteReported = false;

    internal bool TryReportPathFailed()
    {
        if (_pathFailedReported)
        {
            return false;
        }

        _pathFailedReported = true;
        return true;
    }

    internal void ClearPathFailedEpisode() => _pathFailedReported = false;

    internal bool TryReportHomeUnreachable()
    {
        if (_homeUnreachableReported)
        {
            return false;
        }

        _homeUnreachableReported = true;
        return true;
    }

    internal void ClearHomeUnreachableEpisode() => _homeUnreachableReported = false;

    /// <summary>True while the current stuck episode is already reported: callers skip building the detail text.</summary>
    internal bool StuckEpisodeReported => _stuckReported;

    /// <summary>True while the current stuck-in-combat episode is already reported: callers skip building the detail text.</summary>
    internal bool StuckInCombatEpisodeReported => _stuckInCombatReported;

    internal bool BossStateReported(string stateKey) => _reportedInconsistentStates?.Contains(stateKey) ?? false;

    internal bool TryReportStuck()
    {
        if (_stuckReported)
        {
            return false;
        }

        _stuckReported = true;
        return true;
    }

    internal void ResetStuckSampling()
    {
        _stuckSampling = false;
        _stuckElapsedMs = 0;
        _stuckReported = false;
    }

    internal uint AdvanceStuckSample(uint diffMs)
    {
        const float MinProgressSq = CreatureAnomalies.StuckMinProgressYards * CreatureAnomalies.StuckMinProgressYards;
        float dx = X - _stuckSampleX;
        float dy = Y - _stuckSampleY;
        float dz = Z - _stuckSampleZ;
        if (!_stuckSampling || (dx * dx) + (dy * dy) + (dz * dz) >= MinProgressSq)
        {
            _stuckSampling = true;
            _stuckSampleX = X;
            _stuckSampleY = Y;
            _stuckSampleZ = Z;
            _stuckElapsedMs = 0;
            _stuckReported = false;
            return 0;
        }

        _stuckElapsedMs += diffMs;
        return _stuckElapsedMs;
    }

    internal bool TryReportStuckInCombat()
    {
        if (_stuckInCombatReported)
        {
            return false;
        }

        _stuckInCombatReported = true;
        return true;
    }

    internal uint AdvanceStuckInCombat(uint diffMs)
    {
        _stuckInCombatElapsedMs += diffMs;
        return _stuckInCombatElapsedMs;
    }

    internal void ResetStuckInCombatEpisode()
    {
        _stuckInCombatElapsedMs = 0;
        _stuckInCombatReported = false;
    }

    internal uint AdvanceOverdueRespawn(CreatureDeathState state, uint overdueDeltaMs)
    {
        if (_lastOverdueDeathState != state)
        {
            _lastOverdueDeathState = state;
            _overdueRespawnElapsedMs = 0;
            return 0;
        }

        _overdueRespawnElapsedMs += overdueDeltaMs;
        return _overdueRespawnElapsedMs;
    }

    internal bool TryReportRespawnStuck()
    {
        if (_respawnStuckReported)
        {
            return false;
        }

        _respawnStuckReported = true;
        return true;
    }

    internal void ResetRespawnAnomalyState()
    {
        _overdueRespawnElapsedMs = 0;
        _lastOverdueDeathState = CreatureDeathState.Alive;
        _respawnStuckReported = false;
        _evadeWindowStartMs = -1;
        _evadeCountInWindow = 0;
        _evadeLoopReported = false;
        _leashWindowStartMs = -1;
        _leashCountInWindow = 0;
        _leashLoopReported = false;
        _underMapReported = false;
        _nonFiniteReported = false;
        _pathFailedReported = false;
        _homeUnreachableReported = false;
        ResetStuckSampling();
        ResetStuckInCombatEpisode();
        _reportedInconsistentStates?.Clear();
    }

    internal bool TryReportMissingWaypoint(uint pathId)
        => (_reportedMissingWaypointPaths ??= []).Add(pathId);

    internal bool TryReportUnsupportedScript(string detail)
        => (_reportedUnsupportedScripts ??= []).Add(detail);

    internal bool TryReportBossStateInconsistent(string stateKey)
        => (_reportedInconsistentStates ??= []).Add(stateKey);

    internal void ClearBossStateInconsistent(string stateKey)
        => _reportedInconsistentStates?.Remove(stateKey);
}

public sealed partial class CreatureMapSystem
{
    private readonly HashSet<uint> _reportedMissingWaypointSpawns = [];
    private readonly Dictionary<uint, uint> _overdueDormantRespawnMs = [];
    private readonly HashSet<(uint SpawnGuid, long RespawnAtMs)> _reportedDormantRespawnStuck = [];
    private uint _dormantSampleAccumMs;

    /// <summary>The dormant-respawn check runs at most this often per map; the overdue clock advances by the accumulated time.</summary>
    internal const uint DormantRespawnSampleMs = 3_000;

    internal void ReportMissingWaypointPath(Creature creature, uint pathId, string source)
    {
        if (creature.Spawn is { } spawn && pathId == 0)
        {
            if (!_reportedMissingWaypointSpawns.Add(spawn.Guid))
            {
                return;
            }
        }

        CreatureAnomalies.ReportWaypointPathMissing(creature, pathId, source);
    }

    internal void RecordEvadeAnomaly(Creature creature, string? reason = null)
    {
        // A fight the creature won (its victim, or the last unit it attacked, is dead) ends in an evade by design (vmangos
        // SelectHostileTarget with an empty threat list) and is no loop: live 2026-10-18 the six Stockade guards at -8784,830 killed the
        // four RP Defias Rioters of spawn group 19981 one by one every 10 min respawn, three evades in 5 s each time, ~36 reports an hour.
        bool won = LastFightWasWon(creature);
        string? described = string.IsNullOrEmpty(reason) || won ? null : DescribeEvade(creature, reason);
        creature.Combat.LastVictimGuid = default; // the next fight starts without this one's victim
        if (won)
        {
            return;
        }

        if (creature.RecordEvadeForAnomaly(_clockMs, described, out int count, out long windowMs, out string? reasons))
        {
            AnomalyLedger.Shared.Report(
                CreatureAnomalies.CreatureSubsystem,
                CreatureAnomalies.EvadeLoopCode,
                CreatureAnomalies.Subject(creature),
                $"entered evade mode {count} times within {windowMs} ms at ({creature.X:F1}, {creature.Y:F1}, {creature.Z:F1}) on map {Map.MapId}"
                    + (reasons is null ? "" : $"; evades: {reasons}"));
        }
    }

    /// <summary>
    /// One evade for the loop report: who asked, and the fight it ended (the victim, its state and distance, the attackers left, the motion).
    /// Without a victim it adds the last unit the creature attacked and its state now (live 2026-10-18: nearly every loop was "no victim,
    /// 0 att, Chase", e.g. six Stockade guards at -8784,830 evading together every 2-3 s). Kept short: the ledger cuts a detail at 300 chars.
    /// </summary>
    private string DescribeEvade(Creature creature, string reason)
    {
        UnitCombat combat = creature.Combat;
        string victim = combat.Victim is { } v ? DescribeEvadeTarget(creature, v) : "no victim";
        string threat = combat.HasThreatList ? $", threat {combat.Threat.Entries.Count}" : "";
        string last = combat.Victim is null && !combat.LastVictimGuid.IsEmpty ? ", last " + DescribeLastVictim(creature, combat.LastVictimGuid) : "";
        return $"{reason}({victim}, {combat.AttackersInternal.Count} att{threat}, {creature.Motion.CurrentType}{last})";
    }

    /// <summary>Whether the evading creature's victim, or else the last unit it attacked, is dead (the creature won the fight).</summary>
    private bool LastFightWasWon(Creature creature)
    {
        UnitCombat combat = creature.Combat;
        Unit? last = combat.Victim
            ?? (combat.LastVictimGuid.IsEmpty ? null : Map.FindPlayer(combat.LastVictimGuid) as Unit ?? Map.FindObject(combat.LastVictimGuid) as Unit);
        return last is not null && !last.IsAlive;
    }

    private static string DescribeEvadeTarget(Creature creature, Unit v)
        => (v is Player ? "player " : v is Creature c ? $"creature {c.Entry} " : "unit ") + (v.IsAlive ? "alive" : "dead")
            + (ReferenceEquals(v.Map, creature.Map)
                ? " " + Vector3.Distance(new(v.X, v.Y, v.Z), new(creature.X, creature.Y, creature.Z)).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "yd"
                : " off-map");

    /// <summary>The last victim by GUID: gone from the map, or its state now (dead, a ghost, evading, or alive but not attackable and its unit flags).</summary>
    private string DescribeLastVictim(Creature creature, ObjectGuid guid)
    {
        if ((Map.FindPlayer(guid) as Unit ?? Map.FindObject(guid) as Unit) is not { } last)
        {
            return guid.High == HighGuid.Player ? "player gone" : $"{guid.Entry} gone";
        }

        string state = last switch
        {
            Player p when (p.Flags & PlayerFlags.Ghost) != 0 => " ghost",
            Player { IsGameMaster: true } => " gm",
            Creature { IsInEvadeMode: true } => " evading",
            _ when last.IsAlive && !Map.Combat.Hooks.CanAttack(creature, last) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $" unattackable 0x{(uint)last.UnitFlags:X}"),
            _ => "",
        };
        return DescribeEvadeTarget(creature, last) + state;
    }

    internal void RecordLeashResetAnomaly(Creature creature, string reason)
    {
        if (creature.RecordLeashResetForAnomaly(_clockMs, out int count, out long windowMs))
        {
            AnomalyLedger.Shared.Report(
                CreatureAnomalies.CreatureSubsystem,
                CreatureAnomalies.LeashResetLoopCode,
                CreatureAnomalies.Subject(creature),
                $"Creature {creature.Guid} (entry {creature.Entry}) triggered leash reset ({reason}) {count} times within {windowMs} ms at ({creature.X:F1}, {creature.Y:F1}, {creature.Z:F1}) on map {Map.MapId}");
        }
    }

    private void CheckAliveCreatureAnomalies(Creature creature, uint diffMs)
    {
        if (!creature.IsAlive || !_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        CheckNonFinitePosition(creature);
        CheckUnderMap(creature);
        CheckMovementStuck(creature, diffMs);
        CheckCombatStuck(creature, diffMs);
        CheckScriptedStateConsistency(creature);
    }

    internal void CheckNonFinitePosition(Creature creature)
    {
        bool rejected = creature.ConsumeRejectedNonFiniteRelocation();
        bool nonFiniteCoords = !float.IsFinite(creature.X)
            || !float.IsFinite(creature.Y)
            || !float.IsFinite(creature.Z)
            || !float.IsFinite(creature.Orientation);

        if (!rejected && !nonFiniteCoords)
        {
            creature.ClearNonFiniteEpisode();
            return;
        }

        string detail = nonFiniteCoords
            ? $"Creature {creature.Guid} has non-finite coordinates ({creature.X}, {creature.Y}, {creature.Z}, o={creature.Orientation}) on map {Map.MapId}"
            : $"Creature {creature.Guid} rejected a non-finite relocation attempt on map {Map.MapId}";
        if (rejected)
        {
            creature.ClearNonFiniteEpisode();
        }

        CreatureAnomalies.ReportNonFinite(creature, detail);
    }

    /// <summary>
    /// The height provider only knows the terrain surface, so a creature in a cave or a city sits "below" it. Before a conviction the
    /// models are asked (<see cref="IUnderMapClassifier"/>): only <see cref="UnderMapKind.UnderMap"/> convicts, Ok and Unknown do not.
    /// </summary>
    private bool ConfirmUnderMap(Creature creature)
    {
        IUnderMapClassifier classifier = _underMap ?? Map.Collision;
        return classifier.ClassifyUnderMap(creature.X, creature.Y, creature.Z, CreatureAnomalies.UnderMapMarginYards).Kind == UnderMapKind.UnderMap;
    }

    private void CheckUnderMap(Creature creature)
    {
        if (!creature.ShouldSampleUnderMap(_clockMs))
        {
            return;
        }

        if (creature.Z < CreatureAnomalies.HardUnderMapZ)
        {
            CreatureAnomalies.ReportUnderMap(
                creature,
                CreatureAnomalies.HardUnderMapZ,
                $"Creature {creature.Guid} fell below hard world floor (z={creature.Z:F1} < {CreatureAnomalies.HardUnderMapZ:F1}) at ({creature.X:F1}, {creature.Y:F1}) on map {Map.MapId}");
            return;
        }

        // Exclude creatures on transports, flyers, water-only creatures, or currently swimming/flying/hovering units.
        if (creature.Movement.TransportGuid != 0
            || (creature.Template.InhabitType & 4) != 0
            || creature.Template.InhabitType == 2
            || (creature.Movement.Flags & (MovementFlags.Flying | MovementFlags.Hover | MovementFlags.Swimming)) != 0)
        {
            creature.ClearUnderMapEpisode();
            return;
        }

        float? localFloor = _height.GetHeight(Map.MapId, creature.X, creature.Y, creature.Z + 5f);
        if (localFloor is { } validLocal && float.IsFinite(validLocal))
        {
            if (creature.Z < validLocal - CreatureAnomalies.UnderMapMarginYards && ConfirmUnderMap(creature))
            {
                CreatureAnomalies.ReportUnderMap(
                    creature,
                    validLocal,
                    $"Creature {creature.Guid} is {validLocal - creature.Z:F1} yd below terrain/mesh floor (z={creature.Z:F1}, floor={validLocal:F1}) at ({creature.X:F1}, {creature.Y:F1}) on map {Map.MapId}");
            }
            else
            {
                creature.ClearUnderMapEpisode();
            }

            return;
        }

        float probeZ = Math.Max(creature.Z + 100f, creature.Home.Z + 50f);
        float? upperFloor = _height.GetHeight(Map.MapId, creature.X, creature.Y, probeZ);
        if (upperFloor is { } validUpper && float.IsFinite(validUpper)
            && creature.Z < validUpper - CreatureAnomalies.UnderMapMarginYards
            && ConfirmUnderMap(creature))
        {
            CreatureAnomalies.ReportUnderMap(
                creature,
                validUpper,
                $"Creature {creature.Guid} is {validUpper - creature.Z:F1} yd below upper terrain/mesh surface (z={creature.Z:F1}, surface={validUpper:F1}) at ({creature.X:F1}, {creature.Y:F1}) on map {Map.MapId}");
        }
        else
        {
            creature.ClearUnderMapEpisode();
        }
    }

    private void CheckMovementStuck(Creature creature, uint diffMs)
    {
        if (diffMs == 0
            || !creature.CharmerOrOwnerGuid.IsEmpty
            || CreatureMovementGates.CannotMove(creature)
            || (_ai.Spells?.IsCasting(creature) ?? false))
        {
            creature.ResetStuckSampling();
            return;
        }

        // 0 = not moving, 1 = spline, 2 = chasing out of reach, 3 = waypoint walk. The text is built only when the stuck report fires.
        int movingKind = 0;
        Unit? chaseVictim = null;
        if (creature.Spline is not null)
        {
            movingKind = 1;
        }
        else if (creature.Motion.CurrentType == MovementGeneratorType.Chase
            // A chaser whose last path query found the victim unreachable stands on purpose (no path, or at the end of a partial
            // one: vmangos TargetedMovementGenerator.cpp:188-189, cmangos ChaseMovementGenerator::DispatchSplineToPosition) while the
            // unreachable-target evade timer runs; that is not stuck. An evade that never comes is creature/stuck-in-combat.
            && creature.Motion.IsReachable
            && creature.Combat.IsInCombat
            && (creature.AI?.CombatMovement ?? true)
            && creature.Combat.Victim is { IsAlive: true } victim
            && ReferenceEquals(victim.Map, Map)
            && !MapCombat.CanReachWithMeleeAutoAttack(creature, victim))
        {
            movingKind = 2;
            chaseVictim = victim;
        }
        else if (creature.Motion.Top is WaypointMovementGenerator { WaitMs: <= 0, HasPath: true }
            && !creature.WaypointsPaused)
        {
            movingKind = 3;
        }

        if (movingKind == 0)
        {
            creature.ResetStuckSampling();
            return;
        }

        uint stuckMs = creature.AdvanceStuckSample(diffMs);
        if (stuckMs >= CreatureAnomalies.StuckThresholdMs && !creature.StuckEpisodeReported)
        {
            string movingContext = movingKind switch
            {
                1 => $"moving on spline (generator={creature.Motion.CurrentType})",
                2 => $"chasing target {chaseVictim!.Guid} out of melee reach",
                _ => "walking waypoint path",
            };
            CreatureAnomalies.ReportStuck(creature, stuckMs, movingContext);
        }
    }

    private void CheckCombatStuck(Creature creature, uint diffMs)
    {
        bool inCombat = creature.Combat.IsInCombat || (creature.UnitFlags & UnitFlags.InCombat) != 0;
        if (!inCombat || !creature.CharmerOrOwnerGuid.IsEmpty)
        {
            creature.ResetStuckInCombatEpisode();
            return;
        }

        Unit? victim = creature.Combat.Victim;
        bool hasValidVictim = victim is { IsAlive: true } && ReferenceEquals(victim.Map, Map);
        bool hasValidThreat = false;
        if (creature.Combat.HasThreatList)
        {
            foreach (ThreatEntry entry in creature.Combat.Threat.Entries)
            {
                if (entry.Target.IsAlive && ReferenceEquals(entry.Target.Map, Map))
                {
                    hasValidThreat = true;
                    break;
                }
            }
        }

        bool hasValidAttacker = false;
        foreach (Unit attacker in creature.Combat.Attackers)
        {
            if (attacker.IsAlive && ReferenceEquals(attacker.Map, Map))
            {
                hasValidAttacker = true;
                break;
            }
        }

        if (!hasValidVictim && !hasValidThreat && !hasValidAttacker)
        {
            uint elapsed = creature.AdvanceStuckInCombat(diffMs);
            if (elapsed >= CreatureAnomalies.StuckInCombatEmptyMs && !creature.StuckInCombatEpisodeReported)
            {
                CreatureAnomalies.ReportStuckInCombat(
                    creature,
                    elapsed,
                    "in combat with no valid victim, threat entries, or living attackers on map");
            }

            return;
        }

        if (victim is not null && (!victim.IsAlive || !ReferenceEquals(victim.Map, Map)))
        {
            uint elapsed = creature.AdvanceStuckInCombat(diffMs);
            if (elapsed >= CreatureAnomalies.StuckInCombatEmptyMs && !creature.StuckInCombatEpisodeReported)
            {
                CreatureAnomalies.ReportStuckInCombat(
                    creature,
                    elapsed,
                    $"victim {victim.Guid} is dead or not on map {Map.MapId}");
            }

            return;
        }

        if (_options.UnreachableTargetEvadeMs > 0
            && creature.TargetNotReachableMs >= _options.UnreachableTargetEvadeMs + CreatureAnomalies.StuckInCombatUnreachableGraceMs
            && (creature.Template.Behaviour & CreatureBehaviourFlags.NoUnreachableEvade) == 0
            && !Map.Combat.Hooks.IsInstanceable(Map.MapId))
        {
            if (!creature.StuckInCombatEpisodeReported)
            {
                CreatureAnomalies.ReportStuckInCombat(
                    creature,
                    creature.TargetNotReachableMs,
                    $"unreachable target timer ({creature.TargetNotReachableMs} ms) exceeded evade threshold ({_options.UnreachableTargetEvadeMs} ms) without evading");
            }

            return;
        }

        creature.ResetStuckInCombatEpisode();
    }

    private void CheckScriptedStateConsistency(Creature creature)
    {
        // SyncCrowdControl deliberately keeps a fear or confuse generator on top of Home while the creature evades: that stack is consistent.
        if (creature.IsEvading && creature.Motion.CurrentType != MovementGeneratorType.Home
            && creature.Motion.ActiveCrowdControl == CrowdControlMovement.None)
        {
            if (!creature.BossStateReported("evading-without-home-generator"))
            {
                CreatureAnomalies.ReportBossStateInconsistent(
                    creature,
                    "evading-without-home-generator",
                    $"Creature {creature.Guid} has IsEvading=true while Motion.CurrentType={creature.Motion.CurrentType}");
            }
        }
        else
        {
            creature.ClearBossStateInconsistent("evading-without-home-generator");
        }

        if (!creature.Combat.IsInCombat && !creature.IsEvading)
        {
            if (creature.AiImmobilizedCombatOnly && creature.AiImmobilized)
            {
                if (!creature.BossStateReported("combat-only-root-leaked-ooc"))
                {
                    CreatureAnomalies.ReportBossStateInconsistent(
                        creature,
                        "combat-only-root-leaked-ooc",
                        $"Creature {creature.Guid} kept combat-only AI root out of combat");
                }
            }
            else
            {
                creature.ClearBossStateInconsistent("combat-only-root-leaked-ooc");
            }
        }
    }

    private void CheckRespawnStuck(Creature creature, uint diffMs, bool progressedThisTick)
    {
        if (creature.DeathState == CreatureDeathState.Alive
            || creature.Spawn is not { } spawn
            || creature.RespawnAtMs == long.MaxValue
            || creature.RespawnDelayOverrideSeconds == Creature.RespawnNeverSeconds
            || _clockMs <= creature.RespawnAtMs)
        {
            return;
        }

        if (progressedThisTick
            || _scriptOnlySpawns.Contains(spawn.Guid)
            || !SpawnAllowed(spawn.Guid)
            || !LinkAllowsSpawn(creature))
        {
            return;
        }

        uint overdueDelta = (uint)Math.Min(diffMs, Math.Max(0L, _clockMs - creature.RespawnAtMs));
        uint overdueElapsed = creature.AdvanceOverdueRespawn(creature.DeathState, overdueDelta);
        if (overdueElapsed >= CreatureAnomalies.RespawnStuckGraceMs)
        {
            CreatureAnomalies.ReportRespawnStuck(
                creature,
                _clockMs - creature.RespawnAtMs,
                $"creature {creature.Guid} remains in state {creature.DeathState} for {overdueElapsed} ms past RespawnAtMs ({creature.RespawnAtMs})");
        }
    }

    private void CheckDormantRespawnStuck(uint diffMs)
    {
        if (_respawnAt.Count == 0 || diffMs == 0)
        {
            if (_respawnAt.Count == 0 && _reportedDormantRespawnStuck.Count > 0)
            {
                _reportedDormantRespawnStuck.Clear();
            }

            return;
        }

        _dormantSampleAccumMs += diffMs;
        if (_dormantSampleAccumMs < DormantRespawnSampleMs)
        {
            return;
        }

        uint sampleMs = _dormantSampleAccumMs;
        _dormantSampleAccumMs = 0;

        if (_reportedDormantRespawnStuck.Count > 0)
        {
            // A spawn that left _respawnAt (spawned, or rescheduled to another time) ends its episode: forget it.
            _reportedDormantRespawnStuck.RemoveWhere(key => !_respawnAt.TryGetValue(key.SpawnGuid, out long at) || at != key.RespawnAtMs);
        }

        foreach ((uint spawnGuid, long respawnAtMs) in _respawnAt)
        {
            if (respawnAtMs == long.MaxValue || _clockMs <= respawnAtMs)
            {
                _overdueDormantRespawnMs.Remove(spawnGuid);
                continue;
            }

            // A dormant entry on an unloaded grid is overdue by design (it spawns when the grid loads): only loaded grids are judged.
            CreatureSpawn? spawn = FindSpawnOnLoadedGrid(spawnGuid);
            if (spawn is null)
            {
                _overdueDormantRespawnMs.Remove(spawnGuid);
                continue;
            }

            if (_scriptOnlySpawns.Contains(spawnGuid) || !SpawnAllowed(spawnGuid))
            {
                _overdueDormantRespawnMs.Remove(spawnGuid);
                continue;
            }

            if (_groupOfSpawn.TryGetValue(spawnGuid, out Maps.SpawnGroups.SpawnGroupState? groupState))
            {
                if (!SpawnGroupConditionHolds(groupState.Definition, SpawnGroupCondition, WorldStateExpressionEvaluator)
                    || groupState.Objects.Count >= groupState.Definition.MaxCount)
                {
                    _overdueDormantRespawnMs.Remove(spawnGuid);
                    continue;
                }
            }

            if (PoolRefusesAtLoad(spawn) || !LinkAllowsDormantSpawn(spawn))
            {
                _overdueDormantRespawnMs.Remove(spawnGuid);
                continue;
            }

            if (_bySpawnGuid.TryGetValue(spawnGuid, out Creature? existing) && _creatures.ContainsKey(existing.Guid))
            {
                _overdueDormantRespawnMs.Remove(spawnGuid);
                continue;
            }

            uint delta = (uint)Math.Min(sampleMs, Math.Max(0L, _clockMs - respawnAtMs));
            uint overdue = _overdueDormantRespawnMs.GetValueOrDefault(spawnGuid) + delta;
            _overdueDormantRespawnMs[spawnGuid] = overdue;
            if (overdue >= CreatureAnomalies.RespawnStuckGraceMs && _reportedDormantRespawnStuck.Add((spawnGuid, respawnAtMs)))
            {
                CreatureAnomalies.ReportRespawnStuck(
                    spawn,
                    _clockMs - respawnAtMs,
                    $"dormant respawn {spawnGuid} on loaded grid remained unspawned for {overdue} ms past RespawnAtMs ({respawnAtMs})");
            }
        }
    }

    private bool LinkAllowsDormantSpawn(CreatureSpawn spawn)
    {
        if (!_options.Respawn.Linked)
        {
            return true;
        }

        uint flags = 0;
        if (_content.FindLink(spawn.Guid) is { } link)
        {
            flags = link.Flags;
        }
        else if (_content.FindTemplateLink(spawn.Entry, Map.MapId) is { } templateLink)
        {
            flags = templateLink.Flags;
        }

        return (flags & (LinkCantSpawnIfBossDead | LinkCantSpawnIfBossAlive)) == 0;
    }

    private CreatureSpawn? FindSpawnOnLoadedGrid(uint spawnGuid)
    {
        if (_poolSpawns.TryGetValue(spawnGuid, out CreatureSpawn? pooled))
        {
            return _grids.ContainsKey(ComputeGrid(pooled.X, pooled.Y)) ? pooled : null;
        }

        if (_groupSpawns.TryGetValue(spawnGuid, out CreatureSpawn? grouped))
        {
            return _grids.ContainsKey(ComputeGrid(grouped.X, grouped.Y)) ? grouped : null;
        }

        if (_spawnByGuid is null)
        {
            Dictionary<uint, CreatureSpawn> index = [];
            foreach (List<CreatureSpawn> list in _spawnsByGrid.Values)
            {
                foreach (CreatureSpawn sp in list)
                {
                    index.TryAdd(sp.Guid, sp);
                }
            }

            _spawnByGuid = index;
        }

        return _spawnByGuid.TryGetValue(spawnGuid, out CreatureSpawn? found) && _grids.ContainsKey(ComputeGrid(found.X, found.Y))
            ? found
            : null;
    }
}
