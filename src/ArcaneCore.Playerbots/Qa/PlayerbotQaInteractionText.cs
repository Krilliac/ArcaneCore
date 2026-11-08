using System.Globalization;
using System.Text;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// The words of the interaction kinds (<see cref="QaSignalKind.InteractionFailed"/>, <see cref="QaSignalKind.QuestBroken"/>,
/// <see cref="QaSignalKind.ObjectUnsupported"/>, <see cref="QaSignalKind.ScriptUnsupported"/>, <see cref="QaSignalKind.OpcodeUnhandled"/>): their subject
/// (the issue title), their classification with the names the content gives the ids (<see cref="IQaContentFacts"/>), and their issue section and
/// reproduction steps. Pure.
/// </summary>
internal static class PlayerbotQaInteractionText
{
    /// <summary>Whether <paramref name="kind"/> is one of the interaction kinds this class words.</summary>
    internal static bool Covers(QaSignalKind kind)
        => kind is QaSignalKind.InteractionFailed or QaSignalKind.QuestBroken or QaSignalKind.ObjectUnsupported or QaSignalKind.ScriptUnsupported
            or QaSignalKind.OpcodeUnhandled;

    // --- subjects ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The subject of an interaction-kind signal (<see cref="PlayerbotQaAggregator.Subject"/>).</summary>
    internal static string Subject(QaSignal s, string? questTitle) => s.Kind switch
    {
        QaSignalKind.InteractionFailed => InteractionSubject(s),
        QaSignalKind.QuestBroken => QuestSubject(s, questTitle),
        QaSignalKind.ObjectUnsupported => string.Create(CultureInfo.InvariantCulture, $"game object {s.GameObject} ({TypeName(s.Code)}): ") + GapWords(s.Reason),
        QaSignalKind.ScriptUnsupported => ScriptSubject(s),
        QaSignalKind.OpcodeUnhandled => OpcodeName(s) + (s.Reason == "session-only" ? ": a session-task handler only, refused on the bots' world path" : ": no handler"),
        _ => s.Kind.ToString(),
    };

    private static string InteractionSubject(QaSignal s)
    {
        QaInteractionRule? rule = PlayerbotQaInteractionRules.OfToken(s.Reason, s.Code);
        string code = PlayerbotQaInteractionRules.CodeName(s.Reason, s.Code);
        var sb = new StringBuilder(s.Reason);
        if (s.Npc != 0) sb.Append(CultureInfo.InvariantCulture, $" creature {s.Npc}");
        if (s.GameObject != 0) sb.Append(CultureInfo.InvariantCulture, $" object {s.GameObject}");
        if (s.Item != 0) sb.Append(CultureInfo.InvariantCulture, $" item {s.Item}");
        if (s.Spell != 0) sb.Append(CultureInfo.InvariantCulture, $" spell {s.Spell}");
        sb.Append(": ").Append(rule?.Words ?? "refused");
        if (code.Length != 0) sb.Append(" (").Append(code).Append(')');
        return sb.ToString();
    }

    private static string QuestSubject(QaSignal s, string? title)
    {
        string quest = string.Create(CultureInfo.InvariantCulture, $"quest {s.Quest}") + (title is null ? string.Empty : " (" + title + ")");
        QaInteractionRule? rule = PlayerbotQaInteractionRules.OfQuestToken(s.Reason, s.Code);
        string words = rule?.Words ?? s.Reason;
        if (s.EffectIndex >= 0)
        {
            string what = s.Item != 0 ? string.Create(CultureInfo.InvariantCulture, $"item {s.Item}")
                : s.GameObject != 0 ? string.Create(CultureInfo.InvariantCulture, $"object {s.GameObject}")
                : string.Create(CultureInfo.InvariantCulture, $"creature {s.Npc}");
            return quest + string.Create(CultureInfo.InvariantCulture, $" objective {s.EffectIndex}: {words} ({what})");
        }

        return s.Reason is "invalid" or "turn-in-refused"
            ? quest + ": " + words + " with " + PlayerbotQaInteractionRules.QuestReasonName(s.Code)
            : quest + ": " + words;
    }

