using ArcaneCore.Game.Creatures.AI;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Creature spell lists on the map: which creatures run one, the tick, and a script's list change (cmangos Creature::SetSpellList,
/// UnitAI::SpellListChanged). Only a creature whose AI runs lists and that has a list carries a scheduler, so every other creature costs one
/// null test per tick and no random draw.
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// Give a creature the list its template or script names (spawn, respawn, a new AI, a new entry, a content reload). The availability roll runs here,
    /// once per assignment; combat arms the initial cooldowns later (<see cref="CreatureSpellListScheduler{TTarget}.EnterCombat"/>), or at once for a
    /// creature already fighting. A template without a list falls back to list <c>entry * 100</c> (cmangos Creature::UpdateEntry, "legacy compatibility").
    /// </summary>
    private void BindSpellList(Creature creature)
    {
        creature.SpellListVersion = _content.DefinitionsVersion;
        uint listId = creature.Template.SpellList;
        if (listId == 0)
        {
            listId = creature.AI?.ScriptSpellList ?? 0;
        }

        bool named = listId != 0;
        if (!named)
        {
            listId = creature.Template.Entry * 100;
        }

        CreatureSpellList? list = creature.AI is { RunsSpellList: true } ? _content.Ai.SpellLists.Find(listId) : null;
        if (list is null && named && creature.AI is { RunsSpellList: true })
        {
            ReportSpellListOnce($"list:{listId}", "unknown-list", $"creature {creature.Template.Entry} names spell list {listId}, which does not exist");
        }

        AssignSpellList(creature, list);
    }

    private void AssignSpellList(Creature creature, CreatureSpellList? list)
    {
        if (list is null)
        {
            creature.SpellLists = null;
        }
        else
        {
            creature.SpellLists ??= new CreatureSpellListScheduler<Unit>(new CreatureSpellListHost(this, creature), _random, _options.SpellListContinueAfterCast);
            creature.SpellLists.Bind(list);
            if (creature.Combat.IsInCombat)
            {
                creature.SpellLists.EnterCombat(); // cmangos UnitAI::SpellListChanged
            }
        }

        (creature.AI as CreatureEventAI)?.RebuildMainSpells();
    }

    /// <summary>
    /// cmangos Creature::SetSpellList for a script: 0 clears the list, an unknown id keeps the current one and answers false, and a list set in the
    /// middle of a fight arms its initial cooldowns (UnitAI::SpellListChanged). False too when the creature's AI does not run lists.
    /// </summary>
    public bool SetSpellList(Creature creature, uint listId)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.AI is not { RunsSpellList: true })
        {
            return false;
        }

        if (listId == 0)
        {
            AssignSpellList(creature, null);
            return true;
        }

        if (_content.Ai.SpellLists.Find(listId) is not { } list)
        {
            ReportSpellListOnce($"list:{listId}", "unknown-list", $"a script set spell list {listId} on creature {creature.Template.Entry}, which does not exist");
            return false;
        }

        AssignSpellList(creature, list);
        return true;
    }

    /// <summary>The list half of a creature's tick. A reload reaches a creature at its next tick out of combat, so a fight keeps the list it began with.</summary>
    private void UpdateSpellList(Creature creature, uint diffMs)
    {
        if (creature.SpellListVersion != _content.DefinitionsVersion && !creature.Combat.IsInCombat)
        {
            BindSpellList(creature);
        }

        creature.SpellLists?.Update(diffMs);
    }

    /// <summary>Put a spell list problem on the anomaly ledger, once per map for each <paramref name="key"/>.</summary>
    internal void ReportSpellListOnce(string key, string code, string detail)
    {
        if (_reportedAi.Add($"spell-list:{key}"))
        {
            AnomalyLedger.Shared.Report("creature-spell-lists", code, key, detail);
        }
    }
}
