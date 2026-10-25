using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Combat;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// Real players (docs/areas/playbots-groups.md, Cooperation): which player a group short of a role or a member may invite
/// (<see cref="PlayerbotGroupOptions.InvitePlayers"/>), and whether a player is on a bot's goal (<see cref="SharesGoal(Guid, Player)"/>,
/// which the party intake reads for <see cref="PlayerbotInvitePolicy.Affinity"/>). World thread only.
/// </summary>
public sealed partial class PlayerbotGroupCoordinator
{
    /// <summary>The chat safety state (the players' AI chat choice, set by the feature; null without a chat service: nobody has opted in).</summary>
    internal PlayerbotChatSafety? ChatSafety { get; set; }

    /// <summary>The "invites off" store (set by the social feature; null without it: nobody has opted out). An opted-out player is never invited unasked; a volunteer (who asked) still is.</summary>
    internal BotSocial.PlayerbotInviteOptOut? InviteOptOut { get; set; }

    /// <summary>A player invited by the bots, until when they are not invited again (<see cref="PlayerbotGroupOptions.InvitePlayerCooldownMinutes"/>).</summary>
    private readonly Dictionary<ObjectGuid, uint> _invitedPlayers = [];

    /// <summary>Whether the bots invited <paramref name="player"/> within the last <see cref="PlayerbotGroupOptions.InvitePlayerCooldownMinutes"/>.</summary>
    internal bool InvitedRecently(ObjectGuid player) => _invitedPlayers.ContainsKey(player);

    /// <summary>
    /// Whether <paramref name="other"/> is on the goal the bot has: the one it waits for or its group works on, else its own quest.
    /// A quest goal is shared when the player holds that quest unfinished (or one asking for the goal's creature); a dungeon goal when the
    /// player is inside that dungeon's instance, or on the meeting map within
    /// <see cref="PlayerbotCooperationOptions.SameGoalMeetingRadiusYards"/> of the meeting point (the entrance) and in level range of the bot
    /// (a stranger elsewhere on the continent is not on the goal).
    /// </summary>
    internal bool SharesGoal(Guid botId, Player other)
    {
        Player? self = _bots.TryGetValue(botId, out PlayerbotGroupBot found) ? found.Session.Player : null;
        if (_waiting.TryGetValue(botId, out Waiting? waiting)) return SharesGoal(waiting.Goal, other, self);
        if (_memberOf.TryGetValue(botId, out BotGroup? group)) return SharesGoal(group.Goal, other, self);
        if (_bots.TryGetValue(botId, out PlayerbotGroupBot bot) && bot.Brain.QuestId != 0 && QuestState(other) is { } state)
            return state.Quests.Get(bot.Brain.QuestId) is { Status: QuestStatus.Incomplete };
        return false;
    }

    /// <param name="goal">The goal.</param>
    /// <param name="other">The player asked about.</param>
    /// <param name="anchor">The bot whose goal it is (the level range of a dungeon's entrance is measured from it); null reads as the goal's minimum level alone.</param>
    private bool SharesGoal(PlayerbotGroupGoal goal, Player other, Player? anchor)
    {
        if (goal.InInstance) return other.MapId == goal.MapId || AtEntrance(goal, other, anchor);
        if (goal.QuestId != 0 && QuestState(other) is { } state && state.Quests.Get(goal.QuestId) is { Status: QuestStatus.Incomplete }) return true;
        return goal.ObjectiveEntry != 0 && QuestFor(other, goal.ObjectiveEntry) != 0;
    }

    /// <summary>
    /// A dungeon goal's meeting point is on the continent: a player there, within <see cref="PlayerbotCooperationOptions.SameGoalMeetingRadiusYards"/>
    /// of the entrance, at least the goal's minimum level and within <see cref="PlayerbotGroupOptions.LevelRange"/> of <paramref name="anchor"/>.
    /// </summary>
    private bool AtEntrance(PlayerbotGroupGoal goal, Player other, Player? anchor)
    {
        if (other.MapId != goal.MeetingMap || other.Level < goal.MinLevel) return false;
        if (anchor is not null && Math.Abs(other.Level - anchor.Level) > _options.Groups.LevelRange) return false;
        float radius = Coop.SameGoalMeetingRadiusYards;
        return Vector3.DistanceSquared(new Vector3(other.X, other.Y, other.Z), goal.Meeting) <= radius * radius;
    }

