namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>Where a bot is in its battleground life (<see cref="PlayerbotBattlegroundQueueMachine"/>).</summary>
internal enum PlayerbotBattlegroundPhase : byte
{
    /// <summary>Not in a battleground business; the brain drives the bot.</summary>
    Idle,

    /// <summary>Walking to a friendly battlemaster.</summary>
    Travel,

    /// <summary>CMSG_BATTLEMASTER_HELLO and CMSG_BATTLEMASTER_JOIN sent; waiting for the queue slot.</summary>
    Join,

    /// <summary>In the queue; the brain drives the bot while it waits.</summary>
    Queued,

    /// <summary>An invitation is pending that the bot cannot answer yet (dead, or no longer free); the brain still drives.</summary>
    Invited,

    /// <summary>CMSG_BATTLEFIELD_PORT sent; on the way into the match.</summary>
    Entering,

    /// <summary>A participant of a match.</summary>
    InMatch,

    /// <summary>CMSG_LEAVE_BATTLEFIELD sent; on the way out.</summary>
    Leaving,
}

/// <summary>What the bot sends or does at a transition of <see cref="PlayerbotBattlegroundQueueMachine"/>.</summary>
internal enum PlayerbotBattlegroundAction : byte
{
    None,

    /// <summary>Pick the battlemaster and start walking (the walk itself goes on while the phase is Travel).</summary>
    PlanToBattlemaster,

    /// <summary>CMSG_BATTLEMASTER_HELLO (vmangos BattleGroundHandler.cpp:40-73).</summary>
    Hello,

    /// <summary>CMSG_BATTLEMASTER_JOIN (BattleGroundHandler.cpp:82-264).</summary>
    Join,

    /// <summary>CMSG_BATTLEFIELD_PORT, action 1: enter the match (BattleGroundHandler.cpp:361-507).</summary>
    PortEnter,

    /// <summary>CMSG_BATTLEFIELD_PORT, action 0: leave the queue or decline the invitation.</summary>
    PortLeave,

    /// <summary>CMSG_LEAVE_BATTLEFIELD (BattleGroundHandler.cpp:510-525).</summary>
    LeaveBattlefield,

    /// <summary>Hand the bot back to its brain (it resumes whatever it did before).</summary>
    Release,

    /// <summary>The type did not answer a join: set it aside for this bot for a while, then release.</summary>
    SetAside,
}

/// <summary>What the coordinator observed about a bot this tick; everything the machine needs, as plain values.</summary>
internal readonly record struct PlayerbotBattlegroundFacts
{
    /// <summary>Idle: the bot is free, eligible and its roll (or a real player's need) hit.</summary>
    public bool Roll { get; init; }

    /// <summary>Travel/Join: the battlemaster is interactable (the server's own InteractableNpc check).</summary>
    public bool InReach { get; init; }

    /// <summary>The bot is dead, out of the world or between maps: it cannot answer an invitation or walk.</summary>
    public bool Unable { get; init; }

    /// <summary>Hello was sent.</summary>
    public bool HelloSent { get; init; }

    /// <summary>Join was sent.</summary>
    public bool JoinSent { get; init; }

    /// <summary>The bot has a queue slot for the type.</summary>
    public bool QueueSlot { get; init; }

    /// <summary>The instance the bot is invited to for the type (0: none).</summary>
    public uint InvitedInstance { get; init; }

    /// <summary>The invited match already holds <c>MaxBotsPerBattleground</c> bots (<see cref="PlayerbotBattlegroundDemand.MustDecline"/>).</summary>
    public bool MatchHoldsCap { get; init; }

    /// <summary>The bot waited <c>MaxQueueWaitMinutes</c> in the queue.</summary>
    public bool QueuedTooLong { get; init; }

    /// <summary>
    /// Something else has the bot: a scripted controller, its party AI or a bot-led group drives it, or it is in a group (not a battleground
    /// raid) or holds a group invitation. Read every tick from the group manager itself, not from the party AI's next think.
    /// </summary>
    public bool Taken { get; init; }

    /// <summary>The bot is bound to a match and has arrived (added to the match, in the world on its map).</summary>
    public bool Arrived { get; init; }

    /// <summary>The bot is still bound to a match (the manager's state).</summary>
    public bool Bound { get; init; }

    /// <summary>The match ended (WaitLeave) or is gone.</summary>
    public bool MatchOver { get; init; }

    /// <summary>The bot is in the world, on a map that is no battleground, bound to no match.</summary>
    public bool BackInWorld { get; init; }

    /// <summary>Milliseconds the bot has been in its phase.</summary>
    public uint InPhaseMs { get; init; }

    /// <summary>The match the bot is bound to is gone (not found), whatever <see cref="Bound"/> says.</summary>
    public bool MatchGone { get; init; }

    /// <summary>The walk to the battlemaster refused a route (or arrived at an empty/dead battlemaster spot).</summary>
    public bool TravelRefused { get; init; }

    /// <summary>Travel: the length of the walk to the battlemaster as estimated when it was planned, in yards (0: not known, the base timeout).</summary>
    public float TravelYards { get; init; }
}

