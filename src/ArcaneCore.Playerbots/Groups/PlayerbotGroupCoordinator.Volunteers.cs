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
        int waitingForGoal = 0;
        foreach (Waiting w in _waiting.Values)
            if (w.Goal.Key == waiting.Goal.Key) waitingForGoal++;
        return new(PlayerbotVolunteerOutcome.Noted, Math.Max(0, waiting.Goal.Size - waitingForGoal - 1));
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
        foreach ((ObjectGuid guid, (Guid _, (PlayerbotGroupGoalKind, uint) key, uint until)) in _volunteers)
        {
            if (unchecked((int)(until - now)) <= 0)
            {
                _volunteers.Remove(guid);
                continue;
            }

            if (key != goal.Key || world.FindOnlinePlayer(guid) is not { IsAlive: true } real || real.Session is not WorldSession { IsManaged: false }) continue;
            if (real.Team != anchor.Team || groupManager.GetGroup(real.Guid) is not null || groupManager.GetInvite(real.Guid) is not null) continue;
            if (_declinedPlayers.ContainsKey(real.Guid) || real.Level < goal.MinLevel || real.MapId != goal.MeetingMap) continue;
            if (Vector3.Distance(new Vector3(real.X, real.Y, real.Z), goal.Meeting) > MaxMeetingYards) continue;
            var withPlayer = new List<PlayerbotGroupCandidate>(candidates.Count + 1);
            withPlayer.AddRange(candidates);
            withPlayer.Add(new(Guid.Empty, real.Guid.Value, real.Name, real.Class, real.Level, (uint)real.Team, real.MapId,
                new Vector3(real.X, real.Y, real.Z), PlayerbotRoles.Assign(real.Class, static _ => false), Gear(real), uint.MaxValue, Qualifying: PlayerbotGroupContent.QualifiesAtLevel(goal, real.Level)));
            if (PlayerbotGroupContent.Match(withPlayer, size, _options.Groups, goal.InInstance) is { } plan && plan.Leader.BotId != Guid.Empty
                && plan.Members.Any(m => m.Bot.BotId == Guid.Empty))
            {
                _volunteers.Remove(guid);
                return plan;
            }
        }

        return null;
    }
}
