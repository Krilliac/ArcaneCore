using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>Why a bot may fight a player it would not otherwise start on (<see cref="PlayerbotHostility.MayEngage"/>).</summary>
internal enum PlayerbotEngageReason : byte
{
    /// <summary>Nothing beyond the bot's own provocation.</summary>
    None,

    /// <summary>The player attacks the bot: checked by the rule itself, named for call sites that only grant that.</summary>
    Provoked,

    /// <summary>The player attacks a member or the master of the bot's group, or the master's or the leader's victim is the player.</summary>
    GroupProvoked,

    /// <summary>A real player ordered the attack (a party master's attack order).</summary>
    Ordered,

    /// <summary>The bot holds a defend post and the player is inside it (<c>.playerbot defend</c>).</summary>
    Defend,

    /// <summary>The bot belongs to a siege raid (<c>.playerbot siege</c>).</summary>
    Siege,
}

/// <summary>
/// The one rule for when a managed bot fights a player: a player is only ever a target of the bot when the combat hooks say it may be attacked
/// (the same rule as for a real player: the victim must be PvP-flagged unless the two duel, never a teammate, never a Game Master in GM mode),
/// and the bot starts on it unprovoked only when it was ordered to, defends a post or sieges. World thread; pure reads, no state.
/// </summary>
internal static class PlayerbotHostility
{
    /// <summary>
    /// <paramref name="other"/> is a living, spawned player on the bot's map that the bot may attack under the combat hooks
    /// (<c>CombatHooks.CanAttack</c>: friendliness, duel, PvP flag, GM mode). Says nothing about whether the bot should start on it
    /// (<see cref="MayEngage"/>).
    /// </summary>
    internal static bool IsHostilePlayer(Player self, Player other)
        => !ReferenceEquals(self, other) && other.IsInWorld && other.IsAlive && (other.Flags & PlayerFlags.Ghost) == 0
            && self.IsInWorld && ReferenceEquals(self.Map, other.Map) && self.Map is { } map
            && map.Combat.Hooks.CanAttack(self, other);

    /// <summary>
    /// Whether the bot may attack <paramref name="other"/> now: it is a hostile player (<see cref="IsHostilePlayer"/>) and either provoked it
    /// (it attacks the bot), or, with <paramref name="groups"/>,
    /// it attacks a member of the bot's group or is the victim of the group's leader (<see cref="PlayerbotEngageReason.GroupProvoked"/>), or
    /// <paramref name="granted"/> is an initiating reason (<see cref="PlayerbotEngageReason.Ordered"/>, <see cref="PlayerbotEngageReason.Defend"/>,
    /// <see cref="PlayerbotEngageReason.Siege"/>). Real players and bots share this rule.
    /// </summary>
    internal static bool MayEngage(Player self, Player other, PlayerbotEngageReason granted, GroupManager? groups)
    {
        if (!IsHostilePlayer(self, other)) return false;
        if (granted is PlayerbotEngageReason.Ordered or PlayerbotEngageReason.Defend or PlayerbotEngageReason.Siege) return true;
        if (IsProvokedBy(self, other)) return true;
        return groups is not null && IsGroupProvokedBy(self, other, groups);
    }

    /// <summary>The first player (alive, in the world, on the bot's map) among the bot's attackers; the caller still asks <see cref="MayEngage"/>.</summary>
    internal static Player? FirstProvokingPlayer(Player self)
    {
        if (self.Combat.Attackers.Count == 0) return null;
        foreach (Unit unit in self.Combat.Attackers)
        {
            if (unit is Player attacker && IsHostilePlayer(self, attacker)) return attacker;
        }

        return null;
    }

    private static bool IsProvokedBy(Player self, Player other)
        => self.Combat.Attackers.Contains(other) || ReferenceEquals(other.Combat.Victim, self);

    private static bool IsGroupProvokedBy(Player self, Player other, GroupManager groups)
    {
        if (groups.GetGroup(self.Guid) is not { } group || self.Map is not { } map) return false;
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (slot.Guid == self.Guid || map.FindObject(slot.Guid) is not Player member) continue;
            if (ReferenceEquals(other.Combat.Victim, member)) return true;
            if (group.IsLeader(slot.Guid) && ReferenceEquals(member.Combat.Victim, other)) return true;
        }

        return false;
    }
}
