using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Evade, the per-fight AI state and its reset, and the death hook (vmangos CreatureAI::EnterEvadeMode).</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// vmangos CreatureAI::EnterEvadeMode (AI/CreatureAI.cpp:323-346): stop the cast, drop the auras an evade removes
    /// (<see cref="ICreatureAuraReset"/>, not for a charmed creature), stop every fight and clear the threat list, the AI's evade hook,
    /// and run home: to the combat start point for waypoint movers (they resume the path there), else to the spawn point. The creature
    /// refuses attacks until it arrives (<see cref="Creature.IsInEvadeMode"/>); a charmed creature does not run home and is not left in evade
    /// mode. Health and mana are not touched: the creature regenerates a
    /// third of its maximum per 5 s tick once out of combat (<c>Creatures:Movement:EvadeRestoresFullHealth</c> restores the old instant snap).
    /// The loot tap is cleared (the tapper, the group of the tap and the tapped dynamic flags; vmangos CreatureAI::EnterEvadeMode →
    /// SetLootRecipient(nullptr)). Not delivered: combo points other players hold on the creature are not cleared (no evade event reaches
    /// the combo service), a creature's pets and totems are not sent home (creatures have no controlled-unit links).
    /// <see cref="Evaded"/> is raised once per evade.
    /// </summary>
    public void EnterEvadeMode(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || creature.IsEvading || !_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        // A script's EnterEvadeMode override (CreatureAI.OnEnterEvadeMode) replaces the whole evade; a nested evade from inside it is the
        // engine's (the source's Base::EnterEvadeMode()).
        if (creature.AI is { } scripted && _customEvades.Add(creature))
        {
            try
            {
                if (scripted.OnEnterEvadeMode())
                {
                    return;
                }
            }
            finally
            {
                _customEvades.Remove(creature);
            }
        }

        // The default generator's reset position (the last reached waypoint, where a wanderer stands inside its disc), else the spawn
        // point: vmangos HomeMovementGenerator::_setTargetLocation, HomeMovementGenerator.cpp:52-56.
        CreatureHome home = creature.Motion.Default.GetResetPosition(creature) ?? creature.Home;

        bool charmed = !creature.CharmerGuid.IsEmpty;
        _ai.Spells?.Interrupt(creature);
        if (!charmed && _options.EvadeResetsAuras && _ai.Spells is ICreatureAuraReset reset)
        {
            reset.ResetAuras(creature, (creature.Template.Behaviour & CreatureBehaviourFlags.KeepPositiveAurasOnEvade) != 0);
        }

        Map.Combat.CombatStop(creature);
        if (creature.Combat.HasThreatList)
        {
            creature.Combat.Threat.Clear();
        }

        ResetAiState(creature);
        ClearLootTap(creature);
        // vmangos Creature::IsInEvadeMode (Creature.cpp:3239-3260) is the home generator on top: a charmed creature is sent nowhere, so it
        // is not in evade mode (and nothing but reaching home would clear the flag).
        creature.IsEvading = !charmed;
        if (_options.Movement.EvadeRestoresFullHealth)
        {
            // Not retail: vmangos' evade leaves health and mana alone and the regeneration brings them back (Creature.cpp:1087-1160).
            creature.Health = creature.MaxHealth;
            if (creature.PowerType == PowerType.Mana)
            {
                MapCombat.SetPower(creature, PowerType.Mana, MapCombat.GetMaxPower(creature, PowerType.Mana));
            }
        }

        creature.AI?.OnEvade();
        Map.FindUpdater<Instances.Scripts.InstanceData>()?.OnCreatureEvade(creature);
        if (!creature.IsAlive || (!charmed && !creature.IsEvading))
        {
            return;
        }

        if (!charmed)
        {
            creature.Motion.MoveTargetedHome(home);
        }

        Evaded?.Invoke(creature);
        OnGroupMemberEvaded(creature); // cmangos Unit::TriggerEvadeEvents → CREATURE_GROUP_EVENT_EVADE
    }

    /// <summary>Raised after a creature entered evade mode (once per evade; not for a dead creature).</summary>
    public event Action<Creature>? Evaded;

    private readonly HashSet<Creature> _customEvades = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Leave combat where the creature stands, for a script's own evade (<see cref="AI.CreatureAI.OnEnterEvadeMode"/>): the source's
    /// <c>SetLootRecipient(nullptr); CombatStop(false); MovementExpired(true)</c> (mangos-classic wailing_cavernsScripts.cpp:174-191). The loot
    /// tap goes, every fight stops and the threat list is cleared, the per-fight AI state is reset and the chase is dropped. Unlike
    /// <see cref="EnterEvadeMode"/> the cast or channel in progress and the auras stay, the creature is not sent home and is not left in
    /// evade mode, and neither the AI's <c>OnEvade</c> nor the instance script's evade hook runs.
    /// </summary>
    public void StopCombatInPlace(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        ClearLootTap(creature);
        Map.Combat.CombatStop(creature);
        if (creature.Combat.HasThreatList)
        {
            creature.Combat.Threat.Clear();
        }

        ResetAiState(creature);
        if (creature.IsAlive)
        {
            creature.Motion.Remove(MovementGeneratorType.Chase);
        }
    }

    private static void ClearLootTap(Creature creature)
    {
        creature.LootTapPlayerGuid = default;
        creature.LootTapGroup = null;
        creature.SetUInt32(UpdateFields.UnitDynamicFlags,
            creature.GetUInt32(UpdateFields.UnitDynamicFlags) & ~(Loot.LootService.UnitDynFlagTapped | Loot.LootService.UnitDynFlagTappedByPlayer));
    }

    private void ResetAiState(Creature creature)
    {
        creature.IsEvading = false;
        creature.IsEvadingUnreachable = false; // vmangos Unit::CombatStop clears m_targetNotReachableTimer (Unit.cpp:4645)
        creature.TargetNotReachableMs = 0;
        creature.HasAggroed = false;
        creature.InNoMeleePanic = false;
        creature.CalledAssistance = false;
        creature.CombatStart = null;
        creature.LeashClock = null;
        _pendingAssists.RemoveAll(p => ReferenceEquals(p.Helper, creature) || ReferenceEquals(p.Caller, creature));
    }

    private void ForgetAi(Creature creature)
    {
        ResetAiState(creature);
        _summons.RemoveAll(s => ReferenceEquals(s.Creature, creature));
        _arrivalRelays.Remove(creature);
        _scriptDespawns.RemoveAll(s => ReferenceEquals(s.Creature, creature));
        _ai.Spells?.OnCreatureRemoved(creature);
        creature.AI = null;
    }

    private void OnAiDeath(Creature creature, Unit? killer)
    {
        creature.Motion.Reset();
        _ai.Spells?.Interrupt(creature);
        ResetAiState(creature);
        creature.AI?.OnDeath(killer);
        if (Map.FindUpdater<Instances.Scripts.InstanceData>() is { } instanceData)
        {
            instanceData.OnCreatureDeath(creature);
            instanceData.NotifyCreatureGone(creature); // cmangos SetDeathState(JUST_DIED) -> ClearCreatureGroup
        }
    }
}
