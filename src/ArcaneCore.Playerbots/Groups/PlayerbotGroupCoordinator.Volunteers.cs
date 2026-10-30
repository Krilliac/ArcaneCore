using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>How a real player's offer to join a waiting bot's group went (<see cref="PlayerbotGroupCoordinator.Volunteer"/>).</summary>
internal enum PlayerbotVolunteerOutcome : byte
{
    /// <summary>Noted: the player is matched (and invited through the leader's ordinary CMSG_GROUP_INVITE) once the group can form.</summary>
    Noted,

    /// <summary>The bot waits for no group (any more).</summary>
    NotWaiting,

    /// <summary>The player is of the other faction.</summary>
    Team,

    /// <summary>The player is below the goal's level (the entrance level, or the quest's).</summary>
    Level,

    /// <summary>The player is already in a group or has an invitation pending.</summary>
    Grouped,

    /// <summary>The player declined one of the bots' invitations lately.</summary>
    Declined,

    /// <summary>Ready: a dry-run match over the bot's real candidates includes the player, so the group forms and the invitation follows.</summary>
    Ready,

    /// <summary>
    /// The coordinator cannot place the player in this group (too far from the meeting point, on another map, dead, out of the level range
    /// of the bots, an instance goal without an anchor, or the asked bot is no candidate itself). Nothing is kept and no invitation follows.
    /// </summary>
    CannotPlace,
}

/// <summary>The answer to <see cref="PlayerbotGroupCoordinator.Volunteer"/>: the outcome and, when noted, how many more the group still lacks (always above 0).</summary>
internal readonly record struct PlayerbotVolunteerResult(PlayerbotVolunteerOutcome Outcome, int Missing = 0);

/// <summary>
/// Real players who answered a bot's LFG or LFM call ("inv", docs/areas/playbots.md, Bot voice): a volunteer is kept for its goal for
/// <see cref="VolunteerLifeMs"/> and matched like the nearby player of <see cref="PlayerbotGroupOptions.InvitePlayers"/>, but from anywhere
/// on the meeting map within <see cref="MaxMeetingYards"/> of the meeting point and whatever that option says (the player asked). The
/// leader then invites through the ordinary CMSG_GROUP_INVITE; a real player never leads.
/// </summary>
public sealed partial class PlayerbotGroupCoordinator
{
    /// <summary>How long a volunteer stays wanted (five minutes of world time).</summary>
    internal const uint VolunteerLifeMs = 300_000;

    private const int MaxVolunteers = 256;

    private readonly Dictionary<ObjectGuid, (Guid Bot, (PlayerbotGroupGoalKind, uint) Key, uint Until)> _volunteers = [];

    /// <summary>
    /// World thread: <paramref name="player"/> asked <paramref name="botId"/>, which waits for partners, for an invitation. Ready when a
    /// dry-run match over the bot's real candidates includes the player (the group then forms at once); Noted, with the true number the
    /// group still lacks (above 0), when the player could be placed but the bots are too few: the player is kept until they are enough;
    /// CannotPlace when the coordinator could not place the player however many bots come (nothing is kept).
    /// </summary>
    internal PlayerbotVolunteerResult Volunteer(Guid botId, Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!_waiting.TryGetValue(botId, out Waiting? waiting) || !_bots.TryGetValue(botId, out PlayerbotGroupBot bot) || bot.Session.Player is not { } wanting)
            return new(PlayerbotVolunteerOutcome.NotWaiting);
        if (player.Team != wanting.Team) return new(PlayerbotVolunteerOutcome.Team);
        if (player.Level < waiting.Goal.MinLevel) return new(PlayerbotVolunteerOutcome.Level);
        if ((GroupManagerOrNull() is { } groups && (groups.GetGroup(player.Guid) is not null || groups.GetInvite(player.Guid) is not null)) || IsAwaitedMember(player.Guid))
            return new(PlayerbotVolunteerOutcome.Grouped);
        if (_declinedPlayers.ContainsKey(player.Guid)) return new(PlayerbotVolunteerOutcome.Declined);

