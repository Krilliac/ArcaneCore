namespace ArcaneCore.Game.Spells;

/// <summary>One reagent of a spell: an item entry and how many are consumed (vmangos SpellEntry Reagent[i] / ReagentCount[i]).</summary>
public readonly record struct SpellReagent(uint ItemId, uint Count);

/// <summary>
/// Item requirements of a spell (crafting lane). vmangos Spells/SpellEntry.h:635-637 lays them out as Totem[2] (columns 40-41),
/// Reagent[8] (42-49) and ReagentCount[8] (50-57); Spell::CheckItems reads them at Spell.cpp:7249-7306 and TakeReagents
/// consumes them at Spell.cpp:5082-5128. Both lists are filled by <c>SpellStoreFactory.ToSpellInfo</c> from <c>spell_template</c>.
/// </summary>
public sealed partial record SpellInfo
{
    /// <summary>The reagents in slot order. A slot whose item id is not positive is absent (vmangos Spell.cpp:7254 <c>Reagent[i] &lt;= 0</c>).</summary>
    public IReadOnlyList<SpellReagent> Reagents { get; init; } = [];

    /// <summary>The tool items (Spell.dbc Totem[2]: Blacksmith Hammer, Runed rods, shaman totems...) that must be carried but are not consumed; slot value 0 is absent.</summary>
    public IReadOnlyList<uint> Totems { get; init; } = [];
}