/// <summary>
/// The queue-to-leave life of one bot, free of world state (unit tested): <see cref="Next"/> maps a phase and the observed facts to the
/// next phase and the one thing to do. The flow mirrors a client's: walk to a battlemaster, hello, join (vmangos
/// BattleGroundHandler.cpp:40-264), wait in the queue, answer SMSG_BATTLEFIELD_STATUS' invitation with CMSG_BATTLEFIELD_PORT
/// (BattleGroundHandler.cpp:361-507), play, and leave with CMSG_LEAVE_BATTLEFIELD once the match is over (vmangos BattleBotAI.cpp:461-485).
/// </summary>
internal static class PlayerbotBattlegroundQueueMachine
{
    /// <summary>The longest a bot walks to a battlemaster.</summary>
    internal const uint TravelTimeoutMs = 600_000;

    /// <summary>The slowest pace (yards a second) a walk to a battlemaster is expected to keep, fights and detours included; a run is 7.</summary>
    internal const float TravelYardsPerSecond = 3f;

    /// <summary>The most a walk to a battlemaster is given, however long its way.</summary>
    internal const uint MaxTravelTimeoutMs = 3_600_000;

    /// <summary>A walk is longer than its straight line: the way round a ridge or a river (the estimate of a walk's length is the line times this).</summary>
    internal const float TravelDetourFactor = 1.5f;

    /// <summary>
    /// The longest a bot walks to a battlemaster whose way is <paramref name="pathYards"/> long: <see cref="TravelTimeoutMs"/> for a short or
    /// unknown way, longer by the pace <see cref="TravelYardsPerSecond"/> for a long one, at most <see cref="MaxTravelTimeoutMs"/>. A flat
    /// ten minutes timed out the long walks across a continent (live: 'timed out walking to ArathiBasin battlemaster').
    /// </summary>
    internal static uint TravelTimeoutFor(float pathYards)
    {
        if (!float.IsFinite(pathYards) || pathYards <= 0f) return TravelTimeoutMs;
        double ms = pathYards / (double)TravelYardsPerSecond * 1000.0;
        return (uint)Math.Clamp(ms, TravelTimeoutMs, MaxTravelTimeoutMs);
    }

    /// <summary>How much closer to the battlemaster a walk has to get to count as progress.</summary>
    internal const float TravelProgressYards = 10f;

    /// <summary>How long a walk may go without that progress before it is given up (a long timeout, <see cref="TravelTimeoutFor"/>, is no licence to stand still).</summary>
    internal const uint TravelStallMs = 180_000;

    /// <summary>
    /// The walk's progress watch. <paramref name="yardsLeft"/> is the straight distance to the battlemaster now; <paramref name="bestYards"/>
    /// (negative: not seen yet) and <paramref name="bestAtMs"/> are the closest it came and when, updated here. True when the bot has not come
    /// <see cref="TravelProgressYards"/> closer than that in <see cref="TravelStallMs"/>.
    /// </summary>
    internal static bool TravelStalled(float yardsLeft, ref float bestYards, ref uint bestAtMs, uint nowMs)
    {
        if (!float.IsFinite(yardsLeft)) return false;
        if (bestYards < 0f || yardsLeft < bestYards - TravelProgressYards)
        {
            bestYards = yardsLeft;
            bestAtMs = nowMs;
            return false;
        }

        return unchecked(nowMs - bestAtMs) > TravelStallMs;
    }

    /// <summary>The longest the server takes to put the bot in a queue after the join.</summary>
    internal const uint JoinReplyTimeoutMs = 15_000;

    /// <summary>The longest between the port and the arrival in the match.</summary>
    internal const uint EnteringTimeoutMs = 30_000;

    /// <summary>The longest between the leave and the return to the world.</summary>
    internal const uint LeavingTimeoutMs = 30_000;

    /// <summary>How long a type that did not answer a join is set aside for the bot.</summary>
    internal const uint SetAsideMs = 600_000;

