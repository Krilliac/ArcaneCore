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
        bool won = LastFightWasWon(creature, reason);
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
                    + (reasons is null ? "" : $"; evades, newest first: {reasons}"));
        }
    }

    /// <summary>
    /// One evade for the loop report: who asked, and the fight it ended (the victim, its state and distance, the attackers left, the motion).
    /// Without a victim it adds the last unit the creature attacked and its state now (live 2026-10-18: nearly every loop was "no victim,
    /// 0 att, Chase", e.g. six Stockade guards at -8784,830 evading together every 2-3 s). The ledger cuts a detail at 300 chars (297 and "..."): the report lists the newest evade first so the cut falls on the oldest.
    /// </summary>
    private string DescribeEvade(Creature creature, string reason)
    {
        UnitCombat combat = creature.Combat;
        string victim = combat.Victim is { } v ? DescribeEvadeTarget(creature, v) : "no victim";
        string threat = combat.HasThreatList ? $", threat {combat.Threat.Entries.Count}" : "";
        string last = combat.Victim is null && !combat.LastVictimGuid.IsEmpty ? ", last " + DescribeLastVictim(creature, combat.LastVictimGuid) : "";
        // Who last flagged the creature in combat, and how often since the flag was last cleared: an evade without a victim means something
        // re-flagged combat without threat (live 2026-10-22: Firemane mobs 4328/4329/4331, SelectHostileTarget(no victim, 0 att) every 2-3 s).
        // Only for an evade without a victim; the report lists the newest evade first (Creature.RecordEvadeForAnomaly), so the ledger's cut
        // at 300 chars drops the oldest evades, never the one that triggered the report.
        string flagged = combat.Victim is null ? ", " + combat.DescribeLastCombatSet() : "";
        return $"{reason}({victim}, {combat.AttackersInternal.Count} att{threat}, {creature.Motion.CurrentType}{last}{flagged})";
    }

    /// <summary>Whether the evading creature's victim, or else the last unit it attacked, is dead (the creature won the fight).</summary>
    private bool LastFightWasWon(Creature creature, string? reason = null)
    {
        if (reason == nameof(UpdateUnreachableTarget))
        {
            return true;
        }

        UnitCombat combat = creature.Combat;
        Unit? last = combat.Victim
            ?? (combat.LastVictimGuid.IsEmpty ? null : Map.FindPlayer(combat.LastVictimGuid) as Unit ?? Map.FindObject(combat.LastVictimGuid) as Unit);
        // A scripted evade ends a fight on purpose, unless a player is on the other side: the player, or a unit a player owns or charms (a
        // hunter's pet): a loop of those is a real defect.
        if (reason == CreatureAnomalies.ScriptedEvadeReason
            && combat.Victim is not Player
            && !combat.LastVictimGuid.IsPlayer // HighGuid.Player is 0: an empty GUID (cleared after each evade) must not read as a player
            && combat.LastVictimGuid.High != HighGuid.Pet
            && !(last?.CharmerOrOwnerGuid.IsPlayer ?? false))
        {
            return true;
        }

        if (last is null)
        {
            return !combat.LastVictimGuid.IsEmpty;
        }

        if (!last.IsAlive)
        {
            return true;
        }

        return combat.Victim is null
            && last is Creature lastNpc
            && (lastNpc.IsInEvadeMode || !Map.Combat.Hooks.CanAttack(creature, lastNpc));
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
    /// The under-map sample compares with the terrain surface alone (<see cref="ICreatureHeightProvider.GetTerrainHeight"/>), so a
    /// creature in a cave or a city sits "below" it. A model floor as the surface would excuse a creature fallen through a cellar floor
    /// (the floor is then over its head) and cost a model search on every sample. Before a conviction the models are asked (<see cref="IUnderMapClassifier"/>): only <see cref="UnderMapKind.UnderMap"/> convicts, Ok and Unknown do not.
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

        float? localFloor = _height.GetTerrainHeight(Map.MapId, creature.X, creature.Y, creature.Z + 5f);
        if (localFloor is { } validLocal && float.IsFinite(validLocal))
        {
            if (creature.Z < validLocal - CreatureAnomalies.UnderMapMarginYards && ConfirmUnderMap(creature))
            {
                if (TryRecoverClippedUnderTerrain(creature, validLocal) || TryLiftOntoFloorAbove(creature))
                {
                    return;
                }

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
        float? upperFloor = _height.GetTerrainHeight(Map.MapId, creature.X, creature.Y, probeZ);
        if (upperFloor is { } validUpper && float.IsFinite(validUpper)
            && creature.Z < validUpper - CreatureAnomalies.UnderMapMarginYards
            && ConfirmUnderMap(creature))
        {
            if (TryRecoverClippedUnderTerrain(creature, validUpper) || TryLiftOntoFloorAbove(creature))
            {
                return;
            }

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

    private bool TryRecoverClippedUnderTerrain(Creature creature, float surface)
    {
        if (Map.Collision.MeshPoints is null)
        {
            return false;
        }

        float depth = surface - creature.Z;
        if (depth <= 0f || Map.Collision.LineOfSight.GetModelHeight(Map.MapId, creature.X, creature.Y, surface, MathF.Max(1f, depth)) is not null)
        {
            return false;
        }

        float? lowerFloor = _height.GetHeight(Map.MapId, creature.X, creature.Y, creature.Z + 2.0f);
        if (lowerFloor is { } validLower && float.IsFinite(validLower) && validLower > Maps.Terrain.TerrainTile.InvalidHeight
            && MathF.Abs(validLower - surface) > 1.0f
            && validLower <= creature.Z + 2.5f)
        {
            return false;
        }

        float standZ = surface + GroundHeight.StandOffset;
        if (Vector2.DistanceSquared(new Vector2(creature.X, creature.Y), new Vector2(creature.Home.X, creature.Home.Y)) <= 1.0f)
        {
            creature.SetHome(creature.Home with { Z = standZ });
        }

        creature.Relocate(creature.X, creature.Y, standZ, creature.Orientation, unchecked((uint)_clockMs));
        creature.ClearUnderMapEpisode();
        return true;
    }

    /// <summary>
    /// A creature already convicted of being under the map that has a navmesh floor straight above within 10 yd and none below within
    /// 60 yd (a tunnel or cellar ceiling sits between it and the terrain, which <see cref="TryRecoverClippedUnderTerrain"/> declines):
    /// it is put on that floor (when it is more than a yard below it). Reported once per episode: the episode stays open until a later sample
    /// finds the creature on the floor, so a mover that keeps putting it back under is lifted again without a new report.
    /// A running spline is stopped first (it would carry the creature on from its own, wrong, heights), and the creature's movement generator launches a fresh one. This heals a stranded creature. Live 2026-10-16..23: 928 entry-416 (imp) reports,
    /// 15.0-376.8 yd under the terrain surface (map 0: 801, p50 132 yd; map 1: 88, p50 27 yd; map 489: 39, 15.1-23.9 yd). On map 489 they
    /// lie in the two base tunnels (y 1456-1466): 7 on the tunnel floor (older classifiers), 27 at 2.2-4.6 yd and 2 (2026-10-23) at
    /// 6.5-6.7 yd under it, on the chord of a navmesh leg from the foot of the tunnel ramp to its far end. A path long enough to hit
    /// DrapePath's point cap doubled the sample stride; samples more than the walker window under the ramp's top were dropped, and the
    /// draped leg (1399.0, 1461.2, 328.2)-(1491.2, 1461.3, 342.8) ran 7.1 yd under the floor (z 336.0 at x 1448.2; logged 335.9).
    /// Only the two map 489 reports of 2026-10-23 are tied to that cap, and GroundHeight.DrapePath now keeps the most deviating points
    /// instead of coarsening the leg. The map 0 and map 1 reports (889; the map 0 ones cluster in city interiors, to 376.8 yd deep, and 17
    /// of them came after ground snap shipped) are unexplained and not investigated: this lift does not cover them and nothing here fixes their cause.
    /// </summary>
    private bool TryLiftOntoFloorAbove(Creature creature)
    {
        const float LiftYards = 10f;
        const float MinLiftYards = 1f;
        if (!Map.Collision.TryGetMeshFloor(creature.X, creature.Y, creature.Z, 0f, LiftYards, out float floor)
            || !float.IsFinite(floor)
            || floor - creature.Z <= MinLiftYards
            || Map.Collision.TryGetMeshFloor(creature.X, creature.Y, creature.Z, 60f, 0f, out _))
        {
            return false;
        }

        float standZ = floor + GroundHeight.StandOffset;
        CreatureAnomalies.ReportUnderMap(
            creature,
            floor,
            $"Creature {creature.Guid} was {floor - creature.Z:F1} yd under a navmesh floor (z={creature.Z:F1}, floor={floor:F1}) at ({creature.X:F1}, {creature.Y:F1}) on map {Map.MapId}: lifted onto it");
        // The stop packet carries the old (under-floor) position and Relocate sends none, so clients see the corrected z once the
        // movement generator launches its next spline; an idle creature is not re-sent until it next moves, so clients keep seeing it under the floor until then.
        StopMoving(creature);
        if (Vector2.DistanceSquared(new Vector2(creature.X, creature.Y), new Vector2(creature.Home.X, creature.Home.Y)) <= 1.0f)
        {
            creature.SetHome(creature.Home with { Z = standZ });
        }

        creature.Relocate(creature.X, creature.Y, standZ, creature.Orientation, unchecked((uint)_clockMs));
        return true;
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
            CreatureAnomalies.ReportStuck(creature, stuckMs, movingContext, chasing: movingKind == 2 || (movingKind == 1 && creature.Motion.CurrentType == MovementGeneratorType.Chase));
        }
    }

    private void CheckCombatStuck(Creature creature, uint diffMs)
    {
        if ((creature.UnitFlags & UnitFlags.InCombat) == 0 || creature.IsEvading || !creature.CharmerOrOwnerGuid.IsEmpty)
        {
            creature.ResetStuckInCombatEpisode();
            return;
        }

        UnitCombat combat = creature.Combat;
        Unit? victim = combat.Victim;
        bool hasValidVictim = victim is { IsAlive: true } && ReferenceEquals(victim.Map, Map);
        bool hasValidThreat = false;
        if (combat.HasThreatList)
        {
            IReadOnlyList<ThreatEntry> entries = combat.Threat.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                Unit target = entries[i].Target;
                if (target.IsAlive && ReferenceEquals(target.Map, Map))
                {
                    hasValidThreat = true;
                    break;
                }
            }

            if (!hasValidThreat)
            {
                IReadOnlyList<ThreatEntry> offline = combat.Threat.OfflineEntries;
                for (int i = 0; i < offline.Count; i++)
                {
                    Unit target = offline[i].Target;
                    if (target.IsAlive && ReferenceEquals(target.Map, Map))
                    {
                        hasValidThreat = true;
                        break;
                    }
                }
            }
        }

        bool hasValidAttacker = false;
        foreach (Unit attacker in combat.AttackersInternal)
        {
            if (attacker.IsAlive && ReferenceEquals(attacker.Map, Map))
            {
                hasValidAttacker = true;
                break;
            }
        }

        if (!hasValidVictim && !hasValidThreat && !hasValidAttacker)
        {
            // A critter (CritterAI, e.g. entry 4075 Rat) or NO_MELEE_FLEE creature flees in combat for EscapeTimerMs (30 s)
            // with no victim by design before calling EnterEvadeMode.
            uint emptyThresholdMs = (creature.AI is CritterAI || creature.InNoMeleePanic)
                && creature.Motion.CurrentType == MovementGeneratorType.Fleeing
                ? CritterAI.EscapeTimerMs + CreatureAnomalies.StuckInCombatEmptyMs
                : CreatureAnomalies.StuckInCombatEmptyMs;
            uint elapsed = creature.AdvanceStuckInCombat(diffMs);
            if (elapsed >= emptyThresholdMs && !creature.StuckInCombatEpisodeReported)
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

        if ((creature.UnitFlags & UnitFlags.InCombat) == 0 && !creature.IsEvading)
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
            return _grids.ContainsKey(GridDefines.ComputeGridCoord(pooled.X, pooled.Y)) ? pooled : null;
        }

        if (_groupSpawns.TryGetValue(spawnGuid, out CreatureSpawn? grouped))
        {
            return _grids.ContainsKey(GridDefines.ComputeGridCoord(grouped.X, grouped.Y)) ? grouped : null;
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

        return _spawnByGuid.TryGetValue(spawnGuid, out CreatureSpawn? found) && _grids.ContainsKey(GridDefines.ComputeGridCoord(found.X, found.Y))
            ? found
            : null;
    }
}
