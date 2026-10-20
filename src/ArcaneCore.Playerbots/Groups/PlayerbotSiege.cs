using System.Numerics;
using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>What a siege raid's leader does now (<see cref="PlayerbotSiegeCommander.Decide"/>).</summary>
internal enum PlayerbotSiegeOrder : byte
{
    /// <summary>The raid stands where it is: at its waypoint while the other raid is not there yet, or before the siege starts.</summary>
    Hold,

    /// <summary>Out of combat: the raid waits for a laggard, a member who rests or a hurt one.</summary>
    Regroup,

    /// <summary>The leader walks on to the shared waypoint (or, past the last one, to the objective).</summary>
    Advance,

    /// <summary>The leader opens the fight on the nearest hostile and marks it with the skull.</summary>
    Pull,

    /// <summary>Two or more hostiles and no cross: the second one is marked with the cross for the tanks.</summary>
    AssignTank,

    /// <summary>In combat: everyone kills the skull.</summary>
    Assault,

    /// <summary>The fight is lost: the raid disengages to the last waypoint (or where the siege began). Nobody fights or casts; the leader walks and the members follow.</summary>
    Retreat,

    /// <summary>The retreat reached its point: the raid stands there and fights back what followed it, marking nothing.</summary>
    Rally,
}

/// <summary>
/// What the siege commander knows about one raid at one moment. All of it is read from the world by the coordinator
/// (<c>PlayerbotGroupCoordinator.Siege.cs</c>); the rules over it are <see cref="PlayerbotSiegeCommander.Decide"/>.
/// </summary>
/// <param name="InCombat">A living member is in combat.</param>
/// <param name="MsSinceCombat">How long ago the raid was last in combat (0 in combat).</param>
/// <param name="Total">Bot members of the raid, dead or alive.</param>
/// <param name="Alive">Those alive.</param>
/// <param name="HasTank">The raid has a member in the tank role.</param>
/// <param name="TankAlive">One of them lives.</param>
/// <param name="AverageHealthPct">Mean health of the members in percent, a dead member counting 0.</param>
/// <param name="MaxLagYards">How far the farthest living member is from the leader (a member on another map counts as far).</param>
/// <param name="AnyResting">A member eats or drinks.</param>
/// <param name="AnyHurt">A living member is below the rest threshold and has not waited too long for it.</param>
/// <param name="AtWaypoint">The leader stands at the waypoint the siege is heading for.</param>
/// <param name="PeersAtWaypoint">The leaders of the other raids stand there too (true with no other raid, or past the last waypoint).</param>
/// <param name="Hostiles">Hostile units within <see cref="PlayerbotSiegeCommander.PullOffYards"/> of the leader.</param>
/// <param name="NearestHostileYards">The nearest of them (positive infinity when none).</param>
/// <param name="CrossValid">The cross icon marks a living hostile.</param>
/// <param name="RetreatLatched">The raid is in a retreat that has not ended.</param>
/// <param name="PullLatched">The raid is in a pull that has not ended.</param>
/// <param name="DeadSinceEngagement">Members who died since the raid last entered combat (deaths of earlier fights do not count towards "a third lost").</param>
/// <param name="RetreatsUsed">Retreats the raid has begun so far; past <see cref="PlayerbotSiegeCommander.MaxRetreats"/> it commits.</param>
/// <param name="ObjectiveEngaged">A member fights the siege objective, or the skull is on it: the raid never retreats from it.</param>
/// <param name="AtRally">The leader stands within <see cref="PlayerbotSiegeCommander.RallyYards"/> of the retreat point.</param>
/// <param name="MsAtRally">How long the leader has stood at the retreat point during this retreat (0: not there yet).</param>
/// <param name="RetreatedThisEngagement">The raid has already begun a retreat in this engagement (it begins no second one).</param>
internal readonly record struct PlayerbotSiegeFacts(
    bool InCombat,
    uint MsSinceCombat,
    int Total,
    int Alive,
    bool HasTank,
    bool TankAlive,
    float AverageHealthPct,
    float MaxLagYards,
    bool AnyResting,
    bool AnyHurt,
    bool AtWaypoint,
    bool PeersAtWaypoint,
    int Hostiles,
    float NearestHostileYards,
    bool CrossValid,
    bool RetreatLatched,
    bool PullLatched,
    int DeadSinceEngagement = 0,
    int RetreatsUsed = 0,
    bool ObjectiveEngaged = false,
    bool AtRally = false,
    uint MsAtRally = 0,
    bool RetreatedThisEngagement = false);