    internal static (PlayerbotBattlegroundPhase Phase, PlayerbotBattlegroundAction Action) Next(PlayerbotBattlegroundPhase phase,
        in PlayerbotBattlegroundFacts f)
    {
        switch (phase)
        {
            case PlayerbotBattlegroundPhase.Idle:
                return f.Roll ? (PlayerbotBattlegroundPhase.Travel, PlayerbotBattlegroundAction.PlanToBattlemaster) : (phase, PlayerbotBattlegroundAction.None);

            case PlayerbotBattlegroundPhase.Travel:
                if (f.Taken || f.Unable || f.InPhaseMs > TravelTimeoutFor(f.TravelYards)) return (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.Release);
                if (f.InReach) return (PlayerbotBattlegroundPhase.Join, PlayerbotBattlegroundAction.Hello);
                return f.TravelRefused ? (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.SetAside) : (phase, PlayerbotBattlegroundAction.None);

            case PlayerbotBattlegroundPhase.Join:
                if (f.QueueSlot) return (PlayerbotBattlegroundPhase.Queued, PlayerbotBattlegroundAction.Release);
                if (f.Taken || f.Unable) return (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.Release);
                if (!f.JoinSent) return (phase, PlayerbotBattlegroundAction.Join);
                return f.InPhaseMs > JoinReplyTimeoutMs ? (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.SetAside) : (phase, PlayerbotBattlegroundAction.None);

            case PlayerbotBattlegroundPhase.Queued:
                if (f.Bound) return (PlayerbotBattlegroundPhase.Entering, PlayerbotBattlegroundAction.None);
                if (!f.QueueSlot) return (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.None);
                if (f.Taken || f.QueuedTooLong) return (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.PortLeave);
                if (f.InvitedInstance == 0) return (phase, PlayerbotBattlegroundAction.None);
                if (f.Unable) return (PlayerbotBattlegroundPhase.Invited, PlayerbotBattlegroundAction.None);
                return f.MatchHoldsCap ? (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.PortLeave)
                    : (PlayerbotBattlegroundPhase.Entering, PlayerbotBattlegroundAction.PortEnter);

            case PlayerbotBattlegroundPhase.Invited:
                if (f.Bound) return (PlayerbotBattlegroundPhase.Entering, PlayerbotBattlegroundAction.None);
                if (!f.QueueSlot || f.InvitedInstance == 0) return (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.None);
                if (f.Taken) return (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.PortLeave);
                if (f.Unable) return (phase, PlayerbotBattlegroundAction.None);
                return f.MatchHoldsCap ? (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.PortLeave)
                    : (PlayerbotBattlegroundPhase.Entering, PlayerbotBattlegroundAction.PortEnter);

            case PlayerbotBattlegroundPhase.Entering:
                if (!f.Bound || f.MatchGone || f.InPhaseMs > EnteringTimeoutMs) return (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.Release);
                // Taken after the port went out (a party joined in the same tick): out of the match again, never played for someone else.
                if (f.Taken) return (PlayerbotBattlegroundPhase.Leaving, PlayerbotBattlegroundAction.LeaveBattlefield);
                if (f.Arrived) return (PlayerbotBattlegroundPhase.InMatch, PlayerbotBattlegroundAction.None);
                return (phase, PlayerbotBattlegroundAction.None);

            case PlayerbotBattlegroundPhase.InMatch:
                if (f.MatchOver && f.Bound) return (PlayerbotBattlegroundPhase.Leaving, PlayerbotBattlegroundAction.LeaveBattlefield);
                if (!f.Bound || f.MatchGone) return f.BackInWorld ? (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.Release) : (PlayerbotBattlegroundPhase.Leaving, PlayerbotBattlegroundAction.None);
                return (phase, PlayerbotBattlegroundAction.None);

            case PlayerbotBattlegroundPhase.Leaving:
                if (f.BackInWorld) return (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.Release);
                if (f.InPhaseMs > LeavingTimeoutMs) return (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.Release);
                return (phase, PlayerbotBattlegroundAction.None);

            default:
                return (PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.Release);
        }
    }

    /// <summary>Whether the coordinator (not the brain) drives a bot in <paramref name="phase"/>.</summary>
    internal static bool Drives(PlayerbotBattlegroundPhase phase)
        => phase is PlayerbotBattlegroundPhase.Travel or PlayerbotBattlegroundPhase.Join or PlayerbotBattlegroundPhase.Entering
            or PlayerbotBattlegroundPhase.InMatch or PlayerbotBattlegroundPhase.Leaving;
}
