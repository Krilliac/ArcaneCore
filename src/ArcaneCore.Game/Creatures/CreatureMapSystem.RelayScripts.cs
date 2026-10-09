using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Relay DB scripts (cmangos <c>dbscripts_on_relay</c>, run by EventAI's START_RELAY_SCRIPT action 53 and by a relay's own
/// START_RELAY_SCRIPT command 45): the scheduling of <see cref="RelayScriptRunner"/>, the source / target / buddy resolution of cmangos
/// ScriptAction::GetScriptProcessTargets (DBScripts/ScriptMgr.cpp:1360-1645) and the commands this server carries out
/// (ScriptAction::ExecuteDbscriptCommand, :1767-3180). A step of any other command, or with a data flag outside
/// <see cref="SupportedDataFlags"/>, is skipped and reported once per relay id.
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// SCRIPT_FLAG_BUDDY_AS_TARGET, REVERSE_DIRECTION, SOURCE_TARGETS_SELF, COMMAND_ADDITIONAL, BUDDY_BY_GUID, BUDDY_IS_DESPAWNED,
    /// ALL_ELIGIBLE_BUDDIES and BUDDY_BY_GO (ScriptMgr.h:149-163). Pet, pool, spawn group and string id buddies are not supported.
    /// </summary>
    public const uint SupportedDataFlags = 0x001 | 0x002 | 0x004 | 0x008 | 0x010 | 0x040 | 0x200 | 0x400;

    /// <summary>The point id of a MOVE_TO that starts a relay on arrival (cmangos MovePoint with a relay id).</summary>
    public const uint RelayMovePointId = 0xFFFF_F1E1;

    private const uint FlagBuddyAsTarget = 0x001;
    private const uint FlagReverseDirection = 0x002;
    private const uint FlagSourceTargetsSelf = 0x004;
    private const uint FlagCommandAdditional = 0x008;
    private const uint FlagBuddyByGuid = 0x010;
    private const uint FlagBuddyIsDespawned = 0x040;
    private const uint FlagAllEligibleBuddies = 0x200;
    private const uint FlagBuddyByGo = 0x400;

    private readonly RelayScriptRunner _relays = new();
    private readonly Dictionary<DbScriptKind, RelayScriptRunner> _dbScriptRunners = new()
    {
        [DbScriptKind.QuestStart] = new(),
        [DbScriptKind.QuestEnd] = new(),
        [DbScriptKind.Gossip] = new(),
        [DbScriptKind.Event] = new(),
        [DbScriptKind.CreatureMovement] = new(),
    };
    private readonly Dictionary<Creature, (uint RelayId, ObjectGuid Target)> _arrivalRelays = [];
    private readonly List<(Creature Creature, long AtMs)> _scriptDespawns = [];

    /// <summary>Relay steps waiting to run on this map.</summary>
    public int PendingRelaySteps => _relays.PendingCount;

    /// <summary>Steps of quest, gossip and event scripts waiting on this map's clock.</summary>
    public int PendingDbScriptSteps => _dbScriptRunners.Values.Sum(runner => runner.PendingCount);

    /// <summary>
    /// cmangos Map::ScriptsStart for the independent quest, gossip and event namespaces. Source and target must be live
    /// world objects on this map; the executor resolves them again when a delayed command becomes due.
    /// </summary>
    public bool StartDbScript(DbScriptKind kind, uint scriptId, WorldObject source, WorldObject? target)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (kind == DbScriptKind.Relay)
        {
            return StartRelayScript(scriptId, source, target);
        }

        if (!_dbScriptRunners.TryGetValue(kind, out RelayScriptRunner? runner))
        {
            return false;
        }

        IReadOnlyList<RelayScriptStep> steps = _content.Ai.DbScripts.Get(kind, scriptId);
        if (steps.Count == 0)
        {
            return false;
        }

        return runner.Start(steps, source.Guid, target?.Guid ?? default, _clockMs, ExecuteRelayStep);
    }

    /// <summary>
    /// cmangos Map::ScriptsStart(SCRIPT_TYPE_RELAY, id, source, target): run relay <paramref name="relayId"/> with
    /// <paramref name="source"/> and <paramref name="target"/>. False when the relay does not exist or already runs for the pair.
    /// </summary>
    public bool StartRelayScript(uint relayId, WorldObject source, WorldObject? target)
    {
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<RelayScriptStep> steps = _content.Ai.RelayScripts.Get(relayId);
        if (steps.Count == 0)
        {
            if (_reportedAi.Add($"relay-missing:{relayId}"))
            {
                _logger.LogWarning("relay script {Relay} does not exist (dbscripts_on_relay); not started", relayId);
            }

            return false;
        }

        return _relays.Start(steps, source.Guid, target?.Guid ?? default, _clockMs, ExecuteRelayStep);
    }

    /// <summary>cmangos ScriptMgr::GetRandomRelayDbscriptFromTemplate: a relay id from template <paramref name="templateId"/> (0: none chosen).</summary>
    public uint SelectRelayFromTemplate(uint templateId)
        => _content.Ai.RelayScripts.SelectFromTemplate(templateId, _random.Next(0, 1_000_001) / 10_000f, count => _random.Next(0, count));

    private void UpdateRelayScripts()
    {
        _relays.Update(_clockMs, ExecuteRelayStep);
        foreach (RelayScriptRunner runner in _dbScriptRunners.Values)
        {
            runner.Update(_clockMs, ExecuteRelayStep);
        }
        for (int i = _scriptDespawns.Count - 1; i >= 0; i--)
        {
            if (_scriptDespawns[i].AtMs <= _clockMs)
            {
                Creature creature = _scriptDespawns[i].Creature;
                _scriptDespawns.RemoveAt(i);
                Despawn(creature);
            }
        }
    }

    /// <summary>A MOVE_TO with a relay id reached its point: the relay starts with the creature as the source (cmangos PointMovementGenerator).</summary>
    private void OnRelayMoveArrived(Creature creature)
    {
        if (_arrivalRelays.Remove(creature, out (uint RelayId, ObjectGuid Target) arrival) && creature.IsAlive)
        {
            StartRelayScript(arrival.RelayId, creature, Map.FindObject(arrival.Target));
        }
    }

    /// <summary>
    /// One step (cmangos ScriptAction::HandleScriptStep, DBScripts/ScriptMgr.cpp:1704-1764): resolve the sources and targets with the buddy
    /// flags, then run the command once for every source and target pair (once for every source when there is no target); the step ends its
    /// script when any run answers true. The condition is checked per pair (ExecuteDbscriptCommand, :1768-1769).
    /// </summary>
    private bool ExecuteRelayStep(RelayScriptPendingStep pending)
    {
        RelayScriptStep step = pending.Step;
        if ((step.DataFlags & ~SupportedDataFlags) != 0)
        {
            ReportRelay(step, $"data_flags 0x{step.DataFlags:X}");
            return false;
        }

        WorldObject? source = pending.Source.IsEmpty ? null : Map.FindObject(pending.Source);
        WorldObject? target = pending.Target.IsEmpty ? null : Map.FindObject(pending.Target);
        if ((!pending.Source.IsEmpty && source is null) || (!pending.Target.IsEmpty && target is null))
        {
            return false; // ScriptAction::GetScriptCommandObject: the object left the map
        }

        List<WorldObject?> sources = source is null ? [] : [source];
        List<WorldObject?> targets = target is null ? [] : [target];
        bool buddyFound = false;
        if (step.BuddyEntry != 0 || (step.DataFlags & FlagBuddyByGuid) != 0)
        {
            List<WorldObject?>? buddies = FindRelayBuddies(step, source, target);
            if (buddies is null)
            {
                return false; // "has buddy ... not found", skipping
            }

            buddyFound = buddies.Count > 0;
            if ((step.DataFlags & FlagBuddyAsTarget) != 0)
            {
                targets = buddies;
            }
            else if (buddyFound)
            {
                sources = buddies;
            }
        }

        if ((step.DataFlags & FlagReverseDirection) != 0)
        {
            (sources, targets) = (targets, sources);
        }

        if ((step.DataFlags & FlagSourceTargetsSelf) != 0)
        {
            targets = sources;
        }

        bool terminate = false;
        foreach (WorldObject? actor in sources.ToArray())
        {
            foreach (WorldObject? acted in targets.Count == 0 ? [null] : targets.ToArray())
            {
                if (step.ConditionId != 0 && !RelayConditionHolds(step.ConditionId, actor, acted))
                {
                    continue;
                }

                terminate |= RunRelayCommand(step, actor, acted, buddyFound);
            }
        }

        return terminate;
    }

    /// <summary>
    /// The buddies of a step (ScriptMgr.cpp:1364-1621), or null when the step must be skipped. By guid (BUDDY_BY_GUID, <c>search_radius</c>
    /// holding the database guid): the creature spawn, which must be alive (dead with BUDDY_IS_DESPAWNED), or the game object spawn. By entry:
    /// searched around the source (the target when there is no source); the nearest one of <c>buddy_entry</c> within <c>search_radius</c>, or
    /// every one with ALL_ELIGIBLE_BUDDIES. Whether the buddy is a creature or a game object depends on the command
    /// (<see cref="IsCreatureBuddy"/>). When nothing is found the step is skipped, except TERMINATE_SCRIPT, which then gets an empty list.
    /// </summary>
    private List<WorldObject?>? FindRelayBuddies(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        bool creatureBuddy = IsCreatureBuddy(step);
        bool terminate = step.Command == 31;
        var buddies = new List<WorldObject?>();
        GameObjects.GameObjectMapSystem? objects = creatureBuddy ? null : Map.FindUpdater<GameObjects.GameObjectMapSystem>();
        if ((step.DataFlags & FlagBuddyByGuid) != 0)
        {
            if (creatureBuddy)
            {
                Creature? byGuid = _creatures.Values.FirstOrDefault(c => c.Spawn?.Guid == step.SearchRadius);
                if (byGuid is not null && byGuid.IsAlive == ((step.DataFlags & FlagBuddyIsDespawned) != 0))
                {
                    if (!terminate)
                    {
                        return null; // "has buddy ... by guid ... but buddy is dead"
                    }
                }

                if (byGuid is not null)
                {
                    buddies.Add(byGuid);
                }
            }
            else if (objects?.GameObjects.FirstOrDefault(g => g.Spawn?.Guid == step.SearchRadius) is { } byGuid)
            {
                buddies.Add(byGuid);
            }

            return buddies.Count == 0 && !terminate ? null : buddies;
        }

        WorldObject? origin = source ?? target;
        if (origin is null)
        {
            return terminate ? buddies : null;
        }

        bool all = (step.DataFlags & FlagAllEligibleBuddies) != 0;
        float range = step.SearchRadius * (float)step.SearchRadius;
        if (creatureBuddy)
        {
            if (all)
            {
                bool wantAlive = (step.DataFlags & FlagBuddyIsDespawned) == 0;
                buddies.AddRange(_creatures.Values.Where(c => c.Template.Entry == step.BuddyEntry && c.IsAlive == wantAlive
                    && DistanceSquared(c, origin) <= range));
            }
            else if (FindRelayBuddy(step, origin) is { } nearest)
            {
                buddies.Add(nearest);
            }
        }
        else if (objects is not null)
        {
            GameObjects.GameObject[] inRange = [.. objects.GameObjects.Where(g => g.Entry == step.BuddyEntry && DistanceSquared(g, origin) <= range)];
            if (all)
            {
                buddies.AddRange(inRange);
            }
            else if (inRange.MinBy(g => DistanceSquared(g, origin)) is { } nearestObject)
            {
                buddies.Add(nearestObject);
            }
        }

        return buddies.Count == 0 && !terminate ? null : buddies;
    }

    /// <summary>
    /// cmangos ScriptInfo::IsCreatureBuddy (ScriptMgr.h:542-575): the object commands (respawn, doors, activate, lock state, despawn and
    /// reset of a game object) look for a game object; TERMINATE_SCRIPT, SET_FACING and MOVE_DYNAMIC for one with BUDDY_BY_GO; every other
    /// command for a creature.
    /// </summary>
    private static bool IsCreatureBuddy(RelayScriptStep step) => step.Command switch
    {
        31 or 36 or 37 => (step.DataFlags & FlagBuddyByGo) == 0,
        9 or 11 or 12 or 13 or 27 or 40 or 43 => false,
        _ => true,
    };

    /// <summary>
    /// The nearest creature buddy by entry (ScriptMgr.cpp:1532-1585) around <paramref name="origin"/> (the reference's "prefer non-players"
    /// swap only fires when the source is no player, so it never changes the searcher): of <c>buddy_entry</c> within <c>search_radius</c>,
    /// alive (dead with BUDDY_IS_DESPAWNED), other than the searcher.
    /// </summary>
    private Creature? FindRelayBuddy(RelayScriptStep step, WorldObject origin)
    {
        bool wantDead = (step.DataFlags & FlagBuddyIsDespawned) != 0;
        float best = step.SearchRadius * (float)step.SearchRadius;
        Creature? found = null;
        foreach (Creature candidate in _creatures.Values)
        {
            if (candidate.Template.Entry != step.BuddyEntry || ReferenceEquals(candidate, origin) || candidate.IsAlive == wantDead)
            {
                continue;
            }

            float distance = DistanceSquared(candidate, origin);
            if (distance <= best)
            {
                best = distance;
                found = candidate;
            }
        }

        return found;
    }

    /// <summary>cmangos IsConditionSatisfied(condition, target, map, source): this server evaluates conditions for a player only.</summary>
    private bool RelayConditionHolds(uint conditionId, WorldObject? source, WorldObject? target)
    {
        Player? player = target as Player ?? source as Player;
        if (player is null || _ai.Conditions is not { } conditions)
        {
            return false;
        }

        Creature? npc = source as Creature ?? target as Creature;
        NpcInfo? info = npc is null ? null : new NpcInfo(npc.Guid, npc.Entry, npc.Spawn?.Guid ?? npc.Guid.Low, (NpcFlags)npc.NpcFlags, npc.MapId,
            npc.X, npc.Y, npc.Z, npc.BoundingRadius, npc.IsAlive, IsHostile: false, npc.Combat.IsInCombat,
            (npc.UnitFlags & UnitFlags.NotSelectable) != 0, npc.Template.GossipMenuId);
        return conditions.IsSatisfied(conditionId, player, info);
    }

    private bool RunRelayCommand(RelayScriptStep step, WorldObject? source, WorldObject? target, bool buddyFound)
    {
        switch (step.Command)
        {
            case 0: // SCRIPT_COMMAND_TALK (:1774-1810): dataint (or one of dataint..4 at random), or a string template in datalong
            {
                if (source is not Creature speaker)
                {
                    ReportRelay(step, "TALK by a non-creature");
                    return false;
                }

                int textId = step.DataLong != 0
                    ? _content.Ai.SelectTemplateText(step.DataLong, _random.Next(0, 1_000_001) / 10_000f, count => _random.Next(0, count))
                    : PickFilled(step.DataInt, step.DataInts);
                if (textId != 0 && _content.Ai.FindText(textId) is { } text)
                {
                    Say(speaker, text, target as Unit);
                }

                return false;
            }

            case 1: // SCRIPT_COMMAND_EMOTE (:1812-1828): datalong, or one of datalong and dataint..4 at random
            {
                if (source is not Creature actor)
                {
                    ReportRelay(step, "EMOTE by a non-creature");
                    return false;
                }

                var emotes = new List<uint> { step.DataLong };
                foreach (int extra in step.DataInts)
                {
                    if (extra == 0)
                    {
                        break;
                    }

                    emotes.Add((uint)extra);
                }

                PlayEmote(actor, emotes[_random.Next(0, emotes.Count)]);
                return false;
            }

            case 3: // SCRIPT_COMMAND_MOVE_TO (:1843-1897)
                RelayMoveTo(step, source, target);
                return false;

            case 7: // SCRIPT_COMMAND_QUEST_EXPLORED (cmangos ScriptMgr.cpp:1934-1966; the player: GetPlayerTargetOrSourceAndLog, :1685-1694)
            {
                Player? player = target as Player ?? source as Player;
                WorldObject? partner = source is Creature or GameObjects.GameObject ? source
                    : target is Creature or GameObjects.GameObject ? target : null;
                if (player is null || (step.DataLong2 != 0 && partner is null))
                {
                    return false;
                }

                bool failed = partner is Creature { IsAlive: false }
                    || (step.DataLong2 != 0 && (!ReferenceEquals(partner!.Map, player.Map) || !WithinDistInMap(partner, player, step.DataLong2)));
                if (failed)
                {
                    _ai.ScriptQuests?.FailQuest(player, step.DataLong);
                }
                else
                {
                    _ai.ScriptQuests?.AreaExploredOrEventHappens(player, step.DataLong);
                }

                return false;
            }

            case 8: // SCRIPT_COMMAND_KILL_CREDIT (cmangos ScriptMgr.cpp:1968-1999)
            {
                Player? player = target as Player ?? source as Player;
                Creature? partner = source as Creature ?? target as Creature;
                uint entry = step.DataLong != 0 ? step.DataLong : partner?.Entry ?? 0;
                if (player is null || entry == 0)
                {
                    return false;
                }

                // Group credit (RewardPlayerAndGroupAtEventCredit) searches around the creature partner; without one cmangos searches around
                // the source and logs that the script needs adjusting: here the player alone gets the credit then.
                if (step.DataLong2 != 0 && partner is not null && _ai.QuestEvents is { } groups)
                {
                    groups.KillCredit(player, entry, partner);
                }
                else
                {
                    _ai.ScriptQuests?.KilledMonsterCredit(player, entry, partner?.Guid ?? default);
                }

                return false;
            }

            case 10: // SCRIPT_COMMAND_TEMP_SPAWN_CREATURE (:2048-2073)
                RelayTempSpawn(step, source);
                return false;

            case 11: // SCRIPT_COMMAND_OPEN_DOOR (ScriptMgr.cpp:2074-2111), used by Zul'Farrak's cage event 2609
            {
                if (Map.FindUpdater<GameObjects.GameObjectMapSystem>() is { } objects
                    && objects.GameObjects.FirstOrDefault(go => go.Spawn?.Guid == step.DataLong) is { } door
                    && door.State == GameObjects.GameObjectState.Ready)
                    objects.ToggleDoorOrButton(door, Math.Max(15u, step.DataLong2));
                return false;
            }

            case 22: // SCRIPT_COMMAND_SET_FACTION (ScriptMgr.cpp:2394-2409)
                if (source is Creature factionTarget)
                    factionTarget.FactionTemplate = step.DataLong != 0 ? step.DataLong : factionTarget.Template.Faction;
                return false;

            case 13: // SCRIPT_COMMAND_ACTIVATE_OBJECT (:2115-2128)
                RelayActivateObject(step, source, target);
                return false;

            case 20: // SCRIPT_COMMAND_MOVEMENT (:2277-2385)
                RelayMovement(step, source, target);
                return false;

            case 35: // SCRIPT_COMMAND_SEND_AI_EVENT (:2759-2775)
                RelaySendAiEvent(step, source, target);
                return false;

            case 15: // SCRIPT_COMMAND_CAST_SPELL (:2155-2202): datalong, or one of dataint..4; datalong2 cast flags (TRIGGERED_OLD_TRIGGERED 0x01)
            {
                if (source is not Creature caster)
                {
                    ReportRelay(step, "CAST_SPELL by a non-creature");
                    return false;
                }

                int filled = step.DataInts.TakeWhile(i => i != 0).Count();
                uint spell = step.DataLong;
                if (filled > 0 && _random.Next(0, filled + 1) is int pick and > 0)
                {
                    spell = (uint)step.DataInts[pick - 1];
                }

                Unit? at = (step.DataFlags & FlagCommandAdditional) != 0 ? null : target as Unit;
                CastSpell(caster, spell, at, triggered: (step.DataLong2 & 0x01) != 0);
                return false;
            }

            case 18: // SCRIPT_COMMAND_DESPAWN_SELF (:2257-2272): the target creature (the source when only it is one), after datalong ms
            {
                Creature? leaving = target as Creature ?? source as Creature;
                if (leaving is null)
                {
                    return false;
                }

                if (leaving.Spawn is not null)
                {
                    ReportRelay(step, "DESPAWN_SELF of a database spawn (no forced despawn with respawn timer)");
                    return false;
                }

                if (step.DataLong == 0)
                {
                    Despawn(leaving);
                }
                else
                {
                    _scriptDespawns.Add((leaving, _clockMs + step.DataLong));
                }

                return false;
            }

            case 21: // SCRIPT_COMMAND_SET_ACTIVEOBJECT: this server keeps no active-object grid state; nothing to do
                return false;

            case 25: // SCRIPT_COMMAND_SET_RUN (:2460-2477)
                if (source is Creature runner)
                {
                    runner.ScriptRun = step.DataLong != 0;
                    SyncWalkMode(runner, runner.ScriptRun);
                }

                return false;

            case 26: // SCRIPT_COMMAND_ATTACK_START (cmangos ScriptAction::ExecuteDbscriptCommand, ScriptMgr.cpp:2479-2497)
                if (source is Creature attacker && target is Unit victim)
                {
                    _ = attacker.AI?.AttackStart(victim) ?? AttackStart(attacker, victim);
                }

                return false;

            case 28: // SCRIPT_COMMAND_STAND_STATE (:2526-2533)
                if (source is Creature stander)
                {
                    stander.StandState = (StandState)step.DataLong;
                }

                return false;

            case 29: // SCRIPT_COMMAND_MODIFY_NPC_FLAGS (:2535-2557): datalong2 0 remove, 1 add, 2 toggle
                if (source is Creature npc)
                {
                    npc.NpcFlags = step.DataLong2 switch
                    {
                        0 => npc.NpcFlags & ~step.DataLong,
                        1 => npc.NpcFlags | step.DataLong,
                        2 => npc.NpcFlags ^ step.DataLong,
                        _ => npc.NpcFlags,
                    };
                }

                return false;

            case 31: // SCRIPT_COMMAND_TERMINATE_SCRIPT (:2569-2703)
                return RelayTerminates(step, source, target, buddyFound);

            case 32: // SCRIPT_COMMAND_PAUSE_WAYPOINTS (:2705-2713): datalong 1 pause, 0 unpause
                if (source is Creature walker)
                {
                    PauseWaypoints(walker, step.DataLong != 0);
                }

                return false;

            case 36: // SCRIPT_COMMAND_SET_FACING (:2778-2797): face the target, or back to the reset facing with datalong
            {
                if (source is not Creature turner || target is null)
                {
                    return false;
                }

                if (step.DataLong != 0)
                {
                    CreatureHome reset = turner.Motion.Default.GetResetPosition(turner) ?? turner.Home;
                    SetFacingTo(turner, reset.Orientation);
                }
                else if (!turner.IsMoving)
                {
                    SetFacingTo(turner, MathF.Atan2(target.Y - turner.Y, target.X - turner.X)); // Unit::SetFacingToObject: never while moving
                }

                return false;
            }

            case 45: // SCRIPT_COMMAND_START_RELAY_SCRIPT (:2959-2972): datalong relay, or a relay from template datalong2
            {
                uint chosen = step.DataLong2 != 0 ? SelectRelayFromTemplate(step.DataLong2) : step.DataLong;
                if (chosen != 0 && source is not null)
                {
                    StartRelayScript(chosen, source, target);
                }

                return false;
            }

            default:
                ReportRelay(step, $"command {step.Command}");
                return false;
        }
    }

    /// <summary>
    /// SCRIPT_COMMAND_TERMINATE_SCRIPT: with an npc entry (datalong) the nearest living one within datalong2 yd of the searcher is looked
    /// for, otherwise the step's buddy search counts; the script ends when that found nothing, or with COMMAND_ADDITIONAL when it found
    /// something. Pool ids (datalong3) are not supported. The waypoint pause adjustment (dataint) is not applied.
    /// </summary>
    private bool RelayTerminates(RelayScriptStep step, WorldObject? source, WorldObject? target, bool buddyFound)
    {
        if (step.DataLong3 != 0)
        {
            ReportRelay(step, "TERMINATE_SCRIPT by pool");
            return false;
        }

        bool found = buddyFound;
        if (step.DataLong != 0)
        {
            WorldObject? searcher = source ?? target;
            if (searcher is Player && target is not null and not Player)
            {
                searcher = target;
            }

            found = searcher is not null && FindRelayBuddy(step with { BuddyEntry = step.DataLong, SearchRadius = step.DataLong2, DataFlags = 0 }, searcher) is not null;
        }

        bool additional = (step.DataFlags & FlagCommandAdditional) != 0;
        return additional ? found : !found;
    }

    /// <summary>
    /// SCRIPT_COMMAND_MOVE_TO for a creature source: dataint 1 (or 2) walks back to the spawn point; a zero or reached point only turns
    /// to <c>o</c>; x = y = 0 moves by z; otherwise it moves to (x, y, z) after clearing its pushed movement, walking unless SET_RUN said
    /// run, and on arrival starts relay datalong (source the creature, target the step's target). COMMAND_ADDITIONAL (teleport) is reported
    /// and skipped. Speed (datalong2, speed) and forced movement (datalong3) are not modelled.
    /// </summary>
    private void RelayMoveTo(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        if (source is not Creature mover)
        {
            ReportRelay(step, "MOVE_TO by a non-creature");
            return;
        }

        if (step.DataInt is 1 or 2)
        {
            MoveRelayPoint(mover, mover.Home.X, mover.Home.Y, mover.Home.Z, step.DataLong, target);
            return;
        }

        float dx = step.X - mover.X;
        float dy = step.Y - mover.Y;
        float dz = step.Z - mover.Z;
        if ((step.X == 0f && step.Y == 0f && step.Z == 0f) || (dx * dx) + (dy * dy) + (dz * dz) < 0.0001f)
        {
            SetFacingTo(mover, step.Orientation);
            return;
        }

        if (step.X == 0f && step.Y == 0f)
        {
            MoveRelayPoint(mover, mover.X, mover.Y, mover.Z + step.Z, step.DataLong, target);
            return;
        }

        if ((step.DataFlags & FlagCommandAdditional) != 0)
        {
            ReportRelay(step, "MOVE_TO teleport");
            return;
        }

        mover.Motion.Clear();
        MoveRelayPoint(mover, step.X, step.Y, step.Z, step.DataLong, target);
    }

    private void MoveRelayPoint(Creature mover, float x, float y, float z, uint arrivalRelay, WorldObject? target)
    {
        if (arrivalRelay != 0)
        {
            _arrivalRelays[mover] = (arrivalRelay, target?.Guid ?? default);
        }

        mover.Motion.MovePoint(RelayMovePointId, x, y, z, mover.ScriptRun);
    }

    /// <summary>
    /// cmangos MotionMaster::PauseWaypoints(0) / UnpauseWaypoints (MotionGenerators/MotionMaster.cpp:629-676): pausing stops the creature
    /// and holds its waypoint movement until unpaused (UNIT_STAT_WAYPOINT_PAUSED); unpausing sets off for the same node again.
    /// </summary>
    public void PauseWaypoints(Creature creature, bool pause)
    {
        ArgumentNullException.ThrowIfNull(creature);
        creature.WaypointsPaused = pause;
        if (pause)
        {
            StopMoving(creature);
        }
    }

    /// <summary>
    /// vmangos Unit::HandleEmote (Objects/Unit.cpp:1861-1872): 0 clears the emote state; an emote that is a state (Emotes.dbc EmoteType 1
    /// or 2, here the EMOTE_STATE_* ids of <see cref="CreatureEmotes"/>) becomes UNIT_NPC_EMOTESTATE; any other plays once (SMSG_EMOTE).
    /// </summary>
    public void PlayEmote(Creature creature, uint emote)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (emote == 0 || CreatureEmotes.IsState(emote))
        {
            creature.SetUInt32(UpdateFields.UnitNpcEmotestate, emote);
            return;
        }

        Map.BroadcastToObservers(creature, WorldOpcode.SmsgEmote, CreatureChatPackets.BuildEmote(emote, creature.Guid));
    }

    /// <summary>cmangos picks one of the filled dataint columns (the first plus the following non-zero ones) at random.</summary>
    private int PickFilled(int first, IReadOnlyList<int> values)
    {
        if (values.Count < 2 || values[1] == 0)
        {
            return first;
        }

        int filled = values.TakeWhile(v => v != 0).Count();
        return values[_random.Next(0, filled)];
    }

    private void ReportRelay(RelayScriptStep step, string what)
    {
        if (_reportedAi.Add($"relay:{step.Id}:{what}"))
        {
            _logger.LogWarning("relay script {Relay} uses unsupported {What}; that step is skipped", step.Id, what);
        }
    }
}

/// <summary>
/// Which emote ids are states. Emotes.dbc's EmoteType decides it in the references (vmangos Unit::HandleEmote); this server does not load
/// Emotes.dbc, so the EMOTE_STATE_* ids of vmangos SharedDefines.h:489-597 (the Emotes.dbc rows of type 1 and 2 by their names) stand in.
/// </summary>
public static class CreatureEmotes
{
    private static readonly HashSet<uint> States =
    [
        10, 12, 13, 26, 27, 28, 29, 30, 64, 65, 68, 69, 93, 133, 173, 193, 214, 233, 234, 253, 313, 333, 353, 373, 375, 376, 378, 379, 382,
        383, 384, 385, 386, 391, 392, 398, 400, 412, 415, 422, 423,
    ];

    /// <summary>Whether <paramref name="emote"/> is an emote state (it persists in UNIT_NPC_EMOTESTATE instead of playing once).</summary>
    public static bool IsState(uint emote) => States.Contains(emote);
}