/// <summary>
/// The commander's answer: the order, and the two latches to feed back in at the next decision. <paramref name="Committed"/>: the fight is lost by
/// the retreat rules but the raid has used up its retreats and presses on (announced once).
/// </summary>
internal readonly record struct PlayerbotSiegeDecision(PlayerbotSiegeOrder Order, bool RetreatLatched, bool PullLatched, bool Committed = false);

/// <summary>
/// The rules a siege raid's leader follows, in this order, over <see cref="PlayerbotSiegeFacts"/> (pure, no world):
/// <list type="number">
/// <item>Retreat: in combat with the tank dead, the average health below <see cref="RetreatHealthPct"/> or a third of the raid lost in this
/// engagement (<see cref="PlayerbotSiegeFacts.DeadSinceEngagement"/>). A latch in two phases: <see cref="PlayerbotSiegeOrder.Retreat"/> disengages
/// (nobody fights, the leader walks to the retreat point, the members follow) until the leader is within <see cref="RallyYards"/> of it, then
/// <see cref="PlayerbotSiegeOrder.Rally"/> stands there and fights back what followed. The latch ends when the raid has been out of combat for
/// <see cref="RetreatQuietMs"/>, or has stood at the point <see cref="RallyHoldMs"/> and still fights (the bound: a pursuit that does not end is fought).
/// It begins once per engagement (<see cref="PlayerbotSiegeFacts.RetreatedThisEngagement"/>), never while the raid fights the objective, nor after
/// <see cref="MaxRetreats"/>.</item>
/// <item>Regroup or Hold, out of combat: a member lags more than <see cref="LagYards"/>, rests or is hurt (Regroup); the raid is at its waypoint,
/// the other raid is not, and nothing is worth pulling (Hold).</item>
/// <item>Pull, out of combat: a hostile within <see cref="PullOnYards"/>, kept until it is farther than <see cref="PullOffYards"/> (hysteresis).</item>
/// <item>AssignTank: two or more hostiles and no cross on a living one.</item>
/// <item>Advance, out of combat.</item>
/// <item>Assault, in combat.</item>
/// </list>
/// </summary>
internal static class PlayerbotSiegeCommander
{
    /// <summary>The average health under which a fight is given up.</summary>
    internal const float RetreatHealthPct = 35f;

    /// <summary>A living member farther than this from the leader holds the raid.</summary>
    internal const float LagYards = 30f;

    /// <summary>A hostile this close starts a pull.</summary>
    internal const float PullOnYards = 40f;

    /// <summary>A pull in progress ends when the hostile is farther than this.</summary>
    internal const float PullOffYards = 55f;

    /// <summary>A retreat ends once the raid has been out of combat this long.</summary>
    internal const uint RetreatQuietMs = 5_000;

    /// <summary>The most retreats a raid begins; after them it presses on whatever the fight looks like.</summary>
    internal const int MaxRetreats = 2;

    /// <summary>The raid warning of a raid that has used up its retreats.</summary>
    internal const string CommitLine = "No more retreat - press on!";

    /// <summary>The siege makes no waypoint progress and fights nothing for this long: it is reported stalled.</summary>
    internal const uint StallMs = 120_000;

