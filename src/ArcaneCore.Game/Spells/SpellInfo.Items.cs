namespace ArcaneCore.Game.Spells;

/// <summary>
/// Item requirements of a spell (crafting lane). vmangos Spells/SpellEntry.h:635-637 lays them out as Totem[2] (columns 40-41),
/// Reagent[8] (42-49) and ReagentCount[8] (50-57); Spell::CheckItems reads them at Spell.cpp:7249-7306 and TakeReagents
/// consumes them at Spell.cpp:5082-5128. Both lists are filled by <c>SpellStoreFactory.ToSpellInfo</c> from <c>spell_template</c>; the
/// reagents themselves are <see cref="SpellInfo.Reagents"/> (the eight original slots, <see cref="SpellReagent"/>).
/// </summary>
public sealed partial record SpellInfo
{
    /// <summary>The tool items (Spell.dbc Totem[2]: Blacksmith Hammer, Runed rods, shaman totems...) that must be carried but are not consumed; slot value 0 is absent.</summary>
    public IReadOnlyList<uint> Totems { get; init; } = [];
}
