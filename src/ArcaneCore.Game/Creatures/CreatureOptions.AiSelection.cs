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
}
