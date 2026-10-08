using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
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
    private readonly CreatureAiServices _ai;

    private readonly HashSet<string> _reportedAi = [];
    private bool _combatSubscribed;
    private AiRelocationNotifier _relocation = null!;

    /// <summary>The services this map's creature AI uses (hostility, spells, AI factory); paths and sight come from <c>Map.Collision</c>.</summary>
    public CreatureAiServices AiServices => _ai;

    /// <summary>The creature options this map's system runs with (including <see cref="CreatureOptions.EventAi"/>).</summary>
    public CreatureOptions Options => _options;

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

    /// <summary>vmangos Unit::RemoveAurasDueToSpell through the creature spell seam (nothing without a spell system).</summary>
    public void RemoveAuras(Unit unit, uint spellId) => _ai.Spells?.RemoveAuras(unit, spellId);

    /// <summary>A uniform random integer in [min, max] (EventAI chances, timers and choices).</summary>
    public int RandomInt(int min, int max) => min >= max ? min : (int)_random.NextInt64(min, (long)max + 1);

    // --- host plumbing -----------------------------------------------------------------------

    private void SubscribeAi()
    {
        if (_ai.Spells is { } spells)
        {
            spells.SpellHit += OnSpellHit;
        }

        _relocation = new AiRelocationNotifier(this, _options);
        Map.ObjectRelocated += OnObjectRelocated;
        TrySubscribeCombat();
    }

    private void OnObjectRelocated(WorldObject obj)
    {
        if (_options.AggroScanMode == AggroScanMode.Relocation)
        {
            _relocation.OnRelocated(obj, _clockMs);
        }
    }

    /// <summary>
    /// vmangos CallAIMoveLOS (Maps/GridNotifiersImpl.h:57-69): a creature that is alive, not evading, in control and has an AI
    /// gets <c>MoveInLineOfSight</c> for the unit that moved near it when it can see that unit; a stealthed player it cannot see, but
    /// whose stealth it nearly breaks (inside the alert band), gets <c>OnMoveInStealth</c> instead
    /// (<see cref="StealthServices.CanCreatureSee"/>, vmangos IsVisibleForOrDetect with the creature as detector).
    /// </summary>
    internal void CallAiMoveInLineOfSight(Creature creature, Unit moving)
    {
        if (creature.IsAlive && !creature.IsEvading && (creature.UnitFlags & LostControl) == 0
            && _creatures.ContainsKey(creature.Guid) && creature.AI is { } ai)
        {
            bool alert = false;
            bool visible = moving is not Player player || StealthServices.Find(Map) is not { } stealth || stealth.CanCreatureSee(creature, player, out alert);
            if (visible)
            {
                ai.MoveInLineOfSight(moving);
            }
            else if (alert)
            {
                ai.OnMoveInStealth(moving);
            }
        }
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

    /// <summary>
    /// vmangos Creature::AIM_Initialize after a charm or possession ended (SpellAuras.cpp:3097-3101, 3412-3418): the motion master starts
    /// over from the default generator and the template's AI is built again. False when the creature is not this system's.
    /// </summary>
    internal bool ReinitializeAi(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!_creatures.ContainsKey(creature.Guid))
        {
            return false;
        }

        ResetAiState(creature);
        creature.Motion.Initialize(creature.Motion.Default, this, start: creature.IsAlive);
        CreateAi(creature);
        return true;
    }

    private void CreateAi(Creature creature)
    {
        if (CreateEntryAi(creature) is { } scripted)
        {
            creature.AI = scripted; // a script AI of this map for the entry (CreatureMapSystem.ScriptedAi.cs)
            ResetGuardCall(creature);
            return;
        }

        CreatureAI ai = _ai.Factory.Create(creature, _content, out bool unknown, _options.ImplicitEventAi);
        string aiName = creature.Template.AIName;
        if (unknown && _reportedAi.Add($"name:{aiName}"))
        {
            _logger.LogWarning("creature_template {Entry} uses unknown AIName '{AIName}'; using the default AI", creature.Template.Entry, aiName);
        }

        if (ai is CreatureEventAI eventAi && _options.EventAi.ReportUnsupported && eventAi.Unsupported.Count > 0 && _reportedAi.Add($"eventai:{creature.Template.Entry}"))
        {
            _logger.LogWarning("creature_ai_scripts for creature {Entry} use unsupported {Unsupported}; those parts are skipped",
                creature.Template.Entry, string.Join(", ", eventAi.Unsupported));
        }

        creature.AI = ai;
        ResetGuardCall(creature);
    }

    /// <summary>The AI half of a creature's tick: aggro scan over the map's players, then the script.</summary>
    private void UpdateAi(Creature creature, uint diffMs)
    {
        if (creature.AI is not { } ai)
        {
            return;
        }

        if (_options.AggroScanMode == AggroScanMode.Poll && ai.AggroesOnSight && !creature.IsEvading && creature.Combat.Victim is null && _options.AggroRate > 0)
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
        if (_options.AggroScanMode == AggroScanMode.Relocation)
        {
            _relocation.Update(_clockMs);
        }

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
                    // The stored enemy, not the caller's current victim (vmangos AssistDelayEvent::Execute, Creature.cpp:150-173,
                    // attacks m_victimGuid): a caller that switched targets during the delay still brings its helpers.
                    || !caller.IsAlive || caller.IsEvading
                    || !assist.Enemy.IsAlive || !ReferenceEquals(assist.Enemy.Map, Map)
                    || !CanAssistNow(helper, caller, assist.Enemy))
                {
                    continue;
                }

                helper.CalledAssistance = true;
                if (helper.AI is { } ai)
                {
                    ai.AttackStart(assist.Enemy);

                    // Creatures that joined through an assistance call share the caller's leash timer, so attacking one keeps the
                    // rest from leashing (vmangos AssistDelayEvent::Execute, Objects/Creature.cpp:160-170).
                    if (helper.Combat.Victim is not null)
                    {
                        helper.LeashClock = caller.LeashClock ??= new LeashExtensionClock { Seconds = _clockMs / 1000 };
                    }

                }
            }
        }

        UpdateTimedSummons();
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

            case MovementGeneratorType.Waypoint:
                creature.AI?.OnMovementInform(type, pointId); // vmangos WaypointMovementGenerator::OnArrived (:158-160): the node id
                break;

            case MovementGeneratorType.Follow:
                creature.AI?.OnMovementInform(type, pointId); // FollowMovementGenerator::MovementInform: the followed unit's low GUID
                break;

            case MovementGeneratorType.Point:
                if (pointId == FleeForAssistancePointId && creature.IsAlive)
                {
                    CallForHelp(creature, _options.AssistanceRadius);
                }
                else if (pointId == RelayMovePointId)
                {
                    OnRelayMoveArrived(creature);
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

        NotifySpellHitTarget(caster, target, spell);
    }

    /// <summary>vmangos IsWithinLOSInMap through the map's <see cref="ILineOfSight"/> (<c>map.Collision</c>, eye height; open without vmaps).</summary>
    private bool InLineOfSight(WorldObject from, WorldObject to) => Map.Collision.IsWithinLineOfSight(from, to);

    private static float DistanceSquared(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}
