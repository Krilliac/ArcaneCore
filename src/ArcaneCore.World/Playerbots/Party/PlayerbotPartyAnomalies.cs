using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Playerbots.Party;

/// <summary>
/// Time accounting for one outcome check: "this went wrong and stayed wrong for the budget", reported once per episode. State is two
/// longs and two flags; <see cref="Observe"/> costs a branch while the check passes. Not thread safe (world thread only).
/// <para>
/// An episode is only believed across observations that are close in time: a gap longer than <c>maxGapMs</c> between two bad
/// observations (a bot that was not thought for, a server lag spike, another driver in between) starts a new episode, so a check
/// never reports a stretch it did not see. A clock that ran backwards (the 32-bit world clock wrapped) starts a new episode too.
/// </para>
/// </summary>
internal struct PlayerbotEpisode
{
    private long _since;
    private long _last;
    private bool _active;
    private bool _reported;

    /// <summary>Whether an episode is running (a bad observation was made and no good one since).</summary>
    internal readonly bool Active => _active;

    /// <summary>How long the running episode has lasted at <paramref name="now"/> (0 when none).</summary>
    internal readonly long ElapsedMs(long now) => _active ? now - _since : 0;

    /// <summary>Forget the episode (the thing it watched changed for a reason that is not the failure).</summary>
    internal void Reset()
    {
        _active = false;
        _reported = false;
    }

    /// <summary>
    /// One observation. <paramref name="bad"/> false ends the episode. True extends it; returns true exactly once per episode, the
    /// first time it has lasted <paramref name="budgetMs"/>.
    /// </summary>
    internal bool Observe(bool bad, long now, long budgetMs, long maxGapMs)
    {
        if (!bad)
        {
            _active = false;
            _reported = false;
            return false;
        }

        if (!_active || now < _last || now - _last > maxGapMs)
        {
            _active = true;
            _reported = false;
            _since = now;
        }

        _last = now;
        if (_reported || now - _since < budgetMs) return false;
        _reported = true;
        return true;
    }
}

/// <summary>
/// The party AI's outcome checks (area <c>party</c>, subsystem <c>bot-party</c>): what a bot in a real player's group should have done
/// and did not. Every check is a branch on the success path; the detail is formatted after it failed. See each check's type for the
/// trigger and tolerances.
/// </summary>
internal static class PlayerbotPartyAnomalies
{
    /// <summary>The area of every key here.</summary>
    internal const string Area = "party";

    /// <summary>The follower stayed more than <see cref="PlayerbotFollowWatch.FarBehindMinYards"/> from its leader on one map for <see cref="PlayerbotFollowWatch.FarBehindBudgetMs"/>.</summary>
    internal const string FollowerFarBehind = "follower-far-behind";

    /// <summary>The follower stayed on another map than a leader it may follow to for <see cref="PlayerbotFollowWatch.OtherMapBudgetMs"/>.</summary>
    internal const string FollowerOtherMap = "follower-other-map";

    /// <summary>The leader walked far away and the follower did not even try to follow for <see cref="PlayerbotFollowDropWatch.BudgetMs"/>.</summary>
    internal const string FollowDropped = "follow-dropped";

    /// <summary>A group invitation the bot answered is still pending (the answer did not reach the group service).</summary>
    internal const string InviteUnanswered = "invite-unanswered";

    /// <summary>A group loot roll the bot could not vote on (the vote was not dispatched, or the packet was unreadable).</summary>
    internal const string LootRollUncast = "loot-roll-uncast";

    /// <summary>The master's command word was dropped before it was carried out.</summary>
    internal const string CommandIgnored = "command-ignored";

    /// <summary>A reply the bot owed (the acknowledgement of a command) could not be sent.</summary>
    internal const string ReplyLost = "reply-lost";

    /// <summary>A dead party bot was neither resurrected nor back alive within <see cref="DeathRecoveryBudgetMs"/> after its wait.</summary>
    internal const string DeathUnrecovered = "death-unrecovered";

    /// <summary>The bot is still in a group after its master left for good (it should have left it).</summary>
    internal const string LeaderGoneStillGrouped = "leader-gone-still-grouped";

    /// <summary>A dead bot gets this long, after the wait for a resurrection, to be alive again (a corpse run across a continent is under ten minutes).</summary>
    internal const long DeathRecoveryBudgetMs = 15 * 60_000L;

