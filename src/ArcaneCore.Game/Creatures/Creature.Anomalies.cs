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
    /// <paramref name="reasons"/>, newest first, so a loop report says which path keeps sending the creature home.</param>
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
                _evadeReasons = _evadeReasons is null ? reason : reason + "; " + _evadeReasons; // newest first: the ledger's cut falls on the oldest
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

        _stuckElapsedMs += Math.Min(diffMs, CreatureAnomalies.MaxChargedFrameMs);
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
        _stuckInCombatElapsedMs += Math.Min(diffMs, CreatureAnomalies.MaxChargedFrameMs);
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
