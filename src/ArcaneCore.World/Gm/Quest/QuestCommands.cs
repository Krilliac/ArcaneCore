using System.Globalization;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Reputation;
using Microsoft.Extensions.DependencyInjection;
using GameQuest = ArcaneCore.Game.Quests.Quest;

namespace ArcaneCore.World.Gm.Quest;

/// <summary>Reply texts of the <c>.quest</c> commands: mangos zero <c>LANG_*</c> ids (Tools/Language.h:421-423) and ArcaneCore's own.</summary>
public static class QuestCommandText
{
    /// <summary>LANG_COMMAND_QUEST_NOTFOUND (471): "Quest %u not found.".</summary>
    public static string NotFound(uint questId) => string.Create(CultureInfo.InvariantCulture, $"Quest {questId} not found.");

    /// <summary>LANG_COMMAND_QUEST_STARTFROMITEM (472).</summary>
    public static string StartFromItem(uint questId, uint itemId) => string.Create(CultureInfo.InvariantCulture,
        $"Quest {questId} started from item. For correct work, please, add item to inventory and start quest in normal way: .additem {itemId}");

    /// <summary>LANG_COMMAND_QUEST_REMOVED (473).</summary>
    public const string Removed = "Quest removed.";

    /// <summary>ArcaneCore's: the journal is not loaded yet, or a quest reward is settling (see <see cref="Player.CanMutateQuestSettlementState"/>).</summary>
    public static string NotReady(string link) => $"The quest log of {link} is not ready; try again in a moment.";

    /// <summary>ArcaneCore's: mangos would give the quest a second log slot.</summary>
    public static string AlreadyInLog(uint questId, string link) => string.Create(CultureInfo.InvariantCulture, $"Quest {questId} is already in the quest log of {link}.");

    /// <summary>ArcaneCore's: the quest support gate withholds the quest (docs/areas/quests-npc.md).</summary>
    public static string Withheld(uint questId, QuestAdapter missing) => string.Create(CultureInfo.InvariantCulture, $"Quest {questId} is withheld on this server (missing adapters: {missing}).");

    /// <summary>ArcaneCore's: mangos only sends SMSG_QUESTLOG_FULL to the player (the player gets it here too).</summary>
    public static string LogFull(string link) => $"The quest log of {link} is full.";

    /// <summary>ArcaneCore's: the player was told why (SMSG_QUESTGIVER_QUEST_FAILED or the equip error).</summary>
    public static string SourceItemRefused(uint questId, string link) => string.Create(CultureInfo.InvariantCulture, $"Quest {questId} cannot give its source item to {link}.");

    /// <summary>ArcaneCore's: a failed quest is not forced complete (see <see cref="QuestNpcServices.GmCompleteQuest"/>).</summary>
    public static string FailedQuest(uint questId) => string.Create(CultureInfo.InvariantCulture, $"Quest {questId} has failed; remove it and add it again.");

    /// <summary>ArcaneCore's: the header of <c>.quest status</c> without a quest.</summary>
    public static string LogOf(string link, int count) => string.Create(CultureInfo.InvariantCulture, $"Quest log of {link}: {count} quest(s).");

    /// <summary>The quest link the client renders (vmangos <c>|Hquest:id:level|h[title]|h|r</c>, Chat.cpp:89).</summary>
    public static string QuestLink(GameQuest quest) => string.Create(CultureInfo.InvariantCulture, $"|cffffffff|Hquest:{quest.Id}:{quest.QuestLevel}|h[{quest.Title}]|h|r");
}

