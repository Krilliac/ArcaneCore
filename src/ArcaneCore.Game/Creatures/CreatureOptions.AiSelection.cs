namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureOptions
{
    /// <summary>
    /// <c>Creatures:ImplicitEventAi</c> (default off, retail): vmangos runs EventAI only for a template whose <c>AIName</c> is 'EventAI'
    /// (AI/CreatureAISelector.cpp:37-100, AI/EventAI/CreatureEventAI.cpp:51-56), and classic-db z2815 carries that column
    /// (<c>creature_template.AIName</c>, 4,325 templates say 'EventAI'; imported by CreatureDumpImporter). Switched on, a creature whose
    /// template has no AIName but whose entry (or spawn) has <c>creature_ai_scripts</c> rows also runs EventAI, the cmangos-classic permit
    /// (AI/EventAI/CreatureEventAI.cpp:51-63) for hand-edited data; an explicit AIName always wins. A deviation from retail.
    /// </summary>
    public bool ImplicitEventAi { get; set; }

    /// <summary>
    /// <c>Creatures:SpellListContinueAfterCast</c> (default off): a creature spell list casts one blocking spell per tick, as cmangos doc/SpellLists.txt
    /// describes retail ("always one spell is selected"). Switched on, the tick keeps walking the eligible blocking entries after a success, as the
    /// cmangos loop in UnitAI::UpdateSpellLists does; an instant spell can then fire a second one in the same tick. A deviation from retail.
    /// </summary>
    public bool SpellListContinueAfterCast { get; set; }
}
