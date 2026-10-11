using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>What a bot does for its team in a match (<see cref="PlayerbotBattlegroundRoles"/>).</summary>
internal enum PlayerbotBattlegroundRole : byte
{
    /// <summary>Warsong Gulch: takes the enemy flag. Arathi Basin: assaults the enemy's and the neutral nodes.</summary>
    Attacker,

    /// <summary>Warsong Gulch: follows its own flag carrier. Elsewhere the same as <see cref="Attacker"/>.</summary>
    Escort,

    /// <summary>Holds its own base, flag room or the nodes it owns.</summary>
    Defender,
}

/// <summary>
/// Who does what in a match, free of world state (unit tested). A team's bots, ordered by guid, are split by the shares of their match:
/// Warsong Gulch 40% attackers, 30% escorts and 30% defenders; Arathi Basin 70% assaulters and 30% defenders (Alterac Valley is not played
/// by bots, <see cref="PlayerbotBattlegroundBrackets.Supported"/>). Healers take the escort places first (vmangos BattleBotAI.cpp:379-421 has a healer follow a teammate; the escort is the
/// healer's natural place). The split is deterministic, so every bot computes the same one from the same list.
/// </summary>
internal static class PlayerbotBattlegroundRoles
{
    /// <summary>The shares (attackers, escorts, defenders) of a team of <paramref name="count"/> bots; they sum to <paramref name="count"/>.</summary>
    internal static (int Attackers, int Escorts, int Defenders) Shares(BattlegroundType type, int count)
    {
        if (count <= 0) return (0, 0, 0);
        switch (type)
        {
            case BattlegroundType.WarsongGulch:
            {
                int attackers = (int)Math.Ceiling(count * 0.4);
                int escorts = (int)Math.Floor(count * 0.3);
                return (attackers, escorts, count - attackers - escorts);
            }

            case BattlegroundType.ArathiBasin:
            {
                int attackers = (int)Math.Ceiling(count * 0.7);
                return (attackers, 0, count - attackers);
            }

            default:
                return (count, 0, 0);
        }
    }

    /// <summary>The role of every bot of one team: <paramref name="team"/> in any order, each with whether it is a healer.</summary>
    internal static Dictionary<ObjectGuid, PlayerbotBattlegroundRole> Assign(BattlegroundType type, IEnumerable<(ObjectGuid Guid, bool Healer)> team)
    {
        ArgumentNullException.ThrowIfNull(team);
        List<(ObjectGuid Guid, bool Healer)> ordered = [.. team.OrderBy(t => t.Guid.Value)];
        (int attackers, int escorts, int _) = Shares(type, ordered.Count);
        var roles = new Dictionary<ObjectGuid, PlayerbotBattlegroundRole>(ordered.Count);

        // Escorts: the healers first, then the rest in guid order.
        int escortsLeft = escorts;
        foreach ((ObjectGuid guid, bool healer) in ordered)
        {
            if (escortsLeft == 0) break;
            if (!healer) continue;
            roles[guid] = PlayerbotBattlegroundRole.Escort;
            escortsLeft--;
        }

        foreach ((ObjectGuid guid, bool healer) in ordered)
        {
            if (escortsLeft == 0) break;
            if (roles.ContainsKey(guid)) continue;
            roles[guid] = PlayerbotBattlegroundRole.Escort;
            escortsLeft--;
        }

        int attackersLeft = attackers;
        foreach ((ObjectGuid guid, bool _) in ordered)
        {
            if (roles.ContainsKey(guid)) continue;
            if (attackersLeft > 0)
            {
                roles[guid] = PlayerbotBattlegroundRole.Attacker;
                attackersLeft--;
            }
            else roles[guid] = PlayerbotBattlegroundRole.Defender;
        }

        return roles;
    }
}
