using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>A member as the successor choice sees it: who, in which role, how high a level, and whether it lives.</summary>
internal readonly record struct PlayerbotSuccessorCandidate(Guid BotId, PlayerbotGroupRole Role, byte Level, bool Alive);

/// <summary>Who takes over a bot-led group whose leader died: a living tank, else a living healer, else living damage; the higher level first.</summary>
internal static class PlayerbotGroupLeadership
{
    /// <summary>The best living candidate, or null when nobody lives. A pure function: ties keep the earlier candidate.</summary>
    internal static Guid? BestSuccessor(IEnumerable<PlayerbotSuccessorCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        PlayerbotSuccessorCandidate? best = null;
        foreach (PlayerbotSuccessorCandidate candidate in candidates)
        {
            if (!candidate.Alive) continue;
            if (best is not { } current || Rank(candidate.Role) < Rank(current.Role)
                || Rank(candidate.Role) == Rank(current.Role) && candidate.Level > current.Level)
                best = candidate;
        }

        return best?.BotId;
    }

    private static int Rank(PlayerbotGroupRole role) => role switch
    {
        PlayerbotGroupRole.Tank => 0,
        PlayerbotGroupRole.Healer => 1,
        _ => 2,
    };
}

public sealed partial class PlayerbotGroupCoordinator
{
    /// <summary>
    /// Pass the lead of a bot-led group from its dead leader to the best living bot member with the leader's own CMSG_GROUP_SET_LEADER (the
    /// server has no alive check on it, as the client has none), then follow the server group's choice: the group's leader bot and guid change
    /// only once the server group says the new leader leads. False when nobody can take the lead or the server refused. World thread only.
    /// </summary>
    internal bool PromoteLeader(BotGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (_world is not { } world || GroupManagerOrNull() is not { } groups
            || !_bots.TryGetValue(group.LeaderBotId, out PlayerbotGroupBot old) || old.Session.Player is not { } oldLeader
            || groups.GetGroup(oldLeader.Guid) is not { } server || !server.IsLeader(oldLeader.Guid)) return false;

        var candidates = new List<PlayerbotSuccessorCandidate>();
        foreach (Member member in group.Members)
        {
            if (member.Real || member.BotId == group.LeaderBotId || !member.Joined) continue;
            if (world.FindOnlinePlayer(member.Guid) is not { } player || !server.IsMember(member.Guid)) continue;
            candidates.Add(new PlayerbotSuccessorCandidate(member.BotId, member.Role, player.Level, player.IsAlive));
        }

        if (PlayerbotGroupLeadership.BestSuccessor(candidates) is not { } successor || group.Find(successor) is not { } chosen) return false;
        Act(old.Session, WorldOpcode.CmsgGroupSetLeader, BitConverter.GetBytes(chosen.Guid.Value), budgeted: false);
        if (!server.IsLeader(chosen.Guid)) return false;
        group.LeaderBotId = chosen.BotId;
        group.LeaderGuid = chosen.Guid;
        Note($"group {group.Id} leader {oldLeader.Name} fell; {chosen.Name} leads");
        return true;
    }
}
