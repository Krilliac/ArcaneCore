using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.ServerMail;
using ArcaneCore.World.Economy;

namespace ArcaneCore.World.ServerMail;

/// <summary>AzerothCore ServerMailConditionType, less Achievement and AccountFlags (neither exists in 1.12).</summary>
public enum ServerMailConditionType
{
    Invalid,
    Level,
    PlayTime,
    Quest,
    Reputation,
    Faction,
    Race,
    Class,
}

public readonly record struct ServerMailCondition(ServerMailConditionType Type, uint Value, uint State);

/// <summary>One loaded template (AzerothCore ServerMail).</summary>
public sealed record ServerMailTemplate(uint Id, uint SenderEntry, uint MoneyAlliance, uint MoneyHorde, string Subject, string Body,
    IReadOnlyList<ServerMailItem> ItemsAlliance, IReadOnlyList<ServerMailItem> ItemsHorde, IReadOnlyList<ServerMailCondition> Conditions);

/// <summary>What a condition reads about a character.</summary>
public interface IServerMailFacts
{
    uint Level { get; }
    uint PlayedSeconds { get; }
    Team Team { get; }
    Race Race { get; }
    Class Class { get; }

    /// <summary>The quest's status and whether it was rewarded; null when the quest log is not loaded.</summary>
    (QuestStatus Status, bool Rewarded)? Quest(uint questId);

    ReputationRank? Rank(uint factionId);
}

/// <summary>What the loader checks a row against.</summary>
public interface IServerMailValidation
{
    uint MaxLevel { get; }
    bool CreatureExists(uint entry);
    (uint Stackable, uint MaxCount)? Item(uint entry);
    bool QuestExists(uint questId);
    bool FactionExists(uint factionId);
}

/// <summary>
/// AzerothCore ServerMailMgr (src/server/game/Mails/ServerMailMgr.cpp): loading and validating mail_server_template and its items and
/// conditions, and ServerMailCondition::CheckCondition. A bad row is skipped with a message, as AzerothCore logs and skips it.
/// </summary>
public static class ServerMailRules
{
    /// <summary>MAX_MONEY_AMOUNT of 1.12 (vmangos Player.h: 0x7FFFFFFF).</summary>
    public const uint MaxMoney = int.MaxValue;

    /// <summary>The 1.12 status AzerothCore's QUEST_STATUS_REWARDED (6) stands for: rewarded is a flag here, not a status.</summary>
    public const uint QuestStateRewarded = 6;

    /// <summary>The playable race and class masks of 1.12 (races 1-8, classes 1-11 less 6 and 10).</summary>
    public const uint PlayableRaceMask = 0xFF, PlayableClassMask = 0x5DF;

    public static ServerMailConditionType ParseType(string name) => name switch
    {
        "Level" => ServerMailConditionType.Level,
        "PlayTime" => ServerMailConditionType.PlayTime,
        "Quest" => ServerMailConditionType.Quest,
        "Reputation" => ServerMailConditionType.Reputation,
        "Faction" => ServerMailConditionType.Faction,
        "Race" => ServerMailConditionType.Race,
        "Class" => ServerMailConditionType.Class,
        _ => ServerMailConditionType.Invalid,
    };

    /// <summary>LoadMailServerTemplates + Items + Conditions. Inactive templates are left out (AzerothCore skips them while loading).</summary>
    public static IReadOnlyList<ServerMailTemplate> Load(ServerMailContent content, IServerMailValidation check, Action<string> error)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(error);
        var kept = new Dictionary<uint, (ServerMailTemplateRow Row, uint Sender, List<ServerMailItem> A, List<ServerMailItem> H, List<ServerMailCondition> C)>();
        foreach (ServerMailTemplateRow row in content.Templates)
        {
            if (!row.Active) continue;
            if (row.MoneyAlliance > MaxMoney || row.MoneyHorde > MaxMoney)
            {
                error($"mail_server_template {row.Id}: moneyA {row.MoneyAlliance} or moneyH {row.MoneyHorde} above {MaxMoney}, skipped");
                continue;
            }

            uint sender = row.SenderEntry;
            if (sender != 0 && !check.CreatureExists(sender))
            {
                error($"mail_server_template {row.Id}: senderEntry {sender} has no creature_template, the default sender is used");
                sender = 0;
            }

            kept[row.Id] = (row, sender, [], [], []);
        }

        foreach (ServerMailItemRow item in content.Items)
        {
            if (!kept.TryGetValue(item.TemplateId, out var t))
            {
                error($"mail_server_template_items: templateID {item.TemplateId} is not an active template, skipped");
                continue;
            }

            if (check.Item(item.Item) is not { } template)
            {
                error($"mail_server_template_items: item {item.Item} of template {item.TemplateId} does not exist, skipped");
                continue;
            }

            if (item.ItemCount == 0 || item.ItemCount > template.Stackable || (template.MaxCount != 0 && item.ItemCount > template.MaxCount))
            {
                error($"mail_server_template_items: itemCount {item.ItemCount} of item {item.Item} is 0 or above its stack or max count, skipped");
                continue;
            }

            var entry = new ServerMailItem(item.Item, item.ItemCount);
            if (item.Faction == "Alliance") t.A.Add(entry);
            else if (item.Faction == "Horde") t.H.Add(entry);
            else error($"mail_server_template_items: faction '{item.Faction}' of template {item.TemplateId} is neither Alliance nor Horde, skipped");
        }

