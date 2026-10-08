using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.Combat;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>What kind of content a group goal is.</summary>
internal enum PlayerbotGroupGoalKind : byte
{
    /// <summary>An open-world quest objective too strong for one bot (an elite quest, or one the risk estimate passed over).</summary>
    Quest,

    /// <summary>Quest objectives inside a five-player instance: travel to the entrance, go in together, clear to them.</summary>
    Dungeon,

    /// <summary>A raid quest or objectives inside a raid instance: as a dungeon, with a raid group.</summary>
    Raid,
}

/// <summary>A member's job in its group (vmangos CombatBotRoles, folded into the three a group is built from).</summary>
internal enum PlayerbotGroupRole : byte
{
    Tank,
    Healer,
    Damage,
}

/// <summary>
/// One bot's "needs a group of N" goal. <see cref="Key"/> is what two bots must share to be grouped for it: the objective creature
/// for an open-world quest (two quests that kill the same elite overlap), the instance map for a dungeon or raid.
/// </summary>
/// <param name="QuestId">The quest the bot needs it for (0 when the risk estimate found the objective without a quest row to name).</param>
/// <param name="ObjectiveEntry">The creature to kill (for an instance: the first unfinished objective inside).</param>
/// <param name="MapId">Where the objective is: the continent for a quest, the instance map otherwise.</param>
/// <param name="MeetingMap">Where the group meets: the bot's continent.</param>
/// <param name="Meeting">The meeting point: the objective's nearest spawn for a quest, the instance entrance otherwise.</param>
/// <param name="EntranceTrigger">The area trigger into the instance (0 for open-world content).</param>
/// <param name="Objective">Where the objective stands inside the instance (or the open-world spawn).</param>
internal sealed record PlayerbotGroupGoal(
    PlayerbotGroupGoalKind Kind,
    uint QuestId,
    uint ObjectiveEntry,
    uint MapId,
    uint MeetingMap,
    Vector3 Meeting,
    uint EntranceTrigger,
    Vector3 Objective,
    int Size,
    byte MinLevel,
    string Source)
{
    /// <summary>The matching key (<see cref="PlayerbotGroupGoal"/>).</summary>
    public (PlayerbotGroupGoalKind Kind, uint Value) Key => InInstance ? (Kind, MapId) : (Kind, ObjectiveEntry);

    /// <summary>Whether the goal lies inside an instance the group enters through <see cref="EntranceTrigger"/>.</summary>
    public bool InInstance => EntranceTrigger != 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Kind.ToString().ToLowerInvariant()}:{(InInstance ? MapId : ObjectiveEntry)} quest={QuestId} size={Size} source={Source}");
}

/// <summary>A bot as the matcher sees it.</summary>
internal sealed record PlayerbotGroupCandidate(
    Guid BotId,
    ulong Guid,
    string Name,
    Class Class,
    byte Level,
    uint Team,
    uint MapId,
    Vector3 Position,
    PlayerbotRole TalentRole,
    float Gear,
    uint NeedSinceMs);

/// <summary>A formed group, before the invitations go out (<see cref="PlayerbotGroupContent.Match"/>).</summary>
internal sealed record PlayerbotGroupPlan(
    PlayerbotGroupCandidate Leader,
    IReadOnlyList<(PlayerbotGroupCandidate Bot, PlayerbotGroupRole Role, byte SubGroup)> Members);

/// <summary>
/// The pure rules of bot groups (apart from the world so they can be tested alone): which content needs a group and how big, who can
/// fill which role, and who goes with whom.
/// <para>
/// The quest flags are vmangos <c>quest_template.Type</c>, QuestInfo.dbc ids (SharedDefines.h QuestTypes: 1 QUEST_TYPE_ELITE, the
/// client's "Group"; 41 PvP; 62 Raid; 81 Dungeon), and <c>SuggestedPlayers</c>. Type 41 is PvP, not elite: in the live world
/// database "Wanted: Hogger" (176) is Type 1 and the Alterac Valley quests are Type 41. Neither vmangos nor
/// the mangoszero playerbot module groups bots by itself (vmangos PartyBot needs a player; the mangoszero module's "lfg" strategy
/// joins the server's LFG queue); the matching here is ArcaneCore's own, after vanilla's unwritten norms: five for a dungeon, a
/// tank and a healer when there are three or more, levels close together.
/// </para>
/// </summary>
internal static class PlayerbotGroupContent
{
    /// <summary>quest_template.Type: Elite, the client's "Group" (QuestInfo.dbc 1, vmangos QUEST_TYPE_ELITE).</summary>
    internal const uint QuestTypeElite = 1;

