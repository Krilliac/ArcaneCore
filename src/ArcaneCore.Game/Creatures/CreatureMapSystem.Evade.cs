using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Evade, the per-fight AI state and its reset, and the death hook (vmangos CreatureAI::EnterEvadeMode).</summary>
public sealed partial class CreatureMapSystem
{
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

        CreatureHome home = creature.Motion.Default.GetResetPosition(creature)
            ?? (creature.Motion.DefaultType == MovementGeneratorType.Waypoint && creature.CombatStart is { } start
                ? start
                : creature.Home);

        _ai.Spells?.Interrupt(creature);
        Map.Combat.CombatStop(creature);
        if (creature.Combat.HasThreatList)
        {
            creature.Combat.Threat.Clear();
        }

        ResetAiState(creature);
        creature.LootTapPlayerGuid = default;
        creature.LootTapGroup = null;
        creature.SetUInt32(UpdateFields.UnitDynamicFlags,
            creature.GetUInt32(UpdateFields.UnitDynamicFlags) & ~(Loot.LootService.UnitDynFlagTapped | Loot.LootService.UnitDynFlagTappedByPlayer));
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

    private void ResetAiState(Creature creature)
    {
        creature.IsEvading = false;
        creature.HasAggroed = false;
        creature.CalledAssistance = false;
        creature.CombatStart = null;
        creature.LeashClock = null;
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
}
