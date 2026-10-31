using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// Gothik the Harvester's anchor chain (vmangos naxxramas/boss_gothik.cpp boss_gothikAI::SummonedCreatureJustDied and
/// EffectDummyCreature_spell_anchor; instance_naxxramas.cpp SetGothTriggers, GetClosestAnchorForGoth, GetGothSummonPointCreatures and
/// IsInRightSideGothArea). Reimplemented, no GPL source copied.
/// <para>
/// When a living-side add (trainee 16124, death knight 16125, rider 16126) dies, a 15-second sub-boss trigger (16137) is placed on its corpse
/// and casts "To Anchor 1" at the nearest high anchor of the living side; that anchor casts "To Anchor 2" at the nearest high anchor of the
/// spectral side, which casts "Anchor to Skulls" at a random low summon point of the spectral side, where Gothik raises the spectral
/// counterpart (<see cref="GothikAnchorChainScript"/>). vmangos uses a temporary trigger because the corpse cannot keep a delayed spell alive.
/// </para>
/// <para>
/// The sides and heights follow vmangos: a trigger is a high anchor when it stands no more than 5 yards below Gothik, and it is on the living
/// ("right") side when its Y is at most the combat gate's (181170). The temporary corpse triggers are never anchors (vmangos builds its
/// trigger map from the database triggers only, before the fight). No Gothik, no combat gate or no matching trigger stops the chain, as in vmangos.
/// </para>
/// </summary>
public sealed partial class NaxxramasInstance
{
    /// <summary>naxxramas.h NPC_SUB_BOSS_TRIGGER.</summary>
    internal const uint GothikTrigger = 16137;

    /// <summary>naxxramas.h NPC_GOTHIK.</summary>
    internal const uint GothikEntry = 16060;

    /// <summary>naxxramas.h GO_MILI_GOTH_COMBAT_GATE.</summary>
    internal const uint GothikCombatGate = 181170;

    /// <summary>vmangos boss_gothik.cpp: the corpse trigger is a TEMPSUMMON_TIMED_DESPAWN of 15 seconds.</summary>
    internal const uint GothikCorpseTriggerMs = 15000;

    private readonly HashSet<ObjectGuid> _gothikCorpseTriggers = [];

    /// <summary>How many corpse triggers are still tracked (they are pruned when the creature is gone and at the encounter's end).</summary>
    internal int GothikCorpseTriggerCount => _gothikCorpseTriggers.Count;

    /// <summary>
    /// Drops the tracked corpse triggers whose creature is no longer on the map (the 15-second timed despawn, a forced despawn), so the set
    /// does not grow for the life of the instance. With <paramref name="all"/> the set is emptied (the encounter ended: the triggers are
    /// despawned first).
    /// </summary>
    private void PruneGothikCorpseTriggers(CreatureMapSystem system, bool all)
    {
        if (_gothikCorpseTriggers.Count == 0) return;
        if (all)
        {
            foreach (Creature trigger in system.Creatures.Where(c => c.Entry == GothikTrigger && _gothikCorpseTriggers.Contains(c.Guid)).ToArray())
                system.ForcedDespawn(trigger, 0);
            _gothikCorpseTriggers.Clear();
            return;
        }

        _gothikCorpseTriggers.RemoveWhere(guid => system.Creatures.All(c => c.Guid != guid || c.Entry != GothikTrigger));
    }

    /// <summary>
    /// The first spell of the chain for a living-side add (vmangos SPELL_A/B/C_TO_ANCHOR_1), or 0 for any other creature.
    /// </summary>
    internal static uint GothikFirstAnchorSpell(uint entry) => entry switch
    {
        16124 => GothikAnchorChainScript.TraineeToAnchor1,
        16125 => GothikAnchorChainScript.KnightToAnchor1,
        16126 => GothikAnchorChainScript.RiderToAnchor1,
        _ => 0,
    };