    private static string ScriptSubject(QaSignal s)
    {
        string who = s.Npc != 0 ? string.Create(CultureInfo.InvariantCulture, $"creature {s.Npc}") : "an instance or object script";
        return s.Reason switch
        {
            "ai-name" => who + ": unknown AIName '" + s.Detail + "'",
            "event-ai" => who + ": unsupported EventAI " + s.Detail,
            "summon" => who + string.Create(CultureInfo.InvariantCulture, $" summons missing creature_template {s.Target}"),
            "escort" => who + ": escort with no points",
            _ => who + ": " + s.Reason,
        };
    }

    private static string OpcodeName(QaSignal s)
        => (s.Detail.Length != 0 ? s.Detail : WorldOpcodeNames.GetName((WorldOpcode)s.Code)) + string.Create(CultureInfo.InvariantCulture, $" (0x{s.Code:X3})");

    internal static string TypeName(int type)
        => string.Create(CultureInfo.InvariantCulture, $"type {type}") + (Enum.IsDefined((GameObjectType)type) ? " " + (GameObjectType)type : string.Empty);

    /// <summary>The words of a game object gap (<see cref="Game.Diagnostics.IGameQaObserver.OnObjectUnsupported"/>).</summary>
    internal static string GapWords(string gap) => gap switch
    {
        "type" => "the type has no use handler",
        "quest-menu" => "the quest giver object opened no quest menu",
        "loot-service" => "the map has no loot service for the chest",
        "instance-chest" => "an instance chest whose loot cannot be stored",
        "loot-store" => "the chest's loot cannot be stored (durable loot unsupported)",
        "unit-use" => "the type cannot be used by a unit",
        "spell-action" => "the ACTIVATE_OBJECT spell action is not implemented",
        _ => gap,
    };

    // --- classification ------------------------------------------------------------------------------------------------------------------------