    /// <summary>The raid icon index of the cross (skull is 7): the tanks' mark for the second hostile.</summary>
    internal const byte CrossIcon = 6;

    /// <summary>The raid warning that ends a won siege.</summary>
    internal const string CompleteLine = "Siege complete";

    /// <summary>The leader is at its waypoint within this many yards.</summary>
    internal const float WaypointYards = 6f;

    /// <summary>A retreating leader is at its retreat point (the retreat becomes a rally) within this many yards (2D).</summary>
    internal const float RallyYards = 8f;

    /// <summary>A rally that has stood at its point this long while still in combat ends (what followed the raid is fought on, not run from again).</summary>
    internal const uint RallyHoldMs = 30_000;

    /// <summary>The order for <paramref name="facts"/>, and the latches after it.</summary>
    internal static PlayerbotSiegeDecision Decide(in PlayerbotSiegeFacts facts)
    {
        // 1. Retreat. It begins in a lost fight and is a latch: it lasts while the fight does and a while after.
        bool tankDead = facts.HasTank && !facts.TankAlive;
        bool lost = facts.Total > 0 && facts.DeadSinceEngagement * 3 >= facts.Total;
        bool wouldRetreat = facts.InCombat && (tankDead || facts.AverageHealthPct < RetreatHealthPct || lost);
        // A raid that fights the objective, or has retreated MaxRetreats times, commits: it no longer begins a retreat.
        bool commit = facts.ObjectiveEngaged || facts.RetreatsUsed >= MaxRetreats;
        bool held = facts.RetreatLatched && !facts.ObjectiveEngaged;
        // One retreat per engagement: a latch already running is not cut short, but a lost fight begins a second one only in a new engagement.
        bool begins = wouldRetreat && !commit && !facts.RetreatLatched && !facts.RetreatedThisEngagement;
        bool quiet = !facts.InCombat && facts.MsSinceCombat >= RetreatQuietMs;
        bool chasedOut = facts.AtRally && facts.MsAtRally >= RallyHoldMs;
        if ((held || begins) && !quiet && !chasedOut)
            return new PlayerbotSiegeDecision(facts.AtRally ? PlayerbotSiegeOrder.Rally : PlayerbotSiegeOrder.Retreat, true, false);
        bool committed = wouldRetreat && !facts.RetreatLatched && !facts.ObjectiveEngaged && facts.RetreatsUsed >= MaxRetreats;

        // The pull latch: on within PullOnYards, kept until the hostile is beyond PullOffYards (hysteresis).
        bool pullCandidate = facts.Hostiles > 0
            && (facts.PullLatched ? facts.NearestHostileYards <= PullOffYards : facts.NearestHostileYards <= PullOnYards);
        bool pullLatch = !facts.InCombat && pullCandidate;

        if (!facts.InCombat)
        {
            // 2. Regroup for a laggard, a member who rests, a hurt one; hold at the waypoint for the other raid.
            if (facts.MaxLagYards > LagYards || facts.AnyResting || facts.AnyHurt)
                return new PlayerbotSiegeDecision(PlayerbotSiegeOrder.Regroup, false, false);
            if (facts.AtWaypoint && !facts.PeersAtWaypoint && !pullCandidate)
                return new PlayerbotSiegeDecision(PlayerbotSiegeOrder.Hold, false, false);

            // 3. Pull.
            if (pullCandidate) return new PlayerbotSiegeDecision(PlayerbotSiegeOrder.Pull, false, true);
        }

        // 4. Two hostiles and no cross: mark one for the tanks.
        if (facts.Hostiles >= 2 && !facts.CrossValid)
            return new PlayerbotSiegeDecision(PlayerbotSiegeOrder.AssignTank, false, pullLatch, committed);

        // 5. Advance when quiet, 6. assault when fighting.
        return new PlayerbotSiegeDecision(facts.InCombat ? PlayerbotSiegeOrder.Assault : PlayerbotSiegeOrder.Advance, false, false, committed);
    }
}