    /// <summary>quest_template.Type: Raid (QuestInfo.dbc 62).</summary>
    internal const uint QuestTypeRaid = 62;

    /// <summary>quest_template.Type: Dungeon (QuestInfo.dbc 81).</summary>
    internal const uint QuestTypeDungeon = 81;

    /// <summary>The size of a five-player group (Group::MaxGroupSize).</summary>
    internal const int PartySize = 5;

    /// <summary>The smallest raid a raid quest asks for when its row suggests none.</summary>
    internal const int MinRaidSize = 10;

    /// <summary>
    /// The group a quest asks for (0: one player is enough). A raid quest a raid of its suggested size (<see cref="MinRaidSize"/> when
    /// it suggests a party or none), a dungeon quest five, an elite quest its suggested size or three, any other quest its suggested
    /// size when that is more than one. Only a raid quest gets a raid: a raid group's kills credit raid quests alone (vmangos
    /// Player::KilledMonster, the quest's raid check), so an elite quest suggesting more than five is done by a party of five.
    /// </summary>
    internal static int QuestGroupSize(QuestTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        int suggested = template.SuggestedPlayers;
        return template.Type switch
        {
            QuestTypeRaid => suggested > PartySize ? Math.Min(suggested, 40) : MinRaidSize,
            QuestTypeDungeon => suggested > 1 ? Math.Min(suggested, PartySize) : PartySize,
            QuestTypeElite => suggested > 1 ? Math.Min(suggested, PartySize) : 3,
            _ => suggested > 1 ? Math.Min(suggested, PartySize) : 0,
        };
    }

    /// <summary>The group an instance asks for: its player limit (five for a dungeon without one, ten for a raid without one), at most 40.</summary>
    internal static int InstanceGroupSize(MapTemplate map)
    {
        ArgumentNullException.ThrowIfNull(map);
        int limit = (int)Math.Min(map.PlayerLimit, 40u);
        return map.IsRaid ? Math.Max(limit == 0 ? MinRaidSize : limit, PartySize + 1) : Math.Clamp(limit == 0 ? PartySize : limit, 2, PartySize);
    }

    /// <summary>The tanks and healers a group of <paramref name="size"/> needs: none for two, the configured minimum for a party, one more of each per ten in a raid.</summary>
    internal static (int Tanks, int Healers) RoleNeeds(int size, PlayerbotGroupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (size < 3) return (0, 0);
        if (size <= PartySize) return (options.MinTank, options.MinHealer);
        return (Math.Max(options.MinTank, size / 10 + 1), Math.Max(options.MinHealer, size / 5));
    }

    /// <summary>Whether a class can tank (vmangos IsTankClass: warrior, paladin, druid in bear form).</summary>
    internal static bool CanTank(Class @class) => @class is Class.Warrior or Class.Paladin or Class.Druid;

    /// <summary>Whether a class can heal (vmangos IsHealerClass: priest, paladin, shaman, druid).</summary>
    internal static bool CanHeal(Class @class) => @class is Class.Priest or Class.Paladin or Class.Shaman or Class.Druid;

    /// <summary>The role a bot prefers in a group: its talent role (vmangos AutoAssignRole), a warrior a tank.</summary>
    internal static PlayerbotGroupRole Preferred(Class @class, PlayerbotRole talentRole) => talentRole switch
    {
        PlayerbotRole.Tank => PlayerbotGroupRole.Tank,
        PlayerbotRole.Healer => PlayerbotGroupRole.Healer,
        _ when @class == Class.Warrior => PlayerbotGroupRole.Tank,
        _ => PlayerbotGroupRole.Damage,
    };

    /// <summary>The combat role a group role gives the class rotation (a damage dealer keeps its talent role).</summary>
    internal static PlayerbotRole CombatRole(PlayerbotGroupRole role, PlayerbotRole talentRole) => role switch
    {
        PlayerbotGroupRole.Tank => PlayerbotRole.Tank,
        PlayerbotGroupRole.Healer => PlayerbotRole.Healer,
        _ => talentRole is PlayerbotRole.Tank or PlayerbotRole.Healer ? PlayerbotRole.MeleeDps : talentRole,
    };