    /// <summary>The likely cause of an interaction-kind signal, with the content's names (<see cref="PlayerbotQaClassifier.Classify"/>).</summary>
    internal static QaClassification Classify(QaSignal s, IQaContentFacts facts, string? title)
    {
        switch (s.Kind)
        {
            case QaSignalKind.InteractionFailed:
            {
                QaInteractionRule? rule = PlayerbotQaInteractionRules.OfToken(s.Reason, s.Code);
                string ids = Ids(s, facts);
                if (rule is null) return new QaClassification(QaCause.Unknown, ids + ": refused with " + s.Reason, title);
                string why = rule.Class == QaRefusalClass.Expected
                    ? "an ordinary refusal of play, but one bot kept asking and kept getting it" + (s.Detail.Length != 0 ? " (" + s.Detail + ")" : string.Empty)
                        + ": the bot does not learn from the refusal; " + rule.Why
                    : rule.Why;
                return new QaClassification(rule.Class == QaRefusalClass.Expected ? QaCause.BehaviourBug : rule.Cause, ids + ": " + why, title);
            }

            case QaSignalKind.QuestBroken:
            {
                QaInteractionRule? rule = PlayerbotQaInteractionRules.OfQuestToken(s.Reason, s.Code);
                string objectives = facts.QuestObjectives(s.Quest);
                string subject = string.Create(CultureInfo.InvariantCulture, $"quest {s.Quest}") + (title is null ? string.Empty : " (" + title + ")");
                if (s.EffectIndex >= 0)
                {
                    subject += string.Create(CultureInfo.InvariantCulture, $" objective {s.EffectIndex}, ") + Ids(s, facts);
                }

                string why = rule is null ? "refused with " + s.Reason
                    : rule.Class == QaRefusalClass.Expected
                        ? rule.Why + ", again and again for one bot" + (s.Detail.Length != 0 ? " (" + s.Detail + ")" : string.Empty)
                        : rule.Why + (s.Detail.Length != 0 ? " (" + s.Detail + ")" : string.Empty);
                QaCause cause = rule is null ? QaCause.Unknown : rule.Class == QaRefusalClass.Expected && rule.Cause != QaCause.Unknown ? QaCause.BehaviourBug : rule.Cause;
                return new QaClassification(cause, subject + ": " + why + (objectives.Length != 0 ? ". Objectives: " + objectives : string.Empty), title);
            }

            case QaSignalKind.ObjectUnsupported:
                return new QaClassification(QaCause.NotImplemented, string.Create(CultureInfo.InvariantCulture, $"game object {s.GameObject}")
                    + Named(facts.GameObjectName(s.GameObject)) + " (" + TypeName(s.Code) + "): " + GapWords(s.Reason)
                    + (s.Detail.Length != 0 ? " (value " + s.Detail + ")" : string.Empty)
                    + "; the use does nothing (GameObjectUseResult.Unsupported) where vmangos GameObject::Use acts", title);
            case QaSignalKind.ScriptUnsupported:
            {
                string who = s.Npc != 0 ? string.Create(CultureInfo.InvariantCulture, $"creature {s.Npc}") + Named(facts.CreatureName(s.Npc)) : "an instance or object script";
                return s.Reason switch
                {
                    "ai-name" => new QaClassification(QaCause.NotImplemented, who + " uses AIName '" + s.Detail + "', which the server has no AI for: it runs the default AI", title),
                    "event-ai" => new QaClassification(QaCause.NotImplemented, who + " has creature_ai_scripts rows the server does not implement (" + s.Detail + "): those parts are skipped", title),
                    "summon" => new QaClassification(QaCause.MissingData, who + string.Create(CultureInfo.InvariantCulture, $" summons creature {s.Target}")
                        + Named(facts.CreatureName(s.Target)) + ", which has no creature_template row: the summon is skipped (" + s.Detail + ")", title),
                    "escort" => new QaClassification(QaCause.MissingData, who + " starts an escort with no waypoints (" + s.Detail + "): the escort never starts", title),
                    _ => new QaClassification(QaCause.Unknown, who + ": " + s.Reason + " " + s.Detail, title),
                };
            }

            case QaSignalKind.OpcodeUnhandled:
                return s.Reason == "session-only"
                    ? new QaClassification(QaCause.BehaviourBug, OpcodeName(s) + " has a handler on the session task only, and the bots' managed path (TryManagedAction) runs world handlers: the bot's request is dropped", title)
                    : new QaClassification(QaCause.NotImplemented, OpcodeName(s) + " has no handler: the server ignores what the bot asked", title);
            default:
                throw new ArgumentOutOfRangeException(nameof(s), s.Kind, "not an interaction kind");
        }
    }