        // The bots that can still form the goal with this team: what Match would see, so the answer is the one Match would act on.
        var sameGoal = new List<(Guid BotId, Waiting Waiting)>();
        foreach ((Guid id, Waiting w) in _waiting)
            if (w.Goal.Key == waiting.Goal.Key && _bots.TryGetValue(id, out PlayerbotGroupBot other) && other.Session.Player is { } otherPlayer && otherPlayer.Team == wanting.Team)
                sameGoal.Add((id, w));
        PlayerbotGroupGoal goal = waiting.Goal;
        if (goal.InInstance)
        {
            (Guid BotId, Waiting Waiting) anchor = sameGoal.Where(w => w.Waiting.Goal.Anchors).OrderBy(w => w.Waiting.SinceMs).FirstOrDefault();
            if (anchor.Waiting is null) return new(PlayerbotVolunteerOutcome.CannotPlace);
            goal = anchor.Waiting.Goal;
        }

        int size = sameGoal.Max(w => w.Waiting.Goal.Size);
        List<PlayerbotGroupCandidate> candidates = BuildCandidates(sameGoal, goal);
        if (!CanReach(player, goal) || !candidates.Exists(c => c.BotId == botId)) return new(PlayerbotVolunteerOutcome.CannotPlace);
        if (PlanForVolunteer(player, candidates, size, goal, botId) is { } plan && (!goal.InInstance || PlanAnchored(plan)))
        {
            // A group with a real player takes no slot from the bots' cap: it forms now and the leader invites on the forming ticks.
            _volunteers.Remove(player.Guid);
            Form(goal with { Size = size }, plan);
            return new(PlayerbotVolunteerOutcome.Ready);
        }

