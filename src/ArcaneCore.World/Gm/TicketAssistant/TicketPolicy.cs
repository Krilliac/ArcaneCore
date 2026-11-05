namespace ArcaneCore.World.Gm.TicketAssistant;

/// <summary>
/// The code-side allowlist: which <see cref="TicketAction"/> a category may lead to, and the named evidence each needs. Pure: the
/// same category and facts always give the same decision. A model's influence on the world is only the category; the target is always the
/// ticket's author, and there is no entry that gives an item, gold, experience, reputation or a level, or that bans, kicks, mutes or
/// touches an account. (With <see cref="GmTicketAssistantOptions.UseModel"/> on, a model also drafts the text of a Question answer, which closes
/// the ticket; that path is screened by <c>GmTicketAssistant.ScreenDraft</c>, not decided here.) Each entry's evidence predicate is a method of this class.
/// </summary>
public static class TicketPolicy
{
    /// <summary>Evidence of <see cref="TicketAction.Tele"/>: the server itself saw the author under the terrain floor, recently. (Not a fall: its duration includes the time the client reports.)</summary>
    public const string EvidencePositionSignal = "position-signal";

    /// <summary>Evidence of <see cref="TicketAction.Unstuck"/>: the author has not moved for the configured time.</summary>
    public const string EvidenceIdle = "idle";

    /// <summary>Evidence of <see cref="TicketAction.Revive"/>: a ghost for the configured time.</summary>
    public const string EvidenceDeadLong = "dead-long";

    /// <summary>Evidence of <see cref="TicketAction.ReviveInPlace"/>: dead and unreleased for the configured time, after a position signal at the body (a death under the map).</summary>
    public const string EvidenceServerCausedDeath = "server-caused-death";

    /// <summary>Evidence of <see cref="TicketAction.ClearCombat"/>: in combat, with no attacker, victim or threat, for the configured time.</summary>
    public const string EvidenceStaleCombat = "stale-combat";

    /// <summary>The decision for a ticket of <paramref name="category"/> whose author is described by <paramref name="facts"/>.</summary>
    public static TicketDecision Decide(TicketCategory category, TicketFacts facts, GmTicketAssistantOptions options)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        TicketDecision none = new(TicketAction.Escalate, string.Empty);
        switch (category)
        {
            case TicketCategory.Question:
                return new TicketDecision(TicketAction.Answer, "faq");
            case TicketCategory.Stuck:
                if (!facts.Online || facts.Blocked)
                {
                    return none;
                }

                if (facts.Alive && facts.StaleCombat)
                {
                    return new TicketDecision(TicketAction.ClearCombat, EvidenceStaleCombat);
                }

                // Teleporting is an escape: never in a fight or a duel; the dead are only moved once they are ghosts.
                if (facts.PositionSignal is not null && !facts.InCombat && (facts.Alive || facts.Ghost))
                {
                    return new TicketDecision(TicketAction.Tele, EvidencePositionSignal);
                }

                if (facts.Alive || facts.Ghost)
                {
                    return facts.IdleSeconds >= options.StuckIdleSeconds
                        ? new TicketDecision(TicketAction.Unstuck, EvidenceIdle)
                        : none;
                }

                return none;
            case TicketCategory.DeadCantRes:
                if (!facts.Online || facts.Alive || facts.Blocked || facts.DeadSeconds < options.DeadSeconds)
                {
                    return none;
                }

                if (facts.SignalAtDeath && !facts.Ghost)
                {
                    return new TicketDecision(TicketAction.ReviveInPlace, EvidenceServerCausedDeath);
                }

                return facts.Ghost ? new TicketDecision(TicketAction.Revive, EvidenceDeadLong) : none;
            default:
                return none;
        }
    }

    /// <summary>The only actions that change the world (and so count against the per-account cooldown).</summary>
    public static bool ChangesWorld(TicketAction action) => action is not (TicketAction.Escalate or TicketAction.Answer);
}
