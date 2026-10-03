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
    private readonly CreatureAiServices _ai;

    private readonly HashSet<string> _reportedAi = [];
    private bool _combatSubscribed;

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

        if (ai is CreatureEventAI eventAi && _options.EventAi.ReportUnsupported && eventAi.Unsupported.Count > 0 && _reportedAi.Add($"eventai:{creature.Template.Entry}"))
        {
            _logger.LogWarning("creature_ai_scripts for creature {Entry} use unsupported {Unsupported}; those parts are skipped",
                creature.Template.Entry, string.Join(", ", eventAi.Unsupported));
        }

        creature.AI = ai;
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
