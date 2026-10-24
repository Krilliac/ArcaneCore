using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Playerbots.Commands;

/// <summary>
/// Player-accessible and operator companion extensions under <c>.playerbot</c> (<c>gear</c>, <c>party</c> and <c>raid</c>)
/// plus the <c>.bot</c> root shorthand (<see cref="BotAliasCommands"/>).
/// </summary>
public sealed class PlayerbotExtraCommands : ICommandExtension
{
    public string Path => "playerbot";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("commands", AccountSecurity.GameMaster,
            "Syntax: .playerbot commands\nList available playerbot chat/whisper commands and .bot subcommands.",
            CommandsHelp),
        new ChatCommand("debug", AccountSecurity.GameMaster,
            "Syntax: .playerbot debug [$id|$name]\nShow internal brain, motion, route, stall, risk and trace diagnostics for a running bot.",
            DebugBot),
        new ChatCommand("cadence", AccountSecurity.GameMaster,
            "Syntax: .playerbot cadence\nShow how often the bots think: the think gap percentiles (all bots and the combat/dead lane), thinks per tick, the think time budget and the share of ticks that ended on a budget, for the last minute.",
            Cadence),
        new ChatCommand("anomalies", AccountSecurity.GameMaster,
            "Syntax: .playerbot anomalies $name\nShow the recent anomaly history recorded for a bot.",
            Anomalies),
        new ChatCommand("social", AccountSecurity.Administrator,
            "Syntax: .playerbot social $id|$name\nShow a running bot's temperament and its ten best-known relations (affinity, familiarity, trust, flags, last topic, last seen). Read-only.",
            Social),
        new ChatCommand("gear", AccountSecurity.GameMaster,
            "Syntax: .playerbot gear $id|$name\nShow a running bot's equipped items, free bag slots, gold and skills.",
            Gear),
        new ChatCommand("raid", AccountSecurity.GameMaster,
            "Syntax: .playerbot raid #count\nGather #count online autonomous bots near your level into your raid and summon them to you. With no group a raid is formed; a party is converted to a raid (you must lead it). The count is clamped to the free raid slots (40 members); tanks and healers are mixed in as for a bot raid run.",
            RaidGather),
        new ChatCommand("party", AccountSecurity.GameMaster,
            "Syntax: .playerbot party <fill|add|remove|list>\nManage playerbot party members.",
            Children:
            [
                new ChatCommand("fill", AccountSecurity.GameMaster,
                    "Syntax: .playerbot party fill\nInvite online autonomous bots near your level and summon them to you: a party is filled to 5; in a raid, up to 5 more per use (at most 40). Use .playerbot raid #count for a larger raid.",
                    PartyFill),
                new ChatCommand("add", AccountSecurity.GameMaster,
                    "Syntax: .playerbot party add $id|$name\nInvite a running bot into your party.",
                    PartyAdd),
                new ChatCommand("remove", AccountSecurity.GameMaster,
                    "Syntax: .playerbot party remove $name\nRemove a bot from your party.",
                    PartyRemove),
                new ChatCommand("list", AccountSecurity.GameMaster,
                    "Syntax: .playerbot party list\nList the bots currently in your party.",
                    PartyList),
            ]),
    ];

    internal static bool CommandsHelp(CommandContext context, string text)
    {
        bool isGm = context.IsTrueConsole() || context.Security >= AccountSecurity.GameMaster || (!context.IsConsole && context.Player.IsGameMaster);
        context.Reply(PlayerbotChatCommands.HelpFor(isGm));
        context.Reply(isGm
            ? "Dot commands: .bot <commands|gear|summon|party|raid|status|list|debug|cadence> (or .playerbot ...)."
            : "Dot commands: .bot <commands|gear|summon|party <fill|add|remove|list>>.");
        return true;
    }

    /// <summary><c>.playerbot cadence</c>: the bots' think cadence over the last finished minute (<see cref="PlayerbotCadenceStats"/>).</summary>
    internal static bool Cadence(CommandContext context, string text)
    {
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        foreach (string line in CadenceLines(feature.LastCadence, feature.LastThinkBudgetMs)) context.Reply(line);
        return true;
    }

    /// <summary>The reply lines of <c>.playerbot cadence</c>.</summary>
    internal static IEnumerable<string> CadenceLines(PlayerbotCadenceReport? report, double lastBudgetMs)
    {
        if (report is not { } r)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"Playerbot cadence: no full minute measured yet (think budget now {lastBudgetMs:F1} ms per tick).");
            yield break;
        }

        yield return string.Create(CultureInfo.InvariantCulture,
            $"Playerbot cadence, last minute: {r.Bots} bots, think gap p50 {r.GapP50Ms} ms, p95 {r.GapP95Ms} ms, p99 {r.GapP99Ms} ms, max {r.GapMaxMs} ms.");
        yield return string.Create(CultureInfo.InvariantCulture,
            $"Combat/dead lane: {r.CombatThinks} thinks, gap p50 {r.CombatGapP50Ms} ms, p95 {r.CombatGapP95Ms} ms.");
        yield return string.Create(CultureInfo.InvariantCulture,
            $"{r.ThinksPerTick:F1} thinks per tick over {r.Ticks} ticks; think budget {r.AvgBudgetMs:F1} ms on average (now {lastBudgetMs:F1} ms); ticks ended on the time budget {r.TimeLimitedPct:F1}%, on the action budget {r.ActionLimitedPct:F1}%.");
    }

    internal static bool DebugBot(CommandContext context, string text)
    {
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        var args = new CommandArgs(text);
        string? targetArg = args.ExtractArg();
        if (!args.IsEmpty)
        {
            return false;
        }

        Guid botId = Guid.Empty;
        if (!string.IsNullOrEmpty(targetArg))
        {
            if (!feature.TryResolveBotId(targetArg, out botId, out _))
            {
                context.Reply("Playerbot not found.");
                return true;
            }
        }
        else if (!context.IsConsole && context.World.FindOnlinePlayer(context.Player.Selection) is { } selected
                 && feature.TryResolveBotId(selected.Name, out botId, out _))
        {
        }
        else
        {
            return false;
        }

        WorldSession? botSession = feature.FindSession(botId);
        if (botSession is null || botSession.State != SessionState.InWorld || botSession.Player is not { IsInWorld: true } botPlayer
            || feature.FindPartyAI(botId) is not { } party)
        {
            context.Reply("Playerbot is not running or was not found.");
            return true;
        }

        context.Reply($"{botPlayer.Name} ({botId}) — {party.DebugLine(botPlayer)}");
        context.Reply(party.StatusLine(botPlayer));
        return true;
    }

    /// <summary><c>.playerbot social $bot</c>: the bot's traits and its top relations (read-only).</summary>
    internal static bool Social(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? targetArg = args.ExtractArg();
        if (string.IsNullOrEmpty(targetArg) || !args.IsEmpty) return false;
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        if (!feature.TryResolveBotId(targetArg, out Guid botId, out string botName))
        {
            context.Reply("Playerbot not found.");
            return true;
        }

        if (feature.FindSession(botId)?.Player is not { } bot)
        {
            context.Reply("Playerbot is not running.");
            return true;
        }

        BotSocial.PlayerbotSocialMemory memory = feature.Social.Memory;
        foreach (string line in SocialLines(botName, BotSocial.PlayerbotTraits.For(bot.Guid.Value), memory.Enabled, memory.Top(botId, 10),
                     guid => context.World.FindOnlinePlayer(new ObjectGuid(guid))?.Name, memory.NowMs))
            context.Reply(line);
        if (feature.Social.Mood.Enabled) context.Reply(MoodLine(feature.Social.Mood.Get(botId)));
        return true;
    }

    /// <summary>The last line of <c>.playerbot social</c>: the bot's mood, its tone and the factor on the chance of its own initiative.</summary>
    internal static string MoodLine(float mood)
        => string.Create(CultureInfo.InvariantCulture,
            $"Mood {mood:+0.00;-0.00;0.00} ({BotSocial.PlayerbotMood.ToneOf(mood).ToString().ToLowerInvariant()}); the chance of its own initiative is x{BotSocial.PlayerbotMood.ChanceMultiplier(mood):F2}.");

    /// <summary>The reply lines of <c>.playerbot social</c> (a name for a guid is looked up with <paramref name="nameOf"/>; unknown ones show the guid).</summary>
    internal static IEnumerable<string> SocialLines(string botName, BotSocial.PlayerbotTraits traits, bool enabled,
        IReadOnlyList<PlayerbotRelation> top, Func<ulong, string?> nameOf, long nowMs)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        yield return string.Create(c, $"{botName}: sociability {traits.Sociability:F2}, chattiness {traits.Chattiness:F2}, generosity {traits.Generosity:F2}, curiosity {traits.Curiosity:F2}, helpfulness {traits.Helpfulness:F2}.");
        if (!enabled) yield return "Social memory is off (World:Playerbots:Social:Memory:Enabled).";
        if (top.Count == 0)
        {
            yield return "No relations yet.";
            yield break;
        }

        foreach (PlayerbotRelation r in top)
        {
            string who = nameOf(r.OtherGuid) ?? ("guid " + r.OtherGuid.ToString("X", c));
            long ageMs = Math.Max(0, nowMs - r.LastSeenUnixMs);
            string age = ageMs >= 86_400_000 ? string.Create(c, $"{ageMs / 86_400_000}d") : ageMs >= 3_600_000 ? string.Create(c, $"{ageMs / 3_600_000}h") : string.Create(c, $"{ageMs / 60_000}m");
            yield return string.Create(c,
                $"{who} ({(r.OtherIsBot ? "bot" : "player")}): affinity {r.Affinity}, familiarity {r.Familiarity}, trust {r.TrustPermille}, flags {r.Flags}, topic {(r.LastTopic.Length == 0 ? "-" : r.LastTopic)}, seen {age} ago{((r.WarFlags & PlayerbotRelationFlags.WarSeeded) != 0 ? $", war affinity {r.WarAffinity}" : string.Empty)}");
        }
    }

    internal static bool Gear(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? targetArg = args.ExtractArg();
        if (string.IsNullOrEmpty(targetArg) || !args.IsEmpty)
        {
            return false;
        }

        if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        if (!feature.TryResolveBotId(targetArg, out Guid botId, out _))
        {
            context.Reply("Playerbot not found.");
            return true;
        }

        WorldSession? botSession = feature.FindSession(botId);
        if (botSession is null || botSession.State != SessionState.InWorld || botSession.Player is not { IsInWorld: true } botPlayer)
        {
            context.Reply("Playerbot is not running or was not found.");
            return true;
        }

        if (!context.IsTrueConsole() && context.Security < AccountSecurity.GameMaster)
        {
            if (context.IsConsole)
            {
                context.Reply(GmStrings.SecurityTooLow); // a remote invoker has no character to share a group with
                return true;
            }

            GroupManager? groups = context.Session.Services.GetService<SocialFeature>()?.Context.Groups;
            if (groups?.AreInSameGroup(context.Player.Guid, botPlayer.Guid) != true)
            {
                context.Reply(GmStrings.SecurityTooLow);
                return true;
            }
        }

        var equipped = new List<string>();
        foreach ((byte slot, Item item) in botPlayer.Inventory.Equipped)
        {
            string name = !string.IsNullOrWhiteSpace(item.Template.Name)
                ? item.Template.Name
                : "#" + item.Entry.ToString(CultureInfo.InvariantCulture);
            equipped.Add(string.Create(CultureInfo.InvariantCulture,
                $"{SlotName(slot)}: {name} ({item.Entry}, ilvl {item.Template.ItemLevel})"));
        }

        context.Reply(equipped.Count == 0
            ? $"{botPlayer.Name} — Gear: none."
            : $"{botPlayer.Name} — Gear: {string.Join(", ", equipped)}.");
        context.Reply(PlayerbotPartyAI.BagsLine(botPlayer));
        context.Reply(PlayerbotPartyAI.GoldLine(botPlayer));
        context.Reply(PlayerbotPartyAI.SkillsLine(botPlayer));
        return true;
    }

    internal static bool PartyAdd(CommandContext context, string text)
    {
        if (context.IsConsole)
        {
            context.Reply(GmStrings.ConsoleNeedsPlayer);
            return true;
        }

        var args = new CommandArgs(text);
        string? id = args.ExtractArg();
        if (string.IsNullOrEmpty(id) || !args.IsEmpty)
        {
            return false;
        }

        if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        PlayerbotOperationResult result = feature.InviteToGroup(context.Player, id);
        context.Reply($"Playerbot {(result.Success ? "ok" : "failed")}: {result.Code} ({result.Name ?? id}).");
        return true;
    }

    internal static bool PartyRemove(CommandContext context, string text)
    {
        if (context.IsConsole)
        {
            context.Reply(GmStrings.ConsoleNeedsPlayer);
            return true;
        }

        var args = new CommandArgs(text);
        string? name = args.ExtractArg();
        if (string.IsNullOrEmpty(name) || !args.IsEmpty)
        {
            return false;
        }

        GroupManager? groups = context.Session.Services.GetService<SocialFeature>()?.Context.Groups;
        if (groups is null || groups.GetGroup(context.Player.Guid) is null)
        {
            context.Reply("You are not in a party.");
            return true;
        }

        groups.UninviteByName(context.Player, name);
        context.Reply($"Removed {name} from party.");
        return true;
    }

    internal static bool PartyList(CommandContext context, string text)
    {
        if (text.Trim().Length != 0)
        {
            return false;
        }

        if (context.IsConsole)
        {
            context.Reply(GmStrings.ConsoleNeedsPlayer);
            return true;
        }

        GroupManager? groups = context.Session.Services.GetService<SocialFeature>()?.Context.Groups;
        if (groups?.GetGroup(context.Player.Guid) is not { } group)
        {
            context.Reply("You are not in a party.");
            return true;
        }

        int count = 0;
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (slot.Guid == context.Player.Guid)
            {
                continue;
            }

            if (context.World.FindOnlinePlayer(slot.Guid) is { Session: WorldSession { IsManaged: true } } bot)
            {
                count++;
                context.Reply(string.Create(CultureInfo.InvariantCulture,
                    $"{bot.Name} (level {bot.Level} {bot.Class}, hp {bot.Health}/{bot.MaxHealth})"));
            }
        }

        if (count == 0)
        {
            context.Reply("No playerbots in your party.");
        }

        return true;
    }

    /// <summary>
    /// <c>.bot party fill</c>: invite online autonomous bots near the caller's level and summon them. A party (or no group) is filled
    /// to five; a raid takes one more subgroup's worth, up to forty (<see cref="PlayerbotGroupFill.PartyFillCount"/>).
    /// </summary>
    internal static bool PartyFill(CommandContext context, string text)
    {
        if (text.Trim().Length != 0)
        {
            return false;
        }

        if (context.IsConsole)
        {
            context.Reply(GmStrings.ConsoleNeedsPlayer);
            return true;
        }

        if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        Player leader = context.Player;
        GroupManager? groups = context.Session.Services.GetService<SocialFeature>()?.Context.Groups;
        Group? existingGroup = groups?.GetGroup(leader.Guid);
        bool raid = existingGroup is { IsRaid: true };
        int needed = PlayerbotGroupFill.PartyFillCount(raid, existingGroup?.MemberCount ?? 1);
        if (needed == 0)
        {
            context.Reply(raid ? "Your raid is already full." : "Your party is already full.");
            return true;
        }

        (int tanks, int healers) = PlayerbotGroupFill.RoleTargets(needed, feature.BotOptions.Groups);
        List<PlayerbotFillCandidate> selected = PlayerbotGroupFill.Select(FillCandidates(feature, groups, leader), needed, tanks, healers);
        if (selected.Count == 0)
        {
            context.Reply("Playerbot party fill: no eligible online bots found.");
            return true;
        }

        List<string> invitedNames = InviteAndSummon(context, feature, groups, leader, selected, makeRaid: false, out _);
        context.Reply(invitedNames.Count == 0
            ? "Playerbot party fill: 0 bots invited."
            : $"Playerbot party fill: invited {invitedNames.Count} ({PlayerbotGroupFill.Names(invitedNames)}).");
        return true;
    }

    /// <summary>
    /// <c>.bot raid N</c>: gather N online autonomous bots into the caller's raid. With no group one is formed and made a raid; a party
    /// is converted to a raid, which only its leader can do; N is clamped to the free raid slots. The bots are picked as party fill
    /// picks them, with the raid run's tank and healer make-up (<see cref="PlayerbotGroupFill.RoleTargets"/>), invited, made to accept
    /// and summoned.
    /// </summary>
    internal static bool RaidGather(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractInt32(out int requested) || requested < 1 || !args.IsEmpty)
        {
            return false;
        }

        if (context.IsConsole)
        {
            context.Reply(GmStrings.ConsoleNeedsPlayer);
            return true;
        }

        if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature
            || context.Session.Services.GetService<SocialFeature>()?.Context.Groups is not { } groups)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        Player leader = context.Player;
        Group? group = groups.GetGroup(leader.Guid);
        if (RaidRefusal(group, leader.Guid) is { } refusal)
        {
            context.Reply("Playerbot raid: " + refusal);
            return true;
        }

        if (group is null && groups.GetInvite(leader.Guid) is { } pending && !pending.IsLeader(leader.Guid))
        {
            context.Reply("Playerbot raid: answer your pending group invitation first.");
            return true;
        }

        (int count, bool clamped) = PlayerbotGroupFill.RaidFillCount(requested, group?.MemberCount ?? 1);
        if (count == 0)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"Playerbot raid: your raid is already full ({Group.MaxRaidSize}/{Group.MaxRaidSize})."));
            return true;
        }

        if (clamped)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"Playerbot raid: {requested} requested, clamped to the {count} free raid slot{(count == 1 ? string.Empty : "s")}."));
        }

        (int tanks, int healers) = PlayerbotGroupFill.RoleTargets(count, feature.BotOptions.Groups);
        List<PlayerbotFillCandidate> selected = PlayerbotGroupFill.Select(FillCandidates(feature, groups, leader), count, tanks, healers);
        if (selected.Count == 0)
        {
            context.Reply("Playerbot raid: no eligible online bots found.");
            return true;
        }

        bool wasRaid = group is { IsRaid: true }, hadGroup = group is not null;
        List<string> invitedNames = InviteAndSummon(context, feature, groups, leader, selected, makeRaid: true, out string? stopped);
        group = groups.GetGroup(leader.Guid);
        string formed = wasRaid || group is not { IsRaid: true } ? string.Empty : hadGroup ? ", party converted to a raid" : ", raid formed";
        int tankCount = selected.Count(c => invitedNames.Contains(c.Name) && PlayerbotGroupContent.CanTank(c.Class));
        int healerCount = selected.Count(c => invitedNames.Contains(c.Name) && PlayerbotGroupContent.CanHeal(c.Class));
        context.Reply(invitedNames.Count == 0
            ? $"Playerbot raid: 0 bots invited{(stopped is null ? "." : $" ({stopped}).")}"
            : string.Create(CultureInfo.InvariantCulture,
                $"Playerbot raid: invited {invitedNames.Count} of {count}{formed}; raid {group?.MemberCount ?? 1}/{Group.MaxRaidSize} ({tankCount} tank-capable, {healerCount} healer-capable): {PlayerbotGroupFill.Names(invitedNames)}."));
        if (stopped is not null && invitedNames.Count > 0)
        {
            context.Reply($"Playerbot raid: stopped early ({stopped}).");
        }

        return true;
    }

    /// <summary>Why <c>.bot raid</c> refuses the caller's group, or null: a party only its leader converts, a raid only its leader or an assistant fills.</summary>
    internal static string? RaidRefusal(Group? group, ObjectGuid caller)
    {
        if (group is null) return null;
        if (group.IsBattlegroundGroup) return "you are in a battleground group.";
        if (!group.IsRaid && !group.IsLeader(caller)) return $"only the party leader ({group.LeaderName}) can convert the party to a raid.";
        if (group.IsRaid && !group.IsLeaderOrAssistant(caller)) return $"only the raid leader ({group.LeaderName}) or an assistant can invite.";
        return null;
    }

    /// <summary>
    /// The bots party fill and <c>.bot raid</c> may take: running, managed, in the world and alive, of the caller's team, not scripted
    /// and in no group (and no other pending invitation).
    /// </summary>
    private static List<PlayerbotFillCandidate> FillCandidates(ManagedPlayerbotFeature feature, GroupManager? groups, Player leader)
    {
        var candidates = new List<PlayerbotFillCandidate>();
        foreach (PlayerbotStatus status in feature.Snapshot())
        {
            if (status.State != ManagedPlayerbotState.Running)
            {
                continue;
            }

            WorldSession? session = feature.FindSession(status.BotId);
            if (session is not { IsManaged: true, State: SessionState.InWorld, Player: { IsInWorld: true, IsAlive: true } bot })
            {
                continue;
            }

            if (bot.Guid == leader.Guid || bot.Team != leader.Team || feature.IsScripted(status.BotId))
            {
                continue;
            }

            if (groups?.GetGroup(bot.Guid) is not null || groups?.GetInvite(bot.Guid) is not null)
            {
                continue;
            }

            candidates.Add(new PlayerbotFillCandidate(status.BotId, bot.Name, bot.Class, Math.Abs((int)bot.Level - leader.Level)));
        }

        return candidates;
    }

    /// <summary>
    /// Invite each selected bot (it accepts at once: <see cref="ManagedPlayerbotFeature.InviteToGroup"/>) and summon it to the leader
    /// unless it is already being teleported. With <paramref name="makeRaid"/> the group is made a raid as soon as it has two members
    /// (before the sixth would be refused); <paramref name="stopped"/> says why the invites stopped early, null when they did not.
    /// </summary>
    private static List<string> InviteAndSummon(CommandContext context, ManagedPlayerbotFeature feature, GroupManager? groups, Player leader,
        IReadOnlyList<PlayerbotFillCandidate> selected, bool makeRaid, out string? stopped)
    {
        stopped = null;
        TeleportFeature? tele = context.Session.Services.GetService<TeleportFeature>();
        var invitedNames = new List<string>(selected.Count);
        foreach (PlayerbotFillCandidate candidate in selected)
        {
            if (makeRaid && groups?.GetGroup(leader.Guid) is { IsRaid: false } party)
            {
                groups.ConvertToRaid(leader);
                if (!party.IsRaid)
                {
                    stopped = "the party could not be converted to a raid";
                    break;
                }
            }

            PlayerbotOperationResult res = feature.InviteToGroup(leader, candidate.BotId.ToString());
            if (!res.Success)
            {
                continue;
            }

            invitedNames.Add(candidate.Name);
            if (tele is not null && leader.Map is not null && feature.FindSession(candidate.BotId)?.Player is { } bot
                && !tele.Teleports.IsBeingTeleported(bot))
            {
                tele.Teleports.TeleportTo(bot, leader.MapId, leader.X, leader.Y, leader.Z, leader.Orientation);
            }
        }

        if (makeRaid && stopped is null && groups?.GetGroup(leader.Guid) is { IsRaid: false } formed)
        {
            groups.ConvertToRaid(leader);
            if (!formed.IsRaid) stopped = "the party could not be converted to a raid";
        }

        return invitedNames;
    }

    internal static bool SummonSingleOrFleet(CommandContext context, string text)
    {
        if (!context.IsTrueConsole() && context.Security < AccountSecurity.GameMaster)
        {
            if (context.IsConsole)
            {
                context.Reply(GmStrings.SecurityTooLow); // a remote invoker has no character to share a group with
                return true;
            }

            var args = new CommandArgs(text);
            string? targetArg = args.ExtractArg();
            if (string.IsNullOrEmpty(targetArg) || !args.IsEmpty)
            {
                return false;
            }

            if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature
                || context.Session.Services.GetService<TeleportFeature>() is not { } tele)
            {
                context.Reply("Playerbot service is unavailable.");
                return true;
            }

            if (!feature.TryResolveBotId(targetArg, out Guid botId, out _))
            {
                context.Reply("Playerbot not found.");
                return true;
            }

            WorldSession? botSession = feature.FindSession(botId);
            if (botSession is null || botSession.State != SessionState.InWorld || botSession.Player is not { IsInWorld: true } botPlayer)
            {
                context.Reply("Playerbot is not running or was not found.");
                return true;
            }

            GroupManager? groups = context.Session.Services.GetService<SocialFeature>()?.Context.Groups;
            if (groups?.AreInSameGroup(context.Player.Guid, botPlayer.Guid) != true)
            {
                context.Reply(GmStrings.SecurityTooLow);
                return true;
            }

            if (!botPlayer.IsAlive)
            {
                context.Reply($"{botPlayer.Name}: dead");
                return true;
            }

            tele.Teleports.TeleportTo(botPlayer, context.Player.MapId, context.Player.X, context.Player.Y, context.Player.Z, context.Player.Orientation);
            context.Reply($"Summoned {botPlayer.Name}.");
            return true;
        }

        context.Commands.Execute(context, "playerbot summon " + text);
        return true;
    }

    internal static bool Anomalies(CommandContext context, string text)
        => PlayerbotAnomalyHistory.RunCommand(
            text,
            context.Session.Services.GetService<ManagedPlayerbotFeature>(),
            PlayerbotAnomalyHistory.Shared,
            DateTimeOffset.UtcNow,
            context.Reply);

    private static string SlotName(byte slot) => slot switch
    {
        InventorySlots.Head => "Head",
        InventorySlots.Neck => "Neck",
        InventorySlots.Shoulders => "Shoulders",
        InventorySlots.Body => "Shirt",
        InventorySlots.Chest => "Chest",
        InventorySlots.Waist => "Waist",
        InventorySlots.Legs => "Legs",
        InventorySlots.Feet => "Feet",
        InventorySlots.Wrists => "Wrists",
        InventorySlots.Hands => "Hands",
        InventorySlots.Finger1 => "Finger1",
        InventorySlots.Finger2 => "Finger2",
        InventorySlots.Trinket1 => "Trinket1",
        InventorySlots.Trinket2 => "Trinket2",
        InventorySlots.Back => "Back",
        InventorySlots.MainHand => "MainHand",
        InventorySlots.OffHand => "OffHand",
        InventorySlots.Ranged => "Ranged",
        InventorySlots.Tabard => "Tabard",
        _ => slot.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// <c>.bot</c> root shorthand forwarding to <c>.playerbot</c> subcommands.
/// </summary>
public sealed class BotAliasCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("bot", AccountSecurity.GameMaster,
            "Syntax: .bot <commands|status|list|debug|anomalies|gear|summon|party|raid>\nShorthand for .playerbot commands.",
            Children:
            [
                new ChatCommand("commands", AccountSecurity.GameMaster,
                    "Syntax: .bot commands\nList available playerbot chat/whisper commands and .bot subcommands.",
                    PlayerbotExtraCommands.CommandsHelp),
                new ChatCommand("status", AccountSecurity.GameMaster,
                    "Syntax: .bot status [$id|$name]\nShow bot lifecycle status.",
                    static (ctx, args) =>
                    {
                        ctx.Commands.Execute(ctx, string.IsNullOrWhiteSpace(args) ? "playerbot status" : "playerbot status " + args);
                        return true;
                    }),
                new ChatCommand("list", AccountSecurity.GameMaster,
                    "Syntax: .bot list\nList managed bots.",
                    static (ctx, args) =>
                    {
                        ctx.Commands.Execute(ctx, string.IsNullOrWhiteSpace(args) ? "playerbot list" : "playerbot list " + args);
                        return true;
                    }),
                new ChatCommand("debug", AccountSecurity.GameMaster,
                    "Syntax: .bot debug [$id|$name]\nShow internal brain, motion, route, stall, risk and trace diagnostics for a running bot.",
                    PlayerbotExtraCommands.DebugBot),
                new ChatCommand("cadence", AccountSecurity.GameMaster,
                    "Syntax: .bot cadence\nShow how often the bots think (think gap percentiles, thinks per tick, think budget) for the last minute.",
                    PlayerbotExtraCommands.Cadence),
                new ChatCommand("anomalies", AccountSecurity.GameMaster,
                    "Syntax: .bot anomalies $name\nShow the recent anomaly history recorded for a bot.",
                    PlayerbotExtraCommands.Anomalies),
                new ChatCommand("gear", AccountSecurity.GameMaster,
                    "Syntax: .bot gear $id|$name\nShow a running bot's equipped items, free bag slots, gold and skills.",
                    PlayerbotExtraCommands.Gear),
                new ChatCommand("summon", AccountSecurity.GameMaster,
                    "Syntax: .bot summon $id|$name\nTeleport a party bot (or fleet selection for GMs) to you.",
                    PlayerbotExtraCommands.SummonSingleOrFleet),
                new ChatCommand("raid", AccountSecurity.GameMaster,
                    "Syntax: .bot raid #count\nGather #count online autonomous bots near your level into your raid and summon them to you. With no group a raid is formed; a party is converted to a raid (you must lead it). The count is clamped to the free raid slots (40 members); tanks and healers are mixed in as for a bot raid run.",
                    PlayerbotExtraCommands.RaidGather),
                new ChatCommand("party", AccountSecurity.GameMaster,
                    "Syntax: .bot party <fill|add|remove|list>\nManage playerbot party members.",
                    Children:
                    [
                        new ChatCommand("fill", AccountSecurity.GameMaster,
                            "Syntax: .bot party fill\nInvite online autonomous bots near your level and summon them to you: a party is filled to 5; in a raid, up to 5 more per use (at most 40). Use .bot raid #count for a larger raid.",
                            PlayerbotExtraCommands.PartyFill),
                        new ChatCommand("add", AccountSecurity.GameMaster,
                            "Syntax: .bot party add $id|$name\nInvite a running bot into your party.",
                            PlayerbotExtraCommands.PartyAdd),
                        new ChatCommand("remove", AccountSecurity.GameMaster,
                            "Syntax: .bot party remove $name\nRemove a bot from your party.",
                            PlayerbotExtraCommands.PartyRemove),
                        new ChatCommand("list", AccountSecurity.GameMaster,
                            "Syntax: .bot party list\nList the bots currently in your party.",
                            PlayerbotExtraCommands.PartyList),
                    ]),
            ]),
    ];
}