        foreach (ServerMailConditionRow row in content.Conditions)
        {
            if (!kept.TryGetValue(row.TemplateId, out var t))
            {
                error($"mail_server_template_conditions: templateID {row.TemplateId} is not an active template, skipped");
                continue;
            }

            ServerMailConditionType type = ParseType(row.ConditionType);
            if (Invalid(type, row, check) is { } reason)
            {
                error($"mail_server_template_conditions: template {row.TemplateId}: {reason}, skipped");
                continue;
            }

            t.C.Add(new ServerMailCondition(type, row.ConditionValue, row.ConditionState));
        }

        return kept.Values.Select(t => new ServerMailTemplate(t.Row.Id, t.Sender, t.Row.MoneyAlliance, t.Row.MoneyHorde, t.Row.Subject, t.Row.Body,
            t.A, t.H, t.C)).ToList();
    }

    private static string? Invalid(ServerMailConditionType type, ServerMailConditionRow row, IServerMailValidation check) => type switch
    {
        ServerMailConditionType.Invalid => $"unknown conditionType '{row.ConditionType}'",
        ServerMailConditionType.Level when row.ConditionValue > check.MaxLevel => $"level {row.ConditionValue} above the maximum {check.MaxLevel}",
        ServerMailConditionType.Quest when !check.QuestExists(row.ConditionValue) => $"quest {row.ConditionValue} does not exist",
        // QUEST_STATUS_NONE .. REWARDED, without the unused 2 and 4.
        ServerMailConditionType.Quest when row.ConditionState is 2 or 4 or > QuestStateRewarded => $"quest state {row.ConditionState} is not valid",
        ServerMailConditionType.Reputation when !check.FactionExists(row.ConditionValue) => $"faction {row.ConditionValue} does not exist",
        ServerMailConditionType.Reputation when row.ConditionState > (uint)ReputationRank.Exalted => $"reputation rank {row.ConditionState} is not valid",
        ServerMailConditionType.Faction when row.ConditionValue > 1 => $"team {row.ConditionValue} is neither 0 (Alliance) nor 1 (Horde)",
        ServerMailConditionType.Race when (row.ConditionValue & ~PlayableRaceMask) != 0 => $"race mask {row.ConditionValue} has unplayable races",
        ServerMailConditionType.Class when (row.ConditionValue & ~PlayableClassMask) != 0 => $"class mask {row.ConditionValue} has unplayable classes",
        _ => null,
    };

    /// <summary>
    /// ServerMailCondition::CheckCondition. A quest condition on a character whose quest log is not loaded does not pass (the letter stays
    /// owed and is tried again at the next login).
    /// </summary>
    public static bool Check(ServerMailCondition condition, IServerMailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        switch (condition.Type)
        {
            case ServerMailConditionType.Level:
                return facts.Level >= condition.Value;
            case ServerMailConditionType.PlayTime:
                return facts.PlayedSeconds >= condition.Value;
            case ServerMailConditionType.Quest:
            {
                if (facts.Quest(condition.Value) is not { } quest) return false;
                return condition.State == QuestStateRewarded ? quest.Rewarded : !quest.Rewarded && (uint)quest.Status == condition.State;
            }
            case ServerMailConditionType.Reputation:
                return facts.Rank(condition.Value) is { } rank && (uint)rank >= condition.State;
            case ServerMailConditionType.Faction:
                return (uint)facts.Team == condition.Value;
            case ServerMailConditionType.Race:
                return ((1u << ((int)facts.Race - 1)) & condition.Value) != 0;
            case ServerMailConditionType.Class:
                return ((1u << ((int)facts.Class - 1)) & condition.Value) != 0;
            default:
                return false;
        }
    }

    /// <summary>The templates a character is owed at login: active, not sent yet, every condition met.</summary>
    public static IEnumerable<ServerMailTemplate> Owed(IEnumerable<ServerMailTemplate> templates, IReadOnlyCollection<uint> sent, IServerMailFacts facts)
        => templates.Where(t => !sent.Contains(t.Id) && t.Conditions.All(c => Check(c, facts)));

    /// <summary>The letter of a template for a team (moneyA / itemsA for the Alliance, moneyH / itemsH for the Horde).</summary>
    public static ServerMailRequest Letter(ServerMailTemplate template, int receiverId, Team team) => team == Team.Alliance
        ? new ServerMailRequest(receiverId, template.SenderEntry, template.Subject, template.Body, template.MoneyAlliance, template.ItemsAlliance)
        : new ServerMailRequest(receiverId, template.SenderEntry, template.Subject, template.Body, template.MoneyHorde, template.ItemsHorde);
}