    /// <summary>A pending invitation the bot answered must be gone within this long (transient states: the bot is between maps).</summary>
    internal const long InviteAnswerBudgetMs = 10_000;

    /// <summary>A bot grouped with no real player online, driven by nobody, gets this long before it counts as orphaned.</summary>
    internal const long OrphanBudgetMs = 5 * 60_000L;

    /// <summary>Observations further apart than this do not belong to one episode (<see cref="PlayerbotEpisode"/>).</summary>
    internal const long MaxGapMs = 60_000;

    /// <summary>The ledger key of <paramref name="code"/> (tests).</summary>
    internal static string Key(string code) => PlayerbotAnomalies.Key(Area, code);

    /// <summary>Report <paramref name="code"/> for <paramref name="bot"/> (failure path only).</summary>
    internal static void Report(Player bot, string code, string detail, AnomalySeverity severity = AnomalySeverity.Warning)
        => PlayerbotAnomalies.Report(bot, Area, code, detail, severity);

    internal static string Seconds(long ms) => (ms / 1000).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// <c>party/follower-far-behind</c> and <c>party/follower-other-map</c>: a follower that the catch-up rules should have brought to its
/// leader long ago. Fed by <see cref="PlayerbotFollower.Update"/> with the facts of the think (cost: a few comparisons).
/// <list type="bullet">
/// <item><description><b>far behind</b>: same map instance, leader in the world and not on a taxi, the bot not in combat and not told to
/// stay, farther than <c>max(FarBehindMinYards, TeleportDistanceYards + 20)</c> yards, for <see cref="FarBehindBudgetMs"/>. Teleport
/// mode would have teleported at TeleportDistanceYards; the margin keeps a follower that is mid-teleport or at the threshold quiet.</description></item>
/// <item><description><b>other map</b>: the leader on another map (or another instance of the bot's map id) that the bot may enter
/// (<see cref="PlayerbotFollowFacts.MasterMapAllowed"/>), the leader in the world (not between maps) and not on a taxi, the bot not
/// in combat, for <see cref="OtherMapBudgetMs"/>. A map the policy forbids is the operator's choice, not a failure.</description></item>
/// </list>
/// A leader teleport, a death (the party AI resets the follower) or any observation of the bot near its leader ends the episode.
/// </summary>
internal sealed class PlayerbotFollowWatch
{
    /// <summary>The least distance that counts as far behind.</summary>
    internal const float FarBehindMinYards = 120f;

    /// <summary>How far past the teleport distance a follower has to be before it counts (it would teleport at the distance itself).</summary>
    internal const float TeleportMarginYards = 20f;

    /// <summary>How long a follower may stay far behind.</summary>
    internal const long FarBehindBudgetMs = 120_000;

    /// <summary>How long a follower may stay on another map than its leader.</summary>
    internal const long OtherMapBudgetMs = 120_000;

    private PlayerbotEpisode _far;
    private PlayerbotEpisode _other;

    /// <summary>The distance past which a follower counts as far behind, given the configured teleport distance.</summary>
    internal static float FarThreshold(float teleportDistance)
        => float.IsFinite(teleportDistance) ? MathF.Max(FarBehindMinYards, teleportDistance + TeleportMarginYards) : FarBehindMinYards;

    /// <summary>Whether <paramref name="facts"/> describe a follower left far behind on the leader's map.</summary>
    internal static bool IsFarBehind(in PlayerbotFollowFacts facts)
        => facts.Mode != PlayerbotPartyMode.Stay && facts.MasterInWorld && !facts.MasterFlying && facts.SameMap && !facts.InCombat
            && float.IsFinite(facts.Distance) && facts.Distance > FarThreshold(facts.TeleportDistance);

    /// <summary>Whether <paramref name="facts"/> describe a follower on another map than a leader it may follow.</summary>
    internal static bool IsOnOtherMap(in PlayerbotFollowFacts facts)
        => facts.Mode != PlayerbotPartyMode.Stay && facts.MasterInWorld && !facts.MasterFlying && !facts.SameMap && facts.MasterMapAllowed
            && !facts.InCombat;

    /// <summary>Forget both episodes (the follower was reset: it stopped following, died, or was moved).</summary>
    internal void Reset()
    {
        _far.Reset();
        _other.Reset();
    }

