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
}

/// <summary>The answer to <see cref="PlayerbotGroupCoordinator.Volunteer"/>: the outcome and, when noted, how many more the group still lacks.</summary>
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

    /// <summary>How long a volunteer's group holds its first invitation after forming, for the bot's whisper to arrive first.</summary>
    internal const uint VolunteerInviteHoldMs = 1_500;

    private const int MaxVolunteers = 256;

    private readonly Dictionary<ObjectGuid, (Guid Bot, (PlayerbotGroupGoalKind, uint) Key, uint Until)> _volunteers = [];

    /// <summary>
    /// World thread: <paramref name="player"/> asked <paramref name="botId"/>, which waits for partners, for an invitation. Noted when the
    /// bot still waits, the player is of its team, of the goal's level and free; the result says how many more the group still lacks.
    /// </summary>
    internal PlayerbotVolunteerResult Volunteer(Guid botId, Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!_waiting.TryGetValue(botId, out Waiting? waiting) || !_bots.TryGetValue(botId, out PlayerbotGroupBot bot) || bot.Session.Player is not { } wanting)
            return new(PlayerbotVolunteerOutcome.NotWaiting);
        if (player.Team != wanting.Team) return new(PlayerbotVolunteerOutcome.Team);
        if (player.Level < waiting.Goal.MinLevel) return new(PlayerbotVolunteerOutcome.Level);
        if (GroupManagerOrNull() is { } groups && (groups.GetGroup(player.Guid) is not null || groups.GetInvite(player.Guid) is not null))
            return new(PlayerbotVolunteerOutcome.Grouped);
        if (_declinedPlayers.ContainsKey(player.Guid)) return new(PlayerbotVolunteerOutcome.Declined);
        uint now = Now;
        if (_volunteers.Count >= MaxVolunteers)
        {
            foreach ((ObjectGuid key, (Guid, (PlayerbotGroupGoalKind, uint), uint Until) entry) in _volunteers)
                if (unchecked((int)(entry.Until - now)) <= 0) _volunteers.Remove(key);
        }
        if (_volunteers.Count >= MaxVolunteers) _volunteers.Clear();
        _volunteers[player.Guid] = (botId, waiting.Goal.Key, unchecked(now + VolunteerLifeMs));

        // The bots that can still form the goal with this team: what Match would see, so the answer is the one Match would act on.
        var sameGoal = new List<(Guid BotId, Waiting Waiting)>();
        foreach ((Guid id, Waiting w) in _waiting)
            if (w.Goal.Key == waiting.Goal.Key && _bots.TryGetValue(id, out PlayerbotGroupBot other) && other.Session.Player is { } otherPlayer && otherPlayer.Team == wanting.Team)
                sameGoal.Add((id, w));
        PlayerbotGroupGoal goal = waiting.Goal;
        if (goal.InInstance)
        {
            (Guid BotId, Waiting Waiting) anchor = sameGoal.Where(w => w.Waiting.Goal.Anchors).OrderBy(w => w.Waiting.SinceMs).FirstOrDefault();
            if (anchor.Waiting is not null) goal = anchor.Waiting.Goal;
        }

        int size = sameGoal.Max(w => w.Waiting.Goal.Size);
        List<PlayerbotGroupCandidate> candidates = BuildCandidates(sameGoal, goal);
        if (candidates.Exists(c => c.BotId == botId) && PlanForVolunteer(player, candidates, size, goal, botId) is { } plan && (!goal.InInstance || PlanAnchored(plan)))
        {
            _volunteers.Remove(player.Guid);
            // A group with a real player takes no slot from the bots' cap: it forms now and the leader invites on the forming ticks, after a
            // short hold so the bot's "inv in a sec" whisper reaches the player before the invitation does.
            BotGroup formed = Form(goal with { Size = size }, plan);
            formed.InviteHoldUntilMs = unchecked(Now + VolunteerInviteHoldMs);
            return new(PlayerbotVolunteerOutcome.Ready);
        }

        return new(PlayerbotVolunteerOutcome.Noted, Math.Max(1, size - candidates.Count - 1));
    }

    /// <summary>
    /// A dry-run match of <paramref name="candidates"/> plus <paramref name="real"/>: the plan, or null when the player is not on the goal's
    /// map, not alive, too far from the meeting point or of the wrong level, or the plan would leave them out or let them lead. The player is
    /// placed ahead of surplus bots: the first try holds only <paramref name="size"/> - 1 bots (the volunteer's own bot first, then the longest
    /// waiting), so the group cannot fill with bots alone; the second try is the whole list (the roles may need a bot the first left out).
    /// </summary>
    private PlayerbotGroupPlan? PlanForVolunteer(Player real, List<PlayerbotGroupCandidate> candidates, int size, PlayerbotGroupGoal goal, Guid preferredBot)
    {
        if (candidates.Count == 0 || !real.IsAlive || real.MapId != goal.MeetingMap || real.Level < goal.MinLevel) return null;
        if (Vector3.Distance(new Vector3(real.X, real.Y, real.Z), goal.Meeting) > MaxMeetingYards) return null;
        var self = new PlayerbotGroupCandidate(Guid.Empty, real.Guid.Value, real.Name, real.Class, real.Level, (uint)real.Team, real.MapId,
            new Vector3(real.X, real.Y, real.Z), PlayerbotRoles.Assign(real.Class, static _ => false), Gear(real), uint.MaxValue, Qualifying: PlayerbotGroupContent.QualifiesAtLevel(goal, real.Level));

        if (candidates.Count >= size)
        {
            uint baseline = Oldest(candidates).NeedSinceMs;
            List<PlayerbotGroupCandidate> held = [.. candidates
                .OrderBy(c => c.BotId == preferredBot ? long.MinValue : unchecked((int)(c.NeedSinceMs - baseline)))
                .ThenBy(c => c.BotId).Take(size - 1)];
            if (VolunteerPlan(held, self, size, goal) is { } first) return first;
        }

        return VolunteerPlan(candidates, self, size, goal);
    }

    private PlayerbotGroupPlan? VolunteerPlan(List<PlayerbotGroupCandidate> bots, PlayerbotGroupCandidate self, int size, PlayerbotGroupGoal goal)
    {
        var withPlayer = new List<PlayerbotGroupCandidate>(bots.Count + 1);
        withPlayer.AddRange(bots);
        withPlayer.Add(self);
        if (PlayerbotGroupContent.Match(withPlayer, size, _options.Groups, goal.InInstance) is not { } plan || !plan.Members.Any(m => m.Bot.BotId == Guid.Empty)) return null;
        if (plan.Leader.BotId != Guid.Empty) return plan;

        // A real player never leads a bot group: the bots keep their goal. A warrior volunteer is dealt the tank role when no bot tanks and so
        // would lead; when the group asks for no tank, the player takes a damage slot instead and the longest-waiting bot leads.
        if (PlayerbotGroupContent.RoleNeeds(size, _options.Groups).Tanks > 0) return null;
        var members = new List<(PlayerbotGroupCandidate Bot, PlayerbotGroupRole Role, byte SubGroup)>(plan.Members.Count);
        var botMembers = new List<PlayerbotGroupCandidate>(plan.Members.Count);
        foreach ((PlayerbotGroupCandidate member, PlayerbotGroupRole role, byte subGroup) in plan.Members)
        {
            bool real = member.BotId == Guid.Empty;
            members.Add((member, real && role == PlayerbotGroupRole.Tank ? PlayerbotGroupRole.Damage : role, subGroup));
            if (!real) botMembers.Add(member);
        }

        return botMembers.Count == 0 ? null : new PlayerbotGroupPlan(Oldest(botMembers), members);
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
    /// meeting point. The volunteer is forgotten once matched.
    /// </summary>
    private PlayerbotGroupPlan? MatchWithVolunteer(List<PlayerbotGroupCandidate> candidates, int size, PlayerbotGroupGoal goal, GroupManager groupManager)
    {
        if (_volunteers.Count == 0 || _world is not { } world) return null;
        if (PlayerbotGroupContent.QualifyingAnchor(candidates) is not { } oldest || _bots[oldest.BotId].Session.Player is not { } anchor) return null;
        uint now = Now;
        foreach ((ObjectGuid guid, (Guid volunteerBot, (PlayerbotGroupGoalKind, uint) key, uint until)) in _volunteers)
        {
            if (unchecked((int)(until - now)) <= 0) continue; // PruneVolunteers drops it at the next pass

            if (key != goal.Key || world.FindOnlinePlayer(guid) is not { IsAlive: true } real || real.Session is not WorldSession { IsManaged: false }) continue;
            if (real.Team != anchor.Team || groupManager.GetGroup(real.Guid) is not null || groupManager.GetInvite(real.Guid) is not null) continue;
            if (_declinedPlayers.ContainsKey(real.Guid)) continue;
            if (PlanForVolunteer(real, candidates, size, goal, volunteerBot) is { } plan)
            {
                _volunteers.Remove(guid);
                return plan;
            }
        }

        return null;
    }
}