/// <summary>
/// A leader's raid warnings: one line when the plan changes, never twice the same line in a row, and never more often than every
/// <see cref="MinGapMs"/>. A line that changes while the gap runs is not lost: it is offered again with the next decision.
/// </summary>
internal sealed class PlayerbotSiegeAnnouncer
{
    /// <summary>The least time between two raid warnings of one leader.</summary>
    internal const uint MinGapMs = 3_000;

    private string? _last = null;
    private uint _lastMs = 0;

    /// <summary>The lines sent so far, oldest first (status and tests).</summary>
    internal List<string> Sent { get; } = [];

    /// <summary>Whether <paramref name="text"/> is to be sent now; when true it counts as sent.</summary>
    internal bool Offer(string text, uint nowMs)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.Equals(text, _last, StringComparison.Ordinal)) return false;
        if (_last is not null && unchecked(nowMs - _lastMs) < MinGapMs) return false;
        _last = text;
        _lastMs = nowMs;
        Sent.Add(text);
        if (Sent.Count > 64) Sent.RemoveAt(0);
        return true;
    }
}

/// <summary>One siege: the waypoints, the step the raids share, and the raids.</summary>
internal sealed class PlayerbotSiege(int id, uint mapId, uint objectiveEntry, IReadOnlyList<Vector3> waypoints, Vector3 rally, uint startedMs)
{
    public int Id { get; } = id;

    public uint MapId { get; } = mapId;

    /// <summary>The creature entry the siege is after; its death ends the siege.</summary>
    public uint ObjectiveEntry { get; } = objectiveEntry;

    /// <summary>The way, in order (a copy of the waypoints at the start).</summary>
    public IReadOnlyList<Vector3> Waypoints { get; } = waypoints;

    /// <summary>Where a raid with no waypoint behind it retreats to: where the siege began.</summary>
    public Vector3 Rally { get; } = rally;

    public uint StartedMs { get; } = startedMs;

    /// <summary>The waypoint every raid is heading for; equal to the waypoint count once all reached the last one (the approach to the objective).</summary>
    public int Step { get; set; }

    public List<PlayerbotSiegeRaid> Raids { get; } = [];

    /// <summary>The objective was seen alive (its death then ends the siege).</summary>
    public bool ObjectiveSeen { get; set; }

    /// <summary>Where the objective was last seen alive; its death is concluded only when the grid there is loaded and none lives.</summary>
    public Vector3 ObjectivePosition { get; set; }

    /// <summary>The objective was seen alive and is not now, but the grid at <see cref="ObjectivePosition"/> is not loaded: it is unseen, not dead.</summary>
    public bool ObjectiveUnseen { get; set; }

    /// <summary>When the siege last made progress (the step moved, or a raid fought); 0 before the first tick.</summary>
    public uint ProgressMs { get; set; }

    /// <summary>The step <see cref="ProgressMs"/> was taken at.</summary>
    public int ProgressStep { get; set; }

    /// <summary>No progress for <see cref="PlayerbotSiegeCommander.StallMs"/> (reported once per stall; status shows it).</summary>
    public bool Stalled { get; set; }

    /// <summary>The objective died: the raids announce it and the siege ends.</summary>
    public bool Won { get; set; }

    /// <summary>When the win was seen.</summary>
    public uint WonMs { get; set; }

    /// <summary>GMs who asked to watch (<c>.playerbot siege watch</c>), by character guid.</summary>
    public HashSet<ObjectGuid> Watchers { get; } = [];

    /// <summary>When the watchers were last told (status and tests).</summary>
    public uint WatchedMs { get; set; }
}

/// <summary>One bot-led raid of a siege and what its commander decided.</summary>
internal sealed class PlayerbotSiegeRaid(PlayerbotSiege siege, int index)
{
    public PlayerbotSiege Siege { get; } = siege;