    /// <summary>
    /// vmangos boss_gothikAI::SummonedCreatureJustDied: a living-side add's death starts the anchor chain from a 15-second trigger on its corpse.
    /// </summary>
    private void StartGothikAnchorChain(Creature dead)
    {
        uint spell = GothikFirstAnchorSpell(dead.Entry);
        if (spell == 0 || Instance.FindUpdater<CreatureMapSystem>() is not { } system
            || system.Content.FindTemplate(GothikTrigger) is not { } template
            || ClosestGothikAnchor(dead, livingSide: true) is not { } anchor) return;
        PruneGothikCorpseTriggers(system, all: false);
        Creature trigger = system.SpawnTemporary(template, dead.X, dead.Y, dead.Z, dead.Orientation);
        _gothikCorpseTriggers.Add(trigger.Guid);
        system.MarkTimedDespawn(trigger, GothikCorpseTriggerMs);
        system.CastSpell(trigger, spell, anchor, triggered: true);
    }

    /// <summary>vmangos instance_naxxramas::IsInRightSideGothArea: the living side is at or below the combat gate's Y.</summary>
    internal bool? IsOnGothikLivingSide(Unit unit)
        => GetSingleGameObjectFromStorage(GothikCombatGate) is { } gate ? gate.Y >= unit.Y : null;

    /// <summary>vmangos instance_naxxramas::GetClosestAnchorForGoth: the nearest high anchor of a side.</summary>
    internal Creature? ClosestGothikAnchor(Unit source, bool livingSide)
        => GothikTriggers(high: true, livingSide)
            .MinBy(c => (c.X - source.X) * (c.X - source.X) + (c.Y - source.Y) * (c.Y - source.Y) + (c.Z - source.Z) * (c.Z - source.Z));

    /// <summary>vmangos instance_naxxramas::GetGothSummonPointCreatures: the low summon points of a side.</summary>
    internal IReadOnlyList<Creature> GothikSummonPoints(bool livingSide) => [.. GothikTriggers(high: false, livingSide)];

    private IEnumerable<Creature> GothikTriggers(bool high, bool livingSide)
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } system
            || GetSingleCreatureFromStorage(GothikEntry) is not { } gothik
            || GetSingleGameObjectFromStorage(GothikCombatGate) is not { } gate) return [];
        // vmangos SetGothTriggers: bIsAnchorHigh = Z >= Gothik's Z - 5; bIsRightSide = gate Y >= Y.
        return system.Creatures.Where(c => c.Entry == GothikTrigger && !_gothikCorpseTriggers.Contains(c.Guid)
            && (c.Z >= gothik.Z - 5.0f) == high
            && (gate.Y >= c.Y) == livingSide);
    }

    /// <summary>
    /// vmangos boss_gothikAI::SummonAdd for a spectral add raised by "Anchor to Skulls": only while Gothik fights or is dead; a TIMED_OR_DEAD
    /// summon of seven minutes. After the gates open the add fights the whole zone; before, it takes every player on the spectral side and
    /// attacks the nearest.
    /// </summary>
    internal void SummonGothikSpectralAdd(uint entry, float x, float y, float z, float orientation)
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } system
            || GetSingleCreatureFromStorage(GothikEntry) is not { } gothik
            || (!gothik.Combat.IsInCombat && gothik.IsAlive)
            || system.Content.FindTemplate(entry) is not { } template) return;
        Creature add = system.SpawnTemporary(template, x, y, z, orientation, gothik);
        system.MarkTimedDespawn(add, 420000);
        if (GetData(Gothik) is EncounterState.Special or EncounterState.Done)
        {
            system.SetInCombatWithZone(add);
            return;
        }

        Player? nearest = null;
        float best = float.MaxValue;
        foreach (Player player in Instance.Players)
        {
            if (!player.IsAlive || IsOnGothikLivingSide(player) != false) continue;
            add.Combat.Threat.AddThreat(player, 100);
            float distance = (player.X - x) * (player.X - x) + (player.Y - y) * (player.Y - y) + (player.Z - z) * (player.Z - z);
            if (distance < best) { best = distance; nearest = player; }
        }

        if (nearest is not null) add.AI?.AttackStart(nearest);
    }
}
