namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureOptions
{
    /// <summary>
    /// <c>Creatures:ImplicitEventAi</c>: a creature whose template has no AIName but whose entry (or spawn) has <c>creature_ai_scripts</c> rows runs
    /// EventAI. The cmangos-classic data this server imports has no AIName column at all (classic-db z2815 creature_template has 79 columns, none
    /// of them AIName) and cmangos selects EventAI by default (<c>CreatureEventAI::Permissible</c>, AI/EventAI/CreatureEventAI.cpp:51-63, which
    /// accepts every creature that is not a pet, totem or guard); vmangos instead needs <c>ai_name = 'EventAI'</c> (AI/EventAI/CreatureEventAI.cpp:51-56,
    /// AI/CreatureAISelector.cpp:37-100). With the switch on (the default), imported EventAI data drives behaviour (4,325 classic-db templates have rows,
    /// 1,284 of them "flee at 15%"); off restores AIName-only selection. An explicit AIName always wins.
    /// </summary>
    public bool ImplicitEventAi { get; set; } = true;
}
