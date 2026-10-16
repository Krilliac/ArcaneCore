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
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Commands;

/// <summary>
/// Player-accessible and operator companion extensions under <c>.playerbot</c> (<c>gear</c> and <c>party</c>)
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
        new ChatCommand("gear", AccountSecurity.GameMaster,
            "Syntax: .playerbot gear $id|$name\nShow a running bot's equipped items, free bag slots, gold and skills.",
            Gear),
        new ChatCommand("party", AccountSecurity.GameMaster,
            "Syntax: .playerbot party <fill|add|remove|list>\nManage playerbot party members.",
            Children:
            [
                new ChatCommand("fill", AccountSecurity.GameMaster,
                    "Syntax: .playerbot party fill\nInvite up to 4 online autonomous bots near your level into your party and summon them to you.",
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
        bool isGm = context.IsConsole || context.Security >= AccountSecurity.GameMaster || (!context.IsConsole && context.Player.IsGameMaster);
        context.Reply(PlayerbotChatCommands.HelpFor(isGm));
        context.Reply(isGm
            ? "Dot commands: .bot <commands|gear|summon|party|status|list|debug|cadence> (or .playerbot ...)."
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
            || feature.FindParty(botId) is not { } party)
        {
            context.Reply("Playerbot is not running or was not found.");
            return true;
        }

        context.Reply($"{botPlayer.Name} ({botId}) — {party.DebugLine(botPlayer)}");
        context.Reply(party.StatusLine(botPlayer));
        return true;
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

        if (!context.IsConsole && context.Security < AccountSecurity.GameMaster)
        {
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
        int currentMembers = existingGroup?.Members.Count ?? 1;
        int needed = Math.Clamp(5 - currentMembers, 0, 4);
        if (needed == 0)
        {
            context.Reply("Your party is already full.");
            return true;
        }

        var candidates = new List<(Guid BotId, Player Bot, int LevelDiff)>();
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

            if (groups?.GetGroup(bot.Guid) is not null)
            {
                continue;
            }

            int diff = Math.Abs((int)bot.Level - leader.Level);
            candidates.Add((status.BotId, bot, diff));
        }

        List<(Guid BotId, Player Bot, int LevelDiff)> pool = candidates.Where(c => c.LevelDiff <= 5).ToList();
        if (pool.Count < needed)
        {
            pool = candidates.OrderBy(c => c.LevelDiff).ToList();
        }
        else
        {
            pool = pool.OrderBy(c => c.LevelDiff).ToList();
        }

        var selected = new List<(Guid BotId, Player Bot)>(needed);
        void PickRole(Func<Class, bool> predicate)
        {
            if (selected.Count >= needed)
            {
                return;
            }

            int idx = pool.FindIndex(c => predicate(c.Bot.Class));
            if (idx >= 0)
            {
                selected.Add((pool[idx].BotId, pool[idx].Bot));
                pool.RemoveAt(idx);
            }
        }

        PickRole(static cls => cls is Class.Warrior or Class.Druid or Class.Paladin);
        PickRole(static cls => cls is Class.Priest or Class.Paladin or Class.Shaman or Class.Druid);
        while (selected.Count < needed && pool.Count > 0)
        {
            selected.Add((pool[0].BotId, pool[0].Bot));
            pool.RemoveAt(0);
        }

        if (selected.Count == 0)
        {
            context.Reply("Playerbot party fill: no eligible online bots found.");
            return true;
        }

        TeleportFeature? tele = context.Session.Services.GetService<TeleportFeature>();
        var invitedNames = new List<string>(selected.Count);
        foreach ((Guid botId, Player bot) in selected)
        {
            PlayerbotOperationResult res = feature.InviteToGroup(leader, botId.ToString());
            if (res.Success)
            {
                invitedNames.Add(bot.Name);
                if (tele is not null && leader.Map is not null && !tele.Teleports.IsBeingTeleported(bot))
                {
                    tele.Teleports.TeleportTo(bot, leader.MapId, leader.X, leader.Y, leader.Z, leader.Orientation);
                }
            }
        }

        context.Reply(invitedNames.Count == 0
            ? "Playerbot party fill: 0 bots invited."
            : $"Playerbot party fill: invited {invitedNames.Count} ({string.Join(", ", invitedNames)}).");
        return true;
    }

    internal static bool SummonSingleOrFleet(CommandContext context, string text)
    {
        if (!context.IsConsole && context.Security < AccountSecurity.GameMaster)
        {
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
            "Syntax: .bot <commands|status|list|debug|anomalies|gear|summon|party>\nShorthand for .playerbot commands.",
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
                new ChatCommand("party", AccountSecurity.GameMaster,
                    "Syntax: .bot party <fill|add|remove|list>\nManage playerbot party members.",
                    Children:
                    [
                        new ChatCommand("fill", AccountSecurity.GameMaster,
                            "Syntax: .bot party fill\nInvite up to 4 online autonomous bots near your level into your party and summon them to you.",
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