    /// <summary>1-based, in the order the raids were formed.</summary>
    public int Index { get; } = index;

    public PlayerbotGroupCoordinator.BotGroup? Group { get; set; }

    public PlayerbotSiegeOrder Order { get; set; } = PlayerbotSiegeOrder.Hold;

    /// <summary>Where the leader walks under <see cref="PlayerbotSiegeOrder.Advance"/>, <see cref="PlayerbotSiegeOrder.Retreat"/> and <see cref="PlayerbotSiegeOrder.Rally"/> (the retreat point).</summary>
    public Vector3 MoveTo { get; set; }

    /// <summary>The unit the leader opens on under Pull, AssignTank and Assault: the skull's.</summary>
    public ObjectGuid PullTarget { get; set; }

    public bool RetreatLatched { get; set; }

    /// <summary>The raid has begun a retreat in the current engagement (cleared when the raid enters combat from quiet).</summary>
    public bool RetreatedThisEngagement { get; set; }

    /// <summary>The retreating leader reported that it has no path to the retreat point (set by <see cref="PlayerbotGroupAI"/>; the raid then rallies in place).</summary>
    public bool RetreatNoPath { get; set; }

    /// <summary>The time at the retreat point was restarted once in this latch (see <see cref="EndNoPathRally"/>); cleared where the latch ends.</summary>
    public bool RallyClockRestarted { get; set; }

    /// <summary>
    /// The no-path rally ends (the leader planned a walk again, or a new leader took over). The time at the point restarts at most once per latch,
    /// and only when the flag was set: a path that flaps (a plan that is "pending", then refused) must not keep the 30 s bound from ever being reached.
    /// </summary>
    public void EndNoPathRally()
    {
        if (!RetreatNoPath) return;
        RetreatNoPath = false;
        if (RallyClockRestarted) return;
        RallyClockRestarted = true;
        RallySinceMs = 0;
    }

    /// <summary>The bot that led the raid at the last read; a change (a promotion) starts the retreat afresh for the new leader.</summary>
    public Guid LeaderSeen { get; set; }

    /// <summary>When the leader first stood at the retreat point during the current retreat (0: not there during this latch).</summary>
    public uint RallySinceMs { get; set; }

    /// <summary>The facts the last read of the raid saw (status and tests).</summary>
    internal PlayerbotSiegeFacts LastFacts { get; set; }

    public bool PullLatched { get; set; }

    public int Retreats { get; set; }

    /// <summary>The raid was in combat at the last tick.</summary>
    public bool WasInCombat { get; set; }

    /// <summary>When the raid was last seen in combat (0: never); a new engagement begins only after <see cref="PlayerbotSiegeCommander.RetreatQuietMs"/> without it.</summary>
    public uint CombatSeenMs { get; set; }

    /// <summary>The members dead when the current engagement began: their deaths do not count towards "a third lost".</summary>
    public HashSet<ObjectGuid> DeadAtEngagement { get; } = [];

    /// <summary>The raid has said <see cref="PlayerbotSiegeCommander.CommitLine"/>.</summary>
    public bool CommitAnnounced { get; set; }

    public bool AtWaypoint { get; set; }

    /// <summary>When a member first was hurt in the current stretch of regrouping (0: none is).</summary>
    public uint HurtSinceMs { get; set; }

    /// <summary>When the last promotion of a fallen leader was tried.</summary>
    public uint PromoteTriedMs { get; set; }

    public PlayerbotSiegeAnnouncer Announcer { get; } = new();

    /// <summary>The hostiles, the order and the step as the last decision saw them (status).</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>What the raid does, in one line, for a watching GM (<c>.playerbot siege watch</c>).</summary>
    public string WatchLine { get; set; } = string.Empty;

    /// <summary>The <see cref="WatchLine"/> the watchers were last told; a line is sent again only when it changes.</summary>
    public string WatchedLine { get; set; } = string.Empty;
}
