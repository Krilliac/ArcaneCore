using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Creature AI driving for one map (vmangos CreatureAI host parts of Creature/Unit, re-implemented):
/// aggro on sight, attack start with the assistance call, victim selection from the threat list
/// with the leash, evade and return home, flee for assistance, texts, summons and the spell seam.
/// Every hook runs on the world thread inside <see cref="Update"/>.
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>vmangos CREATURE_Z_ATTACK_RANGE: no aggro on a unit more than 3 yd above or below.</summary>
    public const float MaxAggroZDistance = 3.0f;

    /// <summary>vmangos: the aggro radius never exceeds base + 25 levels.</summary>
    public const int MaxAggroLevelDifference = 25;

    /// <summary>vmangos base aggro radius against a unit of the same level (yd).</summary>
    public const float BaseAggroRadius = 20.0f;

    /// <summary>vmangos minimum aggro radius (yd).</summary>
    public const float MinAggroRadius = 5.0f;

    /// <summary>The point id a creature fleeing for assistance runs to its helper with.</summary>
    public const uint FleeForAssistancePointId = 0xFFFF_F1EE;

    private const UnitFlags NotAssistable = UnitFlags.NonAttackable2 | UnitFlags.NotAttackable1 | UnitFlags.NotSelectable;

    private readonly CreatureAiServices _ai;
    private readonly List<PendingAssist> _pendingAssists = [];
    private readonly List<(Creature Creature, long DespawnAtMs)> _summons = [];
    private readonly HashSet<string> _reportedAi = [];
    private bool _combatSubscribed;

    /// <summary>The services this map's creature AI uses (hostility, spells, AI factory); paths and sight come from <c>Map.Collision</c>.</summary>
    public CreatureAiServices AiServices => _ai;

    /// <summary>Delayed assistance calls waiting for their delay (helper, enemy, due time on <see cref="ClockMs"/>).</summary>
    public int PendingAssistCount => _pendingAssists.Count;

    // --- aggro ---------------------------------------------------------------------------------

    /// <summary>
    /// vmangos Creature::GetAttackDistance: 20 yd at equal level, one yard less per level the
    /// target is above the creature and one more per level below (at most 25 levels counted
    /// below), never under 5 yd, times <see cref="CreatureOptions.AggroRate"/>.
    /// </summary>
    public float GetAttackDistance(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        float rate = _options.AggroRate;
        if (rate <= 0)
        {
            return 0;
        }

        int levelDifference = Math.Max(target.Level - creature.Level, -MaxAggroLevelDifference);
        float distance = Math.Max(BaseAggroRadius - levelDifference, MinAggroRadius);
        return distance * rate;
    }

    /// <summary>
    /// Whether <paramref name="creature"/> attacks <paramref name="who"/> on sight (vmangos
    /// AggressorAI::MoveInLineOfSight → Creature::CanInitiateAttack + IsHostileTo + range checks):
    /// alive, idle, not evading, not civilian, no NO_AGGRO extra flag; the target an attackable
    /// living player (not GM) the hostility seam calls an enemy; within the aggro radius (3D,
    /// both bounding radii) and 3 yd vertically; in line of sight.
    /// </summary>
    public bool CanAggroOnSight(Creature creature, Unit who)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(who);
        if (who is not Player player || player.IsGameMaster || !player.IsAlive || !ReferenceEquals(player.Map, Map))
        {
            return false; // creature-versus-creature aggro is not modelled (docs/areas/creature-ai.md)
        }

        if (!creature.IsAlive || creature.IsEvading || creature.Combat.Victim is not null || !_creatures.ContainsKey(creature.Guid)
            || creature.Template.Civilian || (creature.Template.ExtraFlags & Creature.ExtraFlagNoAggro) != 0
            || (creature.UnitFlags & (UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer | UnitFlags.Pacified | UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) != 0)
        {
            return false;
        }

        float radii = creature.BoundingRadius + player.BoundingRadius;
        float dz = MathF.Abs(creature.Z - player.Z) - radii;
        if (dz > MaxAggroZDistance)
        {
            return false;
        }

        float range = GetAttackDistance(creature, player) + radii;
        if (DistanceSquared(creature, player) > range * range)
        {
            return false;
        }

        return Map.Combat.Hooks.CanAttack(creature, player)
            && _ai.Hostility.IsHostile(creature, player)
            && InLineOfSight(creature, player);
    }

    // --- combat --------------------------------------------------------------------------------

    /// <summary>
    /// vmangos CreatureAI::AttackStart: melee (unless the AI turned it off), zero threat so the
    /// target is on the threat list, both sides in combat, then on the first entry the combat
    /// start point, the AI's aggro hook and the assistance call; chase when combat movement is on.
    /// </summary>
    public bool AttackStart(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        if (!_creatures.ContainsKey(creature.Guid) || !creature.IsAlive || creature.IsEvading
            || !target.IsAlive || !ReferenceEquals(target.Map, Map) || !Map.Combat.Hooks.CanAttack(creature, target))
        {
            return false;
        }

        bool melee = creature.AI?.MeleeEnabled ?? true;
        if (!ReferenceEquals(creature.Combat.Victim, target) && !Map.Combat.Attack(creature, target, melee))
        {
            return false;
        }

        creature.Combat.Threat.AddThreat(target, 0f);
        Map.Combat.SetInCombatState(creature, 0);
        Map.Combat.SetInCombatState(target, 0);
        if (!creature.HasAggroed)
        {
            creature.HasAggroed = true;
            creature.CombatStart = new CreatureHome(creature.X, creature.Y, creature.Z, creature.Orientation);
            creature.AI?.OnAggro(target);
            if (!creature.IsAlive || creature.IsEvading)
            {
                return true; // the aggro script killed or reset it
            }

            CallAssistance(creature, target);
        }

        ApplyCombatMovement(creature);
        return true;
    }

    /// <summary>
    /// vmangos Creature::SelectHostileTarget: the victim from the threat list (110 % in melee /
    /// 130 % at range to take aggro), skipping dead, unattackable and leashed targets. Nobody
    /// left means evade. Returns whether the creature still has a victim.
    /// </summary>
    public bool SelectHostileTarget(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || creature.IsEvading || !_creatures.ContainsKey(creature.Guid))
        {
            return false;
        }

        UnitCombat combat = creature.Combat;
        Unit? victim = combat.HasThreatList
            ? combat.Threat.SelectVictim(u => IsValidHostileTarget(creature, u), u => MapCombat.CanReachWithMeleeAutoAttack(creature, u))
            : null;
        if (victim is null)
        {
            if (combat.IsInCombat || combat.Victim is not null || combat.HasThreatList)
            {
                EnterEvadeMode(creature);
            }

            return false;
        }

        if (!ReferenceEquals(combat.Victim, victim))
        {
            Map.Combat.Attack(creature, victim, creature.AI?.MeleeEnabled ?? true);
        }

        ApplyCombatMovement(creature);
        return true;
    }

    /// <summary>
    /// vmangos Creature::IsOutOfThreatArea: a target farther than max(ThreatRadius, aggro radius)
    /// from where the fight began is out of reach (no leash on instanceable maps).
    /// </summary>
    public bool IsOutOfThreatArea(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        if (Map.Combat.Hooks.IsInstanceable(Map.MapId))
        {
            return false;
        }

        CreatureHome anchor = creature.CombatStart ?? creature.Home;
        float radius = Math.Max(_options.ThreatRadius, GetAttackDistance(creature, target));
        var delta = new Vector3(target.X - anchor.X, target.Y - anchor.Y, target.Z - anchor.Z);
        return delta.LengthSquared() > radius * radius;
    }

    /// <summary>
    /// vmangos CreatureAI::EnterEvadeMode: stop the cast, every fight and the threat list, full
    /// health (and mana), the AI's evade hook, and run home: to the combat start point for
    /// waypoint movers (they resume the path there), else to the spawn point. The creature
    /// refuses attacks until it arrives (<see cref="Creature.IsInEvadeMode"/>).
    /// </summary>
    public void EnterEvadeMode(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || creature.IsEvading || !_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        CreatureHome home = creature.Motion.DefaultType == MovementGeneratorType.Waypoint && creature.CombatStart is { } start
            ? start
            : creature.Home;

        _ai.Spells?.Interrupt(creature);
        Map.Combat.CombatStop(creature);
        if (creature.Combat.HasThreatList)
        {
            creature.Combat.Threat.Clear();
        }

        ResetAiState(creature);
        creature.IsEvading = true;
        creature.Health = creature.MaxHealth;
        if (creature.PowerType == PowerType.Mana)
        {
            MapCombat.SetPower(creature, PowerType.Mana, MapCombat.GetMaxPower(creature, PowerType.Mana));
        }

        creature.AI?.OnEvade();
        if (!creature.IsAlive || !creature.IsEvading)
        {
            return;
        }

        creature.Motion.MoveTargetedHome(home);
    }

    /// <summary>
    /// Turn chasing on or off for the current victim (cmangos EventAI COMBAT_MOVEMENT): with it
    /// on the creature chases; off it stops where it is. Flight, point and home moves keep running.
    /// </summary>
    public void ApplyCombatMovement(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || creature.IsEvading)
        {
            return;
        }

        MovementGeneratorType current = creature.Motion.CurrentType;
        if (current is MovementGeneratorType.Fleeing or MovementGeneratorType.Point or MovementGeneratorType.Home)
        {
            return;
        }

        if (creature.Combat.Victim is { } victim && (creature.AI?.CombatMovement ?? true))
        {
            creature.Motion.MoveChase(victim);
        }
        else if (current == MovementGeneratorType.Chase)
        {
            creature.Motion.Remove(MovementGeneratorType.Chase);
            StopMoving(creature);
        }
    }

    /// <summary>Turn auto attack on or off against <paramref name="victim"/> (cmangos EventAI AUTO_ATTACK).</summary>
    public void SetMelee(Creature creature, Unit victim, bool melee)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(victim);
        if (melee)
        {
            Map.Combat.Attack(creature, victim, melee: true);
        }
        else if (creature.Combat.IsMeleeAttacking)
        {
            creature.Combat.IsMeleeAttacking = false;
            CombatPackets.SendToSet(creature, WorldOpcode.SmsgAttackstop, CombatPackets.AttackStop(creature.Guid, victim.Guid, false));
        }
    }

    // --- assistance --------------------------------------------------------------------------

    /// <summary>
    /// vmangos Creature::CallAssistance: once per fight, every idle same-faction creature within
    /// <see cref="CreatureOptions.AssistanceRadius"/> that can see the caller joins after
    /// <see cref="CreatureOptions.AssistanceDelayMs"/> (helpers do not call further help).
    /// </summary>
    internal void CallAssistance(Creature creature, Unit enemy)
    {
        if (creature.CalledAssistance || _options.AssistanceRadius <= 0 || creature.Template.Civilian)
        {
            return;
        }

        creature.CalledAssistance = true;
        foreach (Creature helper in _creatures.Values)
        {
            if (CanAssist(helper, creature, enemy, _options.AssistanceRadius))
            {
                _pendingAssists.Add(new PendingAssist(helper, enemy, creature, _clockMs + _options.AssistanceDelayMs));
            }
        }
    }

    /// <summary>cmangos EventAI CALL_FOR_HELP: idle same-faction creatures within <paramref name="radius"/> join at once.</summary>
    public int CallForHelp(Creature creature, float radius)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Combat.Victim is not { } enemy || radius <= 0)
        {
            return 0;
        }

        int count = 0;
        foreach (Creature helper in _creatures.Values.ToArray())
        {
            if (CanAssist(helper, creature, enemy, radius))
            {
                helper.CalledAssistance = true;
                if (AttackStart(helper, enemy))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// vmangos Creature::CanAssistTo plus the assistance search filters: another living, idle,
    /// non-civilian creature with an AI that fights, attackable flags, within
    /// <paramref name="radius"/> of the caller (3D, both radii), allowed by the hostility seam
    /// (same faction by default), able to attack the enemy and in line of sight of the caller.
    /// </summary>
    public bool CanAssist(Creature helper, Creature caller, Unit enemy, float radius)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(enemy);
        float range = radius + helper.BoundingRadius + caller.BoundingRadius;
        return CanAssistNow(helper, caller, enemy)
            && DistanceSquared(helper, caller) <= range * range
            && InLineOfSight(helper, caller);
    }

    // CanAssistTo is rechecked when the delayed call executes. Radius and sight belong to
    // the initial neighbour search: the caller may have chased away during the delay.
    private bool CanAssistNow(Creature helper, Creature caller, Unit enemy)
    {
        if (ReferenceEquals(helper, caller) || !helper.IsAlive || helper.DeathState != CreatureDeathState.Alive
            || helper.IsEvading || helper.Combat.Victim is not null || helper.Combat.IsInCombat
            || helper.AI is null or NullCreatureAI || helper.Template.Civilian
            || (helper.UnitFlags & NotAssistable) != 0
            || (enemy is Player && (helper.UnitFlags & UnitFlags.ImmuneToPlayer) != 0))
        {
            return false;
        }

        return _ai.Hostility.CanAssist(helper, caller)
            && Map.Combat.Hooks.CanAttack(helper, enemy);
    }

    /// <summary>
    /// cmangos Creature::DoFleeToGetAssistance: run to the nearest creature that can help (within
    /// <see cref="CreatureOptions.FleeAssistanceRadius"/>) and call for help on arrival; with
    /// nobody to find, flee from the victim for <see cref="CreatureOptions.FleeDelayMs"/>.
    /// </summary>
    public void FleeForAssistance(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || creature.IsEvading || creature.Combat.Victim is not { } enemy)
        {
            return;
        }

        Creature? nearest = null;
        float best = float.MaxValue;
        foreach (Creature helper in _creatures.Values)
        {
            if (CanAssist(helper, creature, enemy, _options.FleeAssistanceRadius))
            {
                float d = DistanceSquared(helper, creature);
                if (d < best)
                {
                    best = d;
                    nearest = helper;
                }
            }
        }

        if (nearest is not null)
        {
            creature.Motion.MovePoint(FleeForAssistancePointId, nearest.X, nearest.Y, nearest.Z, run: true);
        }
        else
        {
            creature.Motion.MoveFleeing(enemy, _options.FleeDelayMs);
        }
    }

    // --- spells, texts, summons ----------------------------------------------------------------

    public CreatureCastResult CastSpell(Creature creature, uint spellId, Unit? target, bool triggered)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (_ai.Spells is not { } spells)
        {
            return CreatureCastResult.NoSpellSystem;
        }

        return creature.IsAlive ? spells.Cast(creature, spellId, target, triggered) : CreatureCastResult.Failed;
    }

    public bool HasAura(Unit unit, uint spellId) => _ai.Spells?.HasAura(unit, spellId) ?? false;

    public void InterruptCast(Creature creature) => _ai.Spells?.Interrupt(creature);

    /// <summary>
    /// Speak a creature_ai_texts entry (cmangos DoScriptText): type 0 say (25 yd), 1 yell
    /// (300 yd), 2 text emote (25 yd), 3 boss emote and 6 zone yell (whole map), 4 and 5 whisper
    /// to the target player. <c>$N</c> becomes the target's name; a non-zero emote plays too.
    /// </summary>
    public void Say(Creature creature, CreatureAiText text, Unit? target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(text);
        string content = text.Content;
        if (target is Player named)
        {
            content = content.Replace("$N", named.Name, StringComparison.Ordinal).Replace("$n", named.Name, StringComparison.Ordinal);
        }

        ObjectGuid targetGuid = target?.Guid ?? default;
        string name = creature.Template.Name;
        switch (text.Type)
        {
            case 1:
                Broadcast(ChatType.MonsterYell, CreatureChatPackets.YellRange);
                break;
            case 2:
                Broadcast(ChatType.MonsterEmote, CreatureChatPackets.TextEmoteRange);
                break;
            case 3:
                Broadcast(ChatType.MonsterEmote, 0);
                break;
            case 4:
            case 5:
                if (target is Player player && ReferenceEquals(player.Map, Map))
                {
                    player.Session.Send(WorldOpcode.SmsgMessagechat,
                        CreatureChatPackets.BuildMonsterMessage(ChatType.MonsterWhisper, text.Language, creature.Guid, name, targetGuid, content));
                }

                break;
            case 6:
                Broadcast(ChatType.MonsterYell, 0);
                break;
            default:
                Broadcast(ChatType.MonsterSay, CreatureChatPackets.SayRange);
                break;
        }

        if (text.Emote != 0)
        {
            Map.BroadcastToObservers(creature, WorldOpcode.SmsgEmote, CreatureChatPackets.BuildEmote(text.Emote, creature.Guid));
        }

        void Broadcast(ChatType type, float range)
            => Map.BroadcastInRange(creature, range, WorldOpcode.SmsgMessagechat,
                CreatureChatPackets.BuildMonsterMessage(type, text.Language, creature.Guid, name, targetGuid, content), includeSelf: false);
    }

    /// <summary>
    /// cmangos EventAI SUMMON: a temporary creature at the summoner's position that attacks
    /// <paramref name="target"/> and despawns after <paramref name="despawnMs"/> (0 = stays until
    /// it dies or its grid unloads). A missing template is reported once and summons nothing.
    /// </summary>
    public Creature? Summon(Creature summoner, uint entry, Unit? target, uint despawnMs)
    {
        ArgumentNullException.ThrowIfNull(summoner);
        if (_content.FindTemplate(entry) is not { } template)
        {
            if (_reportedAi.Add($"summon:{entry}"))
            {
                _logger.LogWarning("{Creature} EventAI summons missing creature_template {Entry}; skipped", summoner.Guid, entry);
            }

            return null;
        }

        Creature summoned = SpawnTemporary(template, summoner.X, summoner.Y, summoner.Z, summoner.Orientation);
        if (despawnMs > 0)
        {
            _summons.Add((summoned, _clockMs + despawnMs));
        }

        if (target is not null && target.IsAlive)
        {
            if (summoned.AI is { } ai)
            {
                ai.AttackStart(target);
            }
            else
            {
                AttackStart(summoned, target);
            }
        }

        return summoned;
    }

    /// <summary>A uniform random integer in [min, max] (EventAI chances, timers and choices).</summary>
    public int RandomInt(int min, int max) => min >= max ? min : (int)_random.NextInt64(min, (long)max + 1);

    // --- host plumbing -----------------------------------------------------------------------

    private void SubscribeAi()
    {
        if (_ai.Spells is { } spells)
        {
            spells.SpellHit += OnSpellHit;
        }
        TrySubscribeCombat();
    }

    private void TrySubscribeCombat()
    {
        if (_combatSubscribed || Map.FindUpdater<MapCombat>() is not { } combat)
        {
            return;
        }

        combat.UnitKilled += OnUnitKilled;
        _combatSubscribed = true;
    }

    private void CreateAi(Creature creature)
    {
        CreatureAI ai = _ai.Factory.Create(creature, _content, out bool unknown);
        string aiName = creature.Template.AIName;
        if (unknown && _reportedAi.Add($"name:{aiName}"))
        {
            _logger.LogWarning("creature_template {Entry} uses unknown AIName '{AIName}'; using the default AI", creature.Template.Entry, aiName);
        }

        if (ai is CreatureEventAI eventAi && eventAi.Unsupported.Count > 0 && _reportedAi.Add($"eventai:{creature.Template.Entry}"))
        {
            _logger.LogWarning("creature_ai_scripts for creature {Entry} use unsupported {Unsupported}; those parts are skipped",
                creature.Template.Entry, string.Join(", ", eventAi.Unsupported));
        }

        creature.AI = ai;
    }

    private void ResetAiState(Creature creature)
    {
        creature.IsEvading = false;
        creature.HasAggroed = false;
        creature.CalledAssistance = false;
        creature.CombatStart = null;
        _pendingAssists.RemoveAll(p => ReferenceEquals(p.Helper, creature) || ReferenceEquals(p.Caller, creature));
    }

    private void ForgetAi(Creature creature)
    {
        ResetAiState(creature);
        _summons.RemoveAll(s => ReferenceEquals(s.Creature, creature));
        _ai.Spells?.OnCreatureRemoved(creature);
        creature.AI = null;
    }

    private void OnAiDeath(Creature creature, Unit? killer)
    {
        creature.Motion.Reset();
        _ai.Spells?.Interrupt(creature);
        ResetAiState(creature);
        creature.AI?.OnDeath(killer);
    }

    /// <summary>The AI half of a creature's tick: aggro scan over the map's players, then the script.</summary>
    private void UpdateAi(Creature creature, uint diffMs)
    {
        if (creature.AI is not { } ai)
        {
            return;
        }

        if (ai.AggroesOnSight && !creature.IsEvading && creature.Combat.Victim is null && _options.AggroRate > 0)
        {
            foreach (Player player in Map.Players)
            {
                if (creature.Combat.Victim is not null || !creature.IsAlive)
                {
                    break;
                }

                ai.MoveInLineOfSight(player);
            }
        }

        if (creature.IsAlive && _creatures.ContainsKey(creature.Guid))
        {
            ai.OnUpdate(diffMs);
        }
    }

    /// <summary>Due assistance calls and summon despawns (after every creature's tick).</summary>
    private void UpdatePendingAi()
    {
        TrySubscribeCombat();
        if (_pendingAssists.Count > 0)
        {
            PendingAssist[] due = [.. _pendingAssists.Where(p => p.DueMs <= _clockMs)];
            _pendingAssists.RemoveAll(p => p.DueMs <= _clockMs);
            foreach (PendingAssist assist in due)
            {
                Creature helper = assist.Helper;
                Creature caller = assist.Caller;
                if (!_creatures.TryGetValue(helper.Guid, out Creature? currentHelper) || !ReferenceEquals(currentHelper, helper)
                    || !_creatures.TryGetValue(caller.Guid, out Creature? currentCaller) || !ReferenceEquals(currentCaller, caller)
                    || !ReferenceEquals(helper.Map, Map) || !ReferenceEquals(caller.Map, Map)
                    || !caller.IsAlive || caller.IsEvading || !ReferenceEquals(caller.Combat.Victim, assist.Enemy)
                    || !assist.Enemy.IsAlive || !ReferenceEquals(assist.Enemy.Map, Map)
                    || !CanAssistNow(helper, caller, assist.Enemy))
                {
                    continue;
                }

                helper.CalledAssistance = true;
                if (helper.AI is { } ai)
                {
                    ai.AttackStart(assist.Enemy);
                }
            }
        }

        if (_summons.Count > 0)
        {
            (Creature Creature, long DespawnAtMs)[] expired = [.. _summons.Where(s => s.DespawnAtMs <= _clockMs)];
            _summons.RemoveAll(s => s.DespawnAtMs <= _clockMs);
            foreach ((Creature summoned, _) in expired)
            {
                Despawn(summoned);
            }
        }
    }

    private void OnMovementFinished(Creature creature, MovementGeneratorType type, uint pointId)
    {
        switch (type)
        {
            case MovementGeneratorType.Home:
                creature.IsEvading = false;
                creature.AI?.OnMovementInform(type, pointId);
                creature.AI?.OnReachedHome();
                break;

            case MovementGeneratorType.Point:
                if (pointId == FleeForAssistancePointId && creature.IsAlive)
                {
                    CallForHelp(creature, _options.AssistanceRadius);
                }

                creature.AI?.OnMovementInform(type, pointId);
                break;
        }
    }

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (killer is Creature creature && ReferenceEquals(creature.System, this) && creature.IsAlive && !ReferenceEquals(creature, victim))
        {
            creature.AI?.OnKilledUnit(victim);
        }
    }

    private void OnSpellHit(Unit caster, Unit target, SpellInfo spell)
    {
        if (target is Creature creature && ReferenceEquals(creature.System, this) && creature.IsAlive)
        {
            creature.AI?.OnSpellHit(caster, spell);
        }
    }

    private bool IsValidHostileTarget(Creature creature, Unit target)
        => target.IsAlive && ReferenceEquals(target.Map, Map)
            && Map.Combat.Hooks.CanAttack(creature, target)
            && !IsOutOfThreatArea(creature, target);

    /// <summary>vmangos IsWithinLOSInMap through the map's <see cref="ILineOfSight"/> (<c>map.Collision</c>, eye height; open without vmaps).</summary>
    private bool InLineOfSight(WorldObject from, WorldObject to) => Map.Collision.IsWithinLineOfSight(from, to);

    private static float DistanceSquared(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private readonly record struct PendingAssist(Creature Helper, Unit Enemy, Creature Caller, long DueMs);
}