    /// <summary>One think's observation. <paramref name="nowMs"/> is world uptime in milliseconds.</summary>
    internal void Observe(Player player, Player leader, in PlayerbotFollowFacts facts, PlayerbotFollowAction action, long nowMs)
    {
        if (_far.Observe(IsFarBehind(facts), nowMs, FarBehindBudgetMs, PlayerbotPartyAnomalies.MaxGapMs))
        {
            PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.FollowerFarBehind, string.Create(CultureInfo.InvariantCulture,
                $"leader {leader.Name} is {facts.Distance:F0} yd away on the same map for {PlayerbotPartyAnomalies.Seconds(_far.ElapsedMs(nowMs))}s (mode {facts.Mode}, catch-up {facts.CatchUp}, teleport distance {facts.TeleportDistance:F0} yd, may teleport {facts.TeleportToLeader}, last decision {action})"));
        }

        if (_other.Observe(IsOnOtherMap(facts), nowMs, OtherMapBudgetMs, PlayerbotPartyAnomalies.MaxGapMs))
        {
            string where = facts.MasterInOtherInstance ? $"another instance of map {leader.MapId}" : $"map {leader.MapId}";
            PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.FollowerOtherMap, string.Create(CultureInfo.InvariantCulture,
                $"leader {leader.Name} is on {where} and the bot is not, for {PlayerbotPartyAnomalies.Seconds(_other.ElapsedMs(nowMs))}s (mode {facts.Mode}, catch-up {facts.CatchUp}, may teleport {facts.TeleportToLeader}, entrance known {facts.EntranceToMaster}, last decision {action})"));
        }
    }
}

/// <summary>
/// <c>party/follow-dropped</c>: the leader is more than <see cref="FarYards"/> away on the bot's map, the bot is not in combat and not
/// told to stay, and the party AI has not called the follower at all for <see cref="NotFollowedMs"/> (something else - a rest, a loot
/// walk, a resurrection - kept the think), for <see cref="BudgetMs"/> in all. The follower itself is checked by
/// <see cref="PlayerbotFollowWatch"/>; this catches the follow that never ran. Cost: a mode compare and, when out of combat on the
/// leader's map, one distance.
/// </summary>
internal sealed class PlayerbotFollowDropWatch
{
    /// <summary>The leader farther than this is left behind by a bot that does not follow.</summary>
    internal const float FarYards = 60f;

    /// <summary>The follower must have been silent this long before an observation counts.</summary>
    internal const long NotFollowedMs = 45_000;

    /// <summary>How long the dropped follow may last.</summary>
    internal const long BudgetMs = 120_000;

    private PlayerbotEpisode _episode;
    private long _lastFollowMs;

    /// <summary>The party AI ran the follower (or engaged a master, the follow starts now) at <paramref name="nowMs"/>.</summary>
    internal void NoteFollow(long nowMs)
    {
        _lastFollowMs = nowMs;
        _episode.Reset();
    }

    /// <summary>The bot stopped being party-driven; the next engagement starts the watch afresh.</summary>
    internal void Reset() => _episode.Reset();

    /// <summary>Whether the observation is bad: the follow is on, the leader is far on this map, the follower was silent.</summary>
    internal static bool IsDropped(PlayerbotPartyMode mode, bool sameMap, bool inCombat, float distance, long silentMs)
        => mode != PlayerbotPartyMode.Stay && sameMap && !inCombat && float.IsFinite(distance) && distance > FarYards && silentMs > NotFollowedMs;

    /// <summary>One think's observation; true once per episode when the follow has been dropped for <see cref="BudgetMs"/>.</summary>
    internal bool Observe(PlayerbotPartyMode mode, bool sameMap, bool inCombat, Vector3 at, Vector3 leaderAt, long nowMs, out long droppedMs)
    {
        long silent = nowMs - _lastFollowMs;
        bool candidate = mode != PlayerbotPartyMode.Stay && sameMap && !inCombat && silent > NotFollowedMs;
        bool bad = candidate && IsDropped(mode, sameMap, inCombat, Vector3.Distance(at, leaderAt), silent);
        bool fire = _episode.Observe(bad, nowMs, BudgetMs, PlayerbotPartyAnomalies.MaxGapMs);
        droppedMs = _episode.ElapsedMs(nowMs);
        return fire;
    }
}