    /// <summary>
    /// <see cref="PlayerbotGroupOptions.InvitePlayers"/>: a group one short (a role or a member) may take a nearby real player of the same
    /// team, ungrouped, of a fitting level, on the group's goal, who has not declined lately, who opted in to AI chat or has affinity of
    /// at least <see cref="PlayerbotGroupOptions.InvitePlayerAffinity"/> toward a waiting bot (read through <c>EffectiveAffinity</c>),
    /// who is no rival or NoWhispers player of any of them, and who was not invited in the last
    /// <see cref="PlayerbotGroupOptions.InvitePlayerCooldownMinutes"/>. A player whose remembered role fills the missing tank or healer
    /// comes first, then one whose class can, then the nearest.
    /// </summary>
    private PlayerbotGroupPlan? MatchWithPlayer(List<PlayerbotGroupCandidate> candidates, int size, PlayerbotGroupGoal goal, GroupManager groupManager)
    {
        PlayerbotGroupCandidate oldest = candidates.OrderBy(c => c.NeedSinceMs).First();
        if (_bots[oldest.BotId].Session.Player is not { Map: { } map } anchor) return null;
        uint now = Now;
        if (_invitedPlayers.Count > 0)
            foreach ((ObjectGuid key, uint until) in _invitedPlayers)
                if (unchecked((int)(until - now)) <= 0) _invitedPlayers.Remove(key);

        PlayerbotGroupOptions groups = _options.Groups;
        (int missingTanks, int missingHealers) = PlayerbotRecruitment.MissingRoles(candidates, size, groups);
        var eligible = new List<(Player Real, int Rank, float DistanceSq)>();
        foreach (ObjectGuid guid in anchor.VisibleObjects)
        {
            if (map.FindObject(guid) is not Player { IsAlive: true } real || real.Session is not WorldSession { IsManaged: false }) continue;
            if (real.Team != anchor.Team || groupManager.GetGroup(real.Guid) is not null || groupManager.GetInvite(real.Guid) is not null) continue;
            if (_declinedPlayers.ContainsKey(real.Guid) || _invitedPlayers.ContainsKey(real.Guid) || real.Level < goal.MinLevel) continue;
            float distanceSq = Vector3.DistanceSquared(new Vector3(real.X, real.Y, real.Z), new Vector3(anchor.X, anchor.Y, anchor.Z));
            if (distanceSq > InvitePlayerYards * InvitePlayerYards || !SharesGoal(goal, real, anchor)) continue;
            if (!PlayerWelcome(candidates, real, groups, out PlayerbotRelationFlags role)) continue;
            bool remembered = (role.HasFlag(PlayerbotRelationFlags.PrefersTank) && missingTanks > 0 && PlayerbotGroupContent.CanTank(real.Class))
                || (role.HasFlag(PlayerbotRelationFlags.PrefersHealer) && missingHealers > 0 && PlayerbotGroupContent.CanHeal(real.Class));
            bool capable = (missingTanks > 0 && PlayerbotGroupContent.CanTank(real.Class)) || (missingHealers > 0 && PlayerbotGroupContent.CanHeal(real.Class));
            eligible.Add((real, remembered ? 0 : capable ? 1 : 2, distanceSq));
        }

        foreach ((Player real, _, _) in eligible.OrderBy(e => e.Rank).ThenBy(e => e.DistanceSq).ThenBy(e => e.Real.Guid.Value))
        {
            var withPlayer = new List<PlayerbotGroupCandidate>(candidates)
            {
                new(Guid.Empty, real.Guid.Value, real.Name, real.Class, real.Level, (uint)real.Team, real.MapId,
                    new Vector3(real.X, real.Y, real.Z), PlayerbotRoles.Assign(real.Class, _ => false), Gear(real), uint.MaxValue),
            };
            // A real player never leads a bot group: the bots keep their goal.
            if (PlayerbotGroupContent.Match(withPlayer, size, groups) is { } plan && plan.Leader.BotId != Guid.Empty
                && plan.Members.Any(m => m.Bot.BotId == Guid.Empty))
            {
                _invitedPlayers[real.Guid] = unchecked(now + (uint)(groups.InvitePlayerCooldownMinutes * 60_000));
                return plan;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the waiting bots would invite <paramref name="real"/>: no waiting bot holds them as a rival or flagged NoWhispers, and they
    /// either opted in to AI chat or are liked enough by one of the bots; never a player who said "invites off" (<see cref="InviteOptOut"/>).
    /// <paramref name="role"/> receives the role flags the bots remember.
    /// </summary>
    private bool PlayerWelcome(List<PlayerbotGroupCandidate> bots, Player real, PlayerbotGroupOptions groups, out PlayerbotRelationFlags role)
    {
        role = PlayerbotRelationFlags.None;
        if (InviteOptOut?.IsOptedOut(real.Guid.Value, real.AccountId) == true) return false; // "invites off": never unasked (a volunteer asked, and is matched in MatchWithVolunteer)
        bool liked = ChatSafety?.Player(real.Name)?.AiReplies == true;
        if (Social?.Memory is not { } memory) return liked;
        const PlayerbotRelationFlags roles = PlayerbotRelationFlags.PrefersTank | PlayerbotRelationFlags.PrefersHealer | PlayerbotRelationFlags.PrefersDamage;
        foreach (PlayerbotGroupCandidate bot in bots)
        {
            if (!memory.TryGet(bot.BotId, real.Guid.Value, out PlayerbotRelation relation)) continue;
            // NoWhispers is a normal-layer flag on purpose: a player the bot has muted ("stop whispering me") is not invited into its group
            // either; it is the player's own say over contact, not a War Mode or rival state.
            if (memory.IsRival(relation) || relation.Flags.HasFlag(PlayerbotRelationFlags.NoWhispers)) return false;
            if (memory.EffectiveAffinity(relation) >= groups.InvitePlayerAffinity) liked = true;
            role |= relation.Flags & roles;
        }

        return liked;
    }
}