/// <summary>
/// <c>.quest add|complete|remove #quest_id</c> (mangos zero ChatCommands/QuestCommands.cpp:47-283; all three SEC_ADMINISTRATOR,
/// WorldHandlers/Chat.cpp:527-533, group Chat.cpp:801) plus ArcaneCore's read-only <c>.quest status</c>. The target is the
/// selected player or the invoker (getSelectedPlayer); a selected creature answers "No character selected.". The quest is a
/// number, a <c>|Hquest|</c> link or an exact title (the title form is ArcaneCore's, like <c>.additem</c>'s item name).
/// <para>
/// Deliberate differences from mangos, documented in docs/areas/quests-npc.md: the invoker must outrank the target
/// (<see cref="CommandContext.CanActOn"/>); <c>add</c> refuses a quest already in the log and a quest the support gate
/// withholds, and reports a full log or a refused source item to the invoker; <c>complete</c> refuses a failed quest;
/// <c>status</c> exists. The reputation objective of <c>complete</c> is settled through the reputation feature when it is
/// present (mangos: ReputationMgr::SetReputation); without it the objective is left as it is.
/// </para>
/// </summary>
public sealed class QuestCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("quest", AccountSecurity.GameMaster,
            "Syntax: .quest $subcommand\nType .quest to see the list of possible subcommands or .help quest $subcommand to see info on subcommands.",
            Children:
            [
                new ChatCommand("add", AccountSecurity.Administrator,
                    "Syntax: .quest add #quest_id|[$quest_title]|#shift-click-quest-link\nAdd the quest to the log of the selected player (or yourself) without its requirements; a quest started by an item is refused.",
                    Add, RetailLevel: 6),
                new ChatCommand("complete", AccountSecurity.Administrator,
                    "Syntax: .quest complete #quest_id|[$quest_title]|#shift-click-quest-link\nMark every objective of the quest done for the selected player (or yourself): the required items, kills, reputation and money are given.",
                    Complete, RetailLevel: 6),
                new ChatCommand("remove", AccountSecurity.Administrator,
                    "Syntax: .quest remove #quest_id|[$quest_title]|#shift-click-quest-link\nTake the quest out of the log of the selected player (or yourself), source item included, and forget that it was rewarded.",
                    Remove, RetailLevel: 6),
                new ChatCommand("status", AccountSecurity.GameMaster,
                    "Syntax: .quest status [#quest_id|[$quest_title]|#shift-click-quest-link]\nShow the quest log of the selected player (or yourself), or the progress of one quest.",
                    Status, RetailLevel: 3),
            ], RetailLevel: 3),
    ];

    private enum QuestRead
    {
        /// <summary>No argument: show the syntax.</summary>
        Syntax,

        /// <summary>An error reply was already sent.</summary>
        Replied,

        Ok,
    }

    /// <summary>
    /// The quest named by a number, an <c>Hquest</c> link (ExtractUint32KeyFromLink, QuestCommands.cpp:59-62) or an exact title
    /// (case-insensitive; the lowest id wins among duplicates). An unknown quest answers LANG_COMMAND_QUEST_NOTFOUND.
    /// </summary>
    private static QuestRead ReadQuest(CommandContext context, CommandArgs args, QuestStore quests, out GameQuest? quest)
    {
        quest = null;
        string? read = args.ExtractKeyFromLink("Hquest", out _, out _);
        if (read is null)
        {
            return QuestRead.Syntax;
        }

        if (new CommandArgs(read).ExtractUInt32(out uint questId))
        {
            quest = quests.Get(questId);
            if (quest is null)
            {
                context.Reply(QuestCommandText.NotFound(questId));
                return QuestRead.Replied;
            }

            return QuestRead.Ok;
        }

        quest = quests.All.Where(q => q.Title.Equals(read, StringComparison.OrdinalIgnoreCase)).MinBy(q => q.Id);
        if (quest is null)
        {
            context.Reply(GmStrings.CouldNotFind(read));
            return QuestRead.Replied;
        }

        return QuestRead.Ok;
    }

    /// <summary>The selected player or the invoker, allowed to be acted on; null after a reply.</summary>
    private static Player? Target(CommandContext context)
    {
        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(GmStrings.NoCharSelected);
            return null;
        }

        return context.CanActOn(target) ? target : null;
    }

    private static QuestNpcServices Services(CommandContext context) => context.Session.Services.GetRequiredService<QuestNpcFeature>().Services;

    // mangos checks the target before the arguments (getSelectedPlayer first, QuestCommands.cpp:49-62); an empty argument list
    // shows the syntax, and every refusal after it is a reply without the syntax line (SetSentErrorMessage).
    private static bool Add(CommandContext context, string text)
    {
        if (Target(context) is not { } target)
        {
            return true;
        }

        var args = new CommandArgs(text);
        if (args.IsEmpty)
        {
            return false;
        }

        QuestNpcServices services = Services(context);
        if (ReadQuest(context, args, services.Quests, out GameQuest? quest) != QuestRead.Ok)
        {
            return true; // mangos SetSentErrorMessage: no syntax line after "not found"
        }

        // Check item starting quest ("it can work incorrectly if added without item in inventory", QuestCommands.cpp:72-87).
        IItemTemplateStore templates = target.Inventory.Templates;
        uint startItem = LiveItemTemplateStore.Unwrap(templates)?.QuestStartingItem(quest!.Id) ?? templates.QuestStartingItem(quest!.Id);
        if (startItem != 0)
        {
            context.Reply(QuestCommandText.StartFromItem(quest.Id, startItem));
            return true;
        }

        string link = GmStrings.PlayerLink(target.Name);
        switch (services.GmAddQuest(target, quest))
        {
            case GmQuestAddResult.NotReady:
                context.Reply(QuestCommandText.NotReady(link));
                break;
            case GmQuestAddResult.AlreadyInLog:
                context.Reply(QuestCommandText.AlreadyInLog(quest.Id, link));
                break;
            case GmQuestAddResult.Unsupported:
                context.Reply(QuestCommandText.Withheld(quest.Id, services.MissingAdapters(quest)));
                break;
            case GmQuestAddResult.LogFull:
                context.Reply(QuestCommandText.LogFull(link));
                break;
            case GmQuestAddResult.SourceItemRefused:
                context.Reply(QuestCommandText.SourceItemRefused(quest.Id, link));
                break;
        }

        return true; // mangos: silent on success
    }

    private static bool Remove(CommandContext context, string text)
    {
        if (Target(context) is not { } target)
        {
            return true;
        }

        var args = new CommandArgs(text);
        if (args.IsEmpty)
        {
            return false;
        }

        QuestNpcServices services = Services(context);
        if (ReadQuest(context, args, services.Quests, out GameQuest? quest) != QuestRead.Ok)
        {
            return true;
        }

        context.Reply(services.GmRemoveQuest(target, quest!) ? QuestCommandText.Removed : QuestCommandText.NotReady(GmStrings.PlayerLink(target.Name)));
        return true;
    }

    private static bool Complete(CommandContext context, string text)
    {
        if (Target(context) is not { } target)
        {
            return true;
        }

        var args = new CommandArgs(text);
        if (args.IsEmpty)
        {
            return false;
        }

        QuestNpcServices services = Services(context);
        if (ReadQuest(context, args, services.Quests, out GameQuest? quest) != QuestRead.Ok)
        {
            return true;
        }

        // If the quest requires reputation to complete (QuestCommands.cpp:248-259): the reputation owner is a World feature.
        uint repFaction = quest!.Template.RepObjectiveFaction;
        if (repFaction != 0 && context.Session.Services.GetService<ReputationFeature>()?.Service is { } reputation
            && reputation.GetReputation(target, repFaction) < quest.Template.RepObjectiveValue)
        {
            reputation.SetReputation(target, repFaction, quest.Template.RepObjectiveValue);
        }

        CreatureWorldFeature? creatures = context.Session.Services.GetService<CreatureWorldFeature>();
        Func<uint, bool>? creatureExists = creatures is null ? null : entry => creatures.Content.FindTemplate(entry) is not null;
        switch (services.GmCompleteQuest(target, quest, creatureExists))
        {
            case GmQuestCompleteResult.NotReady:
                context.Reply(QuestCommandText.NotReady(GmStrings.PlayerLink(target.Name)));
                break;
            case GmQuestCompleteResult.NotOnQuest:
                context.Reply(QuestCommandText.NotFound(quest.Id)); // QuestCommands.cpp:178-183: the same text as an unknown quest
                break;
            case GmQuestCompleteResult.Failed:
                context.Reply(QuestCommandText.FailedQuest(quest.Id));
                break;
        }

        return true; // mangos: silent on success
    }

    /// <summary>ArcaneCore's: the log (slot, link, status) or one quest's counters, explored flag, timer and rewarded flag.</summary>
    private static bool Status(CommandContext context, string text)
    {
        if (Target(context) is not { } target)
        {
            return true;
        }

        QuestNpcServices services = Services(context);
        string link = GmStrings.PlayerLink(target.Name);
        if (services.StateOf(target) is not { Loaded: true } state)
        {
            context.Reply(QuestCommandText.NotReady(link));
            return true;
        }

        var args = new CommandArgs(text);
        if (args.IsEmpty)
        {
            var lines = new List<string>();
            for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
            {
                uint questId = state.Quests.SlotQuestId(slot);
                if (questId != 0 && services.Quests.Get(questId) is { } logged)
                {
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"  slot {slot}: {logged.Id} - {QuestCommandText.QuestLink(logged)} [{state.Quests.GetStatus(questId)}]"));
                }
            }

            context.Reply(QuestCommandText.LogOf(link, lines.Count));
            foreach (string line in lines)
            {
                context.Reply(line);
            }

            return true;
        }

        if (ReadQuest(context, args, services.Quests, out GameQuest? quest) != QuestRead.Ok)
        {
            return true;
        }

        context.Reply(Describe(quest!, state.Quests));
        return true;
    }

    /// <summary>One line: "id - link: Status, slot N, kills a/b ..., items a/b ..., explored, timer ends at T, rewarded".</summary>
    private static string Describe(GameQuest quest, PlayerQuestLog log)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{quest.Id} - {QuestCommandText.QuestLink(quest)}: {log.GetStatus(quest.Id)}");
        if (log.Get(quest.Id) is not { } data)
        {
            return sb.ToString();
        }

        int slot = log.FindSlot(quest.Id);
        if (slot < QuestConstants.MaxQuestLogSize)
        {
            sb.Append(CultureInfo.InvariantCulture, $", slot {slot}");
        }
        else
        {
            sb.Append(", not in the log");
        }
        for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
        {
            if (quest.ReqCreatureOrGOId[i] != 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $", {(quest.ReqCreatureOrGOId[i] > 0 ? "creature" : "object")} {Math.Abs((long)quest.ReqCreatureOrGOId[i])} {data.CreatureOrGOCount[i]}/{quest.ReqCreatureOrGOCount[i]}");
            }

            if (quest.ReqItemId[i] != 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $", item {quest.ReqItemId[i]} {data.ItemCount[i]}/{quest.ReqItemCount[i]}");
            }
        }

        if (quest.HasSpecialFlag(QuestSpecialFlags.ExplorationOrEvent))
        {
            sb.Append(data.Explored ? ", explored" : ", not explored");
        }

        if (data.TimerEndUnix != 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $", timer ends at {DateTimeOffset.FromUnixTimeSeconds(data.TimerEndUnix):u}");
        }

        if (data.Rewarded)
        {
            sb.Append(", rewarded");
        }

        return sb.ToString();
    }
}