    /// <summary>The ids of a signal with their names: <c>creature 3312 (Ellandrieth), item 159 (Refreshing Spring Water)</c>.</summary>
    private static string Ids(QaSignal s, IQaContentFacts facts)
    {
        var parts = new List<string>(4);
        if (s.Npc != 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"creature {s.Npc}") + Named(facts.CreatureName(s.Npc)));
        if (s.GameObject != 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"object {s.GameObject}") + Named(facts.GameObjectName(s.GameObject)));
        if (s.Item != 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"item {s.Item}") + Named(facts.ItemName(s.Item)));
        if (s.Spell != 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"spell {s.Spell}"));
        string code = s.Kind == QaSignalKind.InteractionFailed ? PlayerbotQaInteractionRules.CodeName(s.Reason, s.Code) : string.Empty;
        return (parts.Count != 0 ? string.Join(", ", parts) : s.Reason) + (code.Length != 0 && parts.Count != 0 ? " [" + code + "]" : code.Length != 0 ? " [" + code + "]" : string.Empty);
    }

    private static string Named(string? name) => string.IsNullOrEmpty(name) ? string.Empty : " (" + name + ")";

    // --- the issue -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>The <c>## Interaction</c> table of an interaction-kind issue: the ids, the reason and the code.</summary>
    internal static void AppendSection(StringBuilder sb, QaFindingView f, Func<string, string> redact)
    {
        sb.Append("## Interaction\n\n| Field | Value |\n|---|---|\n");
        Row(sb, "Reason", f.Reason.Length != 0 ? f.Reason : "unknown");
        if (f.Quest != 0) Row(sb, "Quest", f.Quest.ToString(CultureInfo.InvariantCulture) + (f.QuestTitle is null ? string.Empty : " " + redact(f.QuestTitle)));
        if (f.Kind == QaSignalKind.QuestBroken && f.EffectIndex >= 0) Row(sb, "Objective", f.EffectIndex.ToString(CultureInfo.InvariantCulture));
        if (f.Npc != 0) Row(sb, "Creature", f.Npc.ToString(CultureInfo.InvariantCulture));
        if (f.GameObject != 0) Row(sb, "Game object", f.GameObject.ToString(CultureInfo.InvariantCulture));
        if (f.Item != 0) Row(sb, "Item", f.Item.ToString(CultureInfo.InvariantCulture));
        if (f.Spell != 0) Row(sb, "Spell", f.Spell.ToString(CultureInfo.InvariantCulture));
        if (f.Target != 0) Row(sb, f.Kind == QaSignalKind.ScriptUnsupported ? "Summoned entry" : "Target", f.Target.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Bots that met it", f.DistinctBots.ToString(CultureInfo.InvariantCulture) + (f.BotIndependent ? " (a content finding: one occurrence is enough)" : string.Empty));
        Row(sb, "Classes", f.ClassMask != 0 ? f.ClassNames : "unknown");
        sb.Append('\n');
    }

    /// <summary>The reproduction steps of an interaction-kind issue, with the GM commands this server has.</summary>
    internal static void AppendReproduction(StringBuilder sb, QaFindingView f)
    {
        switch (f.Kind)
        {
            case QaSignalKind.InteractionFailed:
                if (f.Npc != 0) sb.Append(CultureInfo.InvariantCulture, $"- Go to the creature: `.go creature id {f.Npc}` (or place one: `.npc add {f.Npc}`).\n");
                if (f.GameObject != 0) sb.Append(CultureInfo.InvariantCulture, $"- Go to the object: `.go object id {f.GameObject}`.\n");
                if (f.Item != 0 && f.Reason.StartsWith("buy", StringComparison.Ordinal))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"- Open the vendor and buy item {f.Item} (`.lookup item` finds its name); `.modify money` gives the money.\n");
                }
                else if (f.Item != 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"- Get the item: `.additem {f.Item}`, then repeat what the bot did ({f.Reason}).\n");
                }

                if (f.Spell != 0) sb.Append(CultureInfo.InvariantCulture, $"- Open the trainer and learn spell {f.Spell} on a character of a class that met it.\n");
                break;
            case QaSignalKind.QuestBroken:
                sb.Append(CultureInfo.InvariantCulture, $"- Take the quest: `.quest add {f.Quest}`; watch it with `.quest status {f.Quest}`.\n");
                if (f.Npc != 0) sb.Append(CultureInfo.InvariantCulture, $"- Go to the objective creature: `.go creature id {f.Npc}` and kill it.\n");
                if (f.GameObject != 0) sb.Append(CultureInfo.InvariantCulture, $"- Go to the objective object: `.go object id {f.GameObject}` and use it.\n");
                if (f.Item != 0) sb.Append(CultureInfo.InvariantCulture, $"- Get the objective item: `.additem {f.Item}` (the quest log should count it).\n");
                break;
            case QaSignalKind.ObjectUnsupported:
                sb.Append(CultureInfo.InvariantCulture, $"- Go to the object: `.go object id {f.GameObject}` (or place one: `.gobject add {f.GameObject}`) and use it.\n");
                break;
            case QaSignalKind.ScriptUnsupported:
                if (f.Npc != 0) sb.Append(CultureInfo.InvariantCulture, $"- Go to the creature: `.go creature id {f.Npc}` (or place one: `.npc add {f.Npc}`) and engage it.\n");
                if (f.Target != 0) sb.Append(CultureInfo.InvariantCulture, $"- The missing creature_template is entry {f.Target}.\n");
                break;
            case QaSignalKind.OpcodeUnhandled:
                sb.Append("- A bot sends it during ordinary play; the opcode table (OpcodeTable) has no world handler for it.\n");
                break;
        }
    }

    private static void Row(StringBuilder sb, string name, string value)
        => sb.Append("| ").Append(name).Append(" | ").Append(value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)).Append(" |\n");
}