    /// <summary>
    /// Choose a group of <paramref name="size"/> for one goal from <paramref name="candidates"/> (bots of one team that want it), or
    /// null. Tanks are taken first (bots that prefer the role, then any that can, highest level and gear first), then healers, then
    /// the rest by level closeness to the tank and by who waited longest; every member must be within
    /// <see cref="PlayerbotGroupOptions.LevelRange"/> of every other. The leader is the best tank (highest level, then gear), else
    /// the bot that has waited longest (the quest's owner). A raid is spread over subgroups of five, the tanks and healers first.
    /// </summary>
    internal static PlayerbotGroupPlan? Match(IReadOnlyList<PlayerbotGroupCandidate> candidates, int size, PlayerbotGroupOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);
        if (size < 2 || candidates.Count < size) return null;
        (int tanks, int healers) = RoleNeeds(size, options);
        // Try each candidate as the level anchor (lowest level of the group), lowest first: the first window that fills wins.
        foreach (byte floor in candidates.Select(c => c.Level).Distinct().OrderBy(level => level))
        {
            PlayerbotGroupCandidate[] window = [.. candidates.Where(c => c.Level >= floor && c.Level <= floor + options.LevelRange)];
            if (window.Length < size) continue;
            if (Fill(window, size, tanks, healers) is { } plan) return plan;
        }

        return null;
    }

    private static PlayerbotGroupPlan? Fill(PlayerbotGroupCandidate[] window, int size, int tanks, int healers)
    {
        var chosen = new List<(PlayerbotGroupCandidate Bot, PlayerbotGroupRole Role)>();
        var left = new List<PlayerbotGroupCandidate>(window);
        foreach (PlayerbotGroupCandidate tank in left.Where(c => CanTank(c.Class))
                     .OrderByDescending(c => Preferred(c.Class, c.TalentRole) == PlayerbotGroupRole.Tank)
                     .ThenByDescending(c => c.Level).ThenByDescending(c => c.Gear).ThenBy(c => c.NeedSinceMs).Take(tanks).ToArray())
        {
            chosen.Add((tank, PlayerbotGroupRole.Tank));
            left.Remove(tank);
        }

        if (chosen.Count < tanks) return null;
        foreach (PlayerbotGroupCandidate healer in left.Where(c => CanHeal(c.Class))
                     .OrderByDescending(c => Preferred(c.Class, c.TalentRole) == PlayerbotGroupRole.Healer)
                     .ThenByDescending(c => c.Level).ThenBy(c => c.NeedSinceMs).Take(healers).ToArray())
        {
            chosen.Add((healer, PlayerbotGroupRole.Healer));
            left.Remove(healer);
        }

        if (chosen.Count < tanks + healers) return null;
        byte anchor = chosen.Count > 0 ? chosen[0].Bot.Level : left.Max(c => c.Level);
        foreach (PlayerbotGroupCandidate rest in left.OrderBy(c => c.NeedSinceMs).ThenBy(c => Math.Abs(c.Level - anchor))
                     .ThenBy(c => c.BotId).Take(size - chosen.Count).ToArray())
        {
            // A bot that prefers tanking or healing still fills a damage slot when the roles are covered.
            chosen.Add((rest, Preferred(rest.Class, rest.TalentRole) switch
            {
                PlayerbotGroupRole.Tank when chosen.All(c => c.Role != PlayerbotGroupRole.Tank) => PlayerbotGroupRole.Tank,
                PlayerbotGroupRole.Healer when chosen.All(c => c.Role != PlayerbotGroupRole.Healer) => PlayerbotGroupRole.Healer,
                _ => PlayerbotGroupRole.Damage,
            }));
        }

        if (chosen.Count < size) return null;
        PlayerbotGroupCandidate leader = chosen.Where(c => c.Role == PlayerbotGroupRole.Tank).Select(c => c.Bot)
            .OrderByDescending(c => c.Level).ThenByDescending(c => c.Gear).ThenBy(c => c.NeedSinceMs).FirstOrDefault()
            ?? chosen.Select(c => c.Bot).OrderBy(c => c.NeedSinceMs).ThenByDescending(c => c.Level).ThenBy(c => c.BotId).First();

        // Raid subgroups: tanks, then healers, then the rest dealt round the subgroups so each holds its share of every role.
        int subgroups = Math.Max(1, (size + PartySize - 1) / PartySize);
        var members = new List<(PlayerbotGroupCandidate, PlayerbotGroupRole, byte)>();
        var counts = new int[subgroups];
        int next = 0;
        foreach ((PlayerbotGroupCandidate bot, PlayerbotGroupRole role) in chosen.OrderBy(c => c.Role))
        {
            while (counts[next % subgroups] >= PartySize) next++;
            byte subgroup = (byte)(next % subgroups);
            counts[subgroup]++;
            next++;
            members.Add((bot, role, subgroup));
        }

        return new PlayerbotGroupPlan(leader, members);
    }
}
