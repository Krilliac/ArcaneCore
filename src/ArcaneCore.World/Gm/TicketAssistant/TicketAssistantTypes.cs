namespace ArcaneCore.World.Gm.TicketAssistant;

/// <summary>What a ticket is about. The only thing a model may decide (<see cref="TicketClassifier.Parse"/>).</summary>
public enum TicketCategory
{
    Stuck,
    DeadCantRes,
    QuestBroken,
    ItemLost,
    BugReport,
    Harassment,
    Question,
    Other,
}

/// <summary>What the assistant does for a ticket, chosen by code from the category and the facts (<see cref="TicketPolicy"/>), never by a model.</summary>
public enum TicketAction
{
    /// <summary>Nothing is done to the world: a "logged for a GM" answer, a note, and the ticket stays open.</summary>
    Escalate,

    /// <summary>An answer drafted from the FAQ; the ticket is closed.</summary>
    Answer,

    /// <summary>Teleport the author to the bind point (alive) or the nearest graveyard (ghost); evidence: an under-map or fall signal.</summary>
    Tele,

    /// <summary>The <c>.unstuck</c> path; evidence: no movement for <see cref="GmTicketAssistantOptions.StuckIdleSeconds"/>.</summary>
    Unstuck,

    /// <summary>Resurrect a dead author at the graveyard with resurrection sickness (the spirit healer's rules).</summary>
    Revive,

    /// <summary>Resurrect without sickness where the author lies; evidence: the death followed an under-map or fall signal.</summary>
    ReviveInPlace,

    /// <summary>Clear an "in combat" state nobody is fighting.</summary>
    ClearCombat,
}

/// <summary>A classification: a category and a one-line summary (never a command, target or argument).</summary>
public sealed record TicketClassification(TicketCategory Category, string Summary, string Source);

/// <summary>An anomaly signal heard for a character (<see cref="TicketEvidenceLog"/>).</summary>
public sealed record TicketSignal(string Code, long AtUnix, string Detail);

/// <summary>
/// What the server knows about a ticket's author when it decides, read on the world thread. <see cref="DeadSeconds"/> and
/// <see cref="IdleSeconds"/> are 0 when the state was first seen just now.
/// </summary>
public sealed record TicketFacts(
    bool Online,
    int AccountId,
    string Name,
    uint MapId,
    float X,
    float Y,
    float Z,
    bool Alive,
    bool Ghost,
    int Level,
    long DeadSeconds,
    long IdleSeconds,
    bool InCombat,
    bool StaleCombat,
    bool Blocked,
    TicketSignal? PositionSignal,
    string? QuestNote)
{
    /// <summary>A line for the GM note.</summary>
    public string Describe()
    {
        if (!Online)
        {
            return "author offline";
        }

        string state = Alive ? "alive" : Ghost ? "ghost" : "dead";
        string text = FormattableString.Invariant($"{Name} lvl {Level} {state} map {MapId} at ({X:F1}, {Y:F1}, {Z:F1}); idle {IdleSeconds}s");
        if (!Alive)
        {
            text += FormattableString.Invariant($"; dead {DeadSeconds}s");
        }

        if (StaleCombat)
        {
            text += "; in combat with nobody fighting";
        }

        if (PositionSignal is { } signal)
        {
            text += $"; signal {signal.Code}";
        }

        return QuestNote is null ? text : text + "; " + QuestNote;
    }
}

/// <summary>A decision: the action and the named evidence that allows it.</summary>
public sealed record TicketDecision(TicketAction Action, string Evidence);

/// <summary>
/// The model seam. ArcaneCore.World never references the playerbot pack, so the pack's chat stack implements this and is found
/// through the service provider (a host without it gets the keyword classifier and no drafted answers). Both calls run off the world thread.
/// </summary>
public interface ITicketAssistantModel
{
    /// <summary>Whether a model provider is configured now.</summary>
    bool Available { get; }

    /// <summary>The model's raw reply to a classification of <paramref name="ticketText"/> (parsed strictly by <see cref="TicketClassifier"/>); null when none.</summary>
    Task<string?> ClassifyAsync(string ticketText, CancellationToken cancellationToken);

    /// <summary>
    /// An answer to a player's question from <paramref name="faq"/>, already moderated and redacted; null when no model answered or
    /// moderation or the output screen refused it.
    /// </summary>
    Task<string?> DraftAnswerAsync(string ticketText, string faq, CancellationToken cancellationToken);
}