        // Too few bots is the one reason that time can mend; any other (the level range, the roles) is not promised away.
        if (candidates.Count + 1 >= size) return new(PlayerbotVolunteerOutcome.CannotPlace);
        uint now = Now;
        if (_volunteers.Count >= MaxVolunteers)
        {
            foreach ((ObjectGuid key, var entry) in _volunteers)
                if (unchecked((int)(entry.Until - now)) <= 0) _volunteers.Remove(key);
        }
        if (_volunteers.Count >= MaxVolunteers) _volunteers.Clear();
        _volunteers[player.Guid] = (botId, waiting.Goal.Key, unchecked(now + VolunteerLifeMs));
        return new(PlayerbotVolunteerOutcome.Noted, size - candidates.Count - 1);
    }

    /// <summary>Whether <paramref name="real"/> is alive, on the goal's meeting map, of its level and within <see cref="MaxMeetingYards"/> of the meeting point.</summary>
    private static bool CanReach(Player real, PlayerbotGroupGoal goal)
        => real.IsAlive && real.MapId == goal.MeetingMap && real.Level >= goal.MinLevel
           && Vector3.Distance(new Vector3(real.X, real.Y, real.Z), goal.Meeting) <= MaxMeetingYards;

    /// <summary>
    /// A dry-run match of <paramref name="candidates"/> plus <paramref name="real"/>: the plan, or null when the player cannot be reached
    /// (<see cref="CanReach"/>) or no plan includes them. A bot always leads. The player is placed ahead of surplus bots: the first tries
    /// hold only <paramref name="size"/> - 1 bots (the volunteer's own bot first, then the longest waiting), so the group cannot fill with
    /// bots alone, then the whole list. The player is first tried as a damage dealer, so a bot tanks and heals when one can; only when
    /// none can does the player's own class count (and may fill the tank or healer the group asks for, with a bot still leading).
    /// </summary>
    private PlayerbotGroupPlan? PlanForVolunteer(Player real, List<PlayerbotGroupCandidate> candidates, int size, PlayerbotGroupGoal goal, Guid preferredBot)
    {
        if (candidates.Count == 0 || !CanReach(real, goal)) return null;
        var self = new PlayerbotGroupCandidate(Guid.Empty, real.Guid.Value, real.Name, real.Class, real.Level, (uint)real.Team, real.MapId,
            new Vector3(real.X, real.Y, real.Z), PlayerbotRoles.Assign(real.Class, static _ => false), Gear(real), uint.MaxValue, Qualifying: PlayerbotGroupContent.QualifiesAtLevel(goal, real.Level));
        PlayerbotGroupCandidate damageOnly = self with { Class = Class.Mage, TalentRole = PlayerbotRole.RangeDps };

        var lists = new List<List<PlayerbotGroupCandidate>>(2);
        if (candidates.Count >= size)
        {
            uint baseline = Oldest(candidates).NeedSinceMs;
            lists.Add([.. candidates
                .OrderBy(c => c.BotId == preferredBot ? long.MinValue : unchecked((int)(c.NeedSinceMs - baseline)))
                .ThenBy(c => c.BotId).Take(size - 1)]);
        }

        lists.Add(candidates);
        foreach (PlayerbotGroupCandidate asked in new[] { damageOnly, self })
            foreach (List<PlayerbotGroupCandidate> bots in lists)
                if (VolunteerPlan(bots, asked, self, size, goal) is { } plan && (!goal.InInstance || PlanAnchored(plan))) return plan; // an instance plan needs a waiting anchor: try the next list
        return null;
    }

    private PlayerbotGroupPlan? VolunteerPlan(List<PlayerbotGroupCandidate> bots, PlayerbotGroupCandidate asked, PlayerbotGroupCandidate self, int size, PlayerbotGroupGoal goal)
    {
        var withPlayer = new List<PlayerbotGroupCandidate>(bots.Count + 1);
        withPlayer.AddRange(bots);
        withPlayer.Add(asked);
        if (PlayerbotGroupContent.Match(withPlayer, size, _options.Groups, goal.InInstance) is not { } plan || !plan.Members.Any(m => m.Bot.BotId == Guid.Empty)) return null;

        // The plan names the player by the candidate that was asked (a damage dealer first); the member is the player. A real player never
        // leads a bot group: when the player's own class took the only tank slot the longest-waiting bot leads, the player keeps the role.
        var members = new List<(PlayerbotGroupCandidate Bot, PlayerbotGroupRole Role, byte SubGroup)>(plan.Members.Count);
        foreach ((PlayerbotGroupCandidate member, PlayerbotGroupRole role, byte subGroup) in plan.Members)
            members.Add((member.BotId == Guid.Empty ? self : member, role, subGroup));
        return WithBotLeader(new PlayerbotGroupPlan(plan.Leader, members));
    }

    /// <summary>
    /// Whether <paramref name="guid"/> is a real player a bot group has already taken whose invitation has not been answered (the group formed,
    /// the leader invites on the forming ticks): they are as engaged as one with an invitation pending, though the group manager does not know yet.
    /// </summary>
    private bool IsAwaitedMember(ObjectGuid guid)
    {
        foreach (BotGroup group in _groups)
            foreach (Member member in group.Members)
                if (member.Real && !member.Joined && member.Guid == guid) return true;
        return false;
    }

    /// <summary>
    /// <paramref name="plan"/> with a bot as its leader: the plan itself when a bot leads, else the longest-waiting bot member leads (the
    /// real player, dealt the only tank slot, keeps the role); a healer bot is preferred over the others, since it stays behind the tank and
    /// has the least to do with the pull. Null when no bot is in the plan. A real player never leads a bot group.
    /// </summary>
    private static PlayerbotGroupPlan? WithBotLeader(PlayerbotGroupPlan plan)
    {
        if (plan.Leader.BotId != Guid.Empty) return plan;
        var bots = new List<PlayerbotGroupCandidate>(plan.Members.Count);
        var healers = new List<PlayerbotGroupCandidate>(plan.Members.Count);
        foreach ((PlayerbotGroupCandidate member, PlayerbotGroupRole role, _) in plan.Members)
        {
            if (member.BotId == Guid.Empty) continue;
            bots.Add(member);
            if (role == PlayerbotGroupRole.Healer) healers.Add(member);
        }

        return bots.Count == 0 ? null : new PlayerbotGroupPlan(Oldest(healers.Count > 0 ? healers : bots), plan.Members);
    }

    private readonly List<(Guid BotId, ObjectGuid Player)> _expiredVolunteers = [];

    /// <summary>
    /// The volunteers whose offer ran out unplaced and who are still ungrouped: (bot, player). The voice has the bot whisper one honest line
    /// to each, rate-limited like any reply (world thread).
    /// </summary>
    internal IReadOnlyList<(Guid BotId, ObjectGuid Player)> TakeExpiredVolunteers()
    {
        if (_expiredVolunteers.Count == 0) return [];
        (Guid, ObjectGuid)[] taken = [.. _expiredVolunteers];
        _expiredVolunteers.Clear();
        return taken;
    }

    private void PruneVolunteers(uint now)
    {
        if (_volunteers.Count == 0) return;
        GroupManager? groups = GroupManagerOrNull();
        foreach ((ObjectGuid guid, var entry) in _volunteers)
        {
            if (unchecked((int)(entry.Until - now)) > 0) continue;
            _volunteers.Remove(guid);
            if (groups is not null && groups.GetGroup(guid) is null && groups.GetInvite(guid) is null && _expiredVolunteers.Count < 64) _expiredVolunteers.Add((entry.Bot, guid));
        }
    }

    /// <summary>Whether <paramref name="player"/> volunteered for a goal with <paramref name="key"/> and still may be matched (tests and status).</summary>
    internal bool IsVolunteer(ObjectGuid player, (PlayerbotGroupGoalKind, uint) key)
        => _volunteers.TryGetValue(player, out var entry) && entry.Key == key && unchecked((int)(entry.Until - Now)) > 0;

    /// <summary>
    /// <see cref="Match"/>'s volunteer step: a group one short (a role or a member) takes a volunteer for its goal (<see cref="Volunteer"/>)
    /// who is online, alive, of the team, ungrouped, of the level and on the meeting map within <see cref="MaxMeetingYards"/> of the
    /// meeting point. The volunteer stays kept until the caller forms the plan and forgets <paramref name="volunteer"/>: a plan the caller
    /// rejects (an instance plan without an anchor) loses nobody.
    /// </summary>
    private PlayerbotGroupPlan? MatchWithVolunteer(List<PlayerbotGroupCandidate> candidates, int size, PlayerbotGroupGoal goal, GroupManager groupManager, out ObjectGuid? volunteer)
    {
        volunteer = null;
        if (_volunteers.Count == 0 || _world is not { } world) return null;
        if (PlayerbotGroupContent.QualifyingAnchor(candidates) is not { } oldest || _bots[oldest.BotId].Session.Player is not { } anchor) return null;
        uint now = Now;
        foreach ((ObjectGuid guid, (Guid volunteerBot, (PlayerbotGroupGoalKind, uint) key, uint until)) in _volunteers)
        {
            if (unchecked((int)(until - now)) <= 0) continue; // PruneVolunteers drops it at the next pass

            if (key != goal.Key || world.FindOnlinePlayer(guid) is not { IsAlive: true } real || real.Session is not WorldSession { IsManaged: false }) continue;
            if (real.Team != anchor.Team || groupManager.GetGroup(real.Guid) is not null || groupManager.GetInvite(real.Guid) is not null || IsAwaitedMember(real.Guid)) continue;
            if (_declinedPlayers.ContainsKey(real.Guid)) continue;
            if (PlanForVolunteer(real, candidates, size, goal, volunteerBot) is { } plan)
            {
                volunteer = guid;
                return plan;
            }
        }

        return null;
    }

    /// <summary>Whether a volunteer for the goal <paramref name="key"/> is kept and not yet expired, asked of a bot of <paramref name="team"/>.</summary>
    private bool HasLiveVolunteer((PlayerbotGroupGoalKind, uint) key, Team team)
    {
        uint now = Now;
        foreach (var entry in _volunteers.Values)
            if (entry.Key == key && unchecked((int)(entry.Until - now)) > 0
                && _bots.TryGetValue(entry.Bot, out PlayerbotGroupBot bot) && bot.Session.Player is { } asked && asked.Team == team) return true;
        return false;
    }
}
