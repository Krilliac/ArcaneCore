using System.Collections.Frozen;
using System.Globalization;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using InventoryResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>How bot QA treats one refused interaction or quest exchange (<see cref="PlayerbotQaInteractionRules"/>).</summary>
/// <param name="Class">Expected (reported only when one bot keeps repeating it) or suspicious (reported at once).</param>
/// <param name="Cause">The likely cause of a suspicious refusal (an expected one that repeats is a bot that does not learn: <see cref="QaCause.BehaviourBug"/>).</param>
/// <param name="Token">The machine-readable reason (<see cref="QaSignal.Reason"/>): a fingerprint part, never changed once published.</param>
/// <param name="Words">A few words for a title (<c>vendor does not sell it</c>).</param>
/// <param name="Why">The cause text.</param>
internal sealed record QaInteractionRule(QaRefusalClass Class, QaCause Cause, string Token, string Words, string Why);

/// <summary>
/// The classification tables of <see cref="QaSignalKind.InteractionFailed"/> and <see cref="QaSignalKind.QuestBroken"/>: every decision site the game
/// reports (<see cref="InteractionFailure"/>, <see cref="QuestFailure"/>, with the protocol code the client got) as Expected (a refusal of play: no
/// money, bags full, sold out, too far; reported only when one bot gets it <see cref="PlayerbotQaInteractionMonitor.RepeatThreshold"/> times within
/// <see cref="PlayerbotQaInteractionMonitor.RepeatWindowMs"/>, as a bot that does not learn) or Suspicious (the vendor does not sell the item, the trainer
/// cannot teach a valid spell, the server has no handler; reported at once). Pure; the rules are built once.
/// </summary>
internal static class PlayerbotQaInteractionRules
{
    private const QaRefusalClass E = QaRefusalClass.Expected;
    private const QaRefusalClass S = QaRefusalClass.Suspicious;

    /// <summary>The reason of an objective that never progressed (a kill, a loot, a used object): a <see cref="QaSignalKind.QuestBroken"/> made by the monitor.</summary>
    internal const string NoProgressKill = "no-progress-kill", NoProgressItem = "no-progress-item", NoProgressObject = "no-progress-object";

    private static readonly FrozenDictionary<InteractionFailure, QaInteractionRule> s_interactions = new Dictionary<InteractionFailure, QaInteractionRule>
    {
        [InteractionFailure.BuyItemTemplateMissing] = new(S, QaCause.MissingData, "buy-no-item-template", "item has no template",
            "the bot asked to buy an item that has no item_template row (BUY_ERR_CANT_FIND_ITEM): a vendor list names an item that does not exist"),
        [InteractionFailure.BuyVendorUnreachable] = new(E, QaCause.BehaviourBug, "buy-vendor-unreachable", "vendor out of reach",
            "the vendor was out of reach, not a vendor, hostile or dead (BUY_ERR_DISTANCE_TOO_FAR)"),
        [InteractionFailure.BuyNotSold] = new(S, QaCause.BehaviourBug, "buy-not-sold", "vendor does not sell it",
            "the vendor does not list the item, or not for this character (npc_vendor row, condition, faction): the bot chose an item the server does not offer it"),
        [InteractionFailure.BuySoldOut] = new(E, QaCause.BehaviourBug, "buy-sold-out", "sold out", "the limited stock ran out (BUY_ERR_ITEM_ALREADY_SOLD)"),
        [InteractionFailure.BuyReputation] = new(E, QaCause.BehaviourBug, "buy-reputation", "reputation too low", "a reputation rank the bot lacks (BUY_ERR_REPUTATION_REQUIRE)"),
        [InteractionFailure.BuyHonorRank] = new(E, QaCause.BehaviourBug, "buy-rank", "rank too low", "an honor rank or level the bot lacks (BUY_ERR_RANK_REQUIRE)"),
        [InteractionFailure.BuyNoMoney] = new(E, QaCause.BehaviourBug, "buy-no-money", "not enough money", "not enough money (BUY_ERR_NOT_ENOUGHT_MONEY)"),
        [InteractionFailure.BuyCannotStore] = new(E, QaCause.BehaviourBug, "buy-cannot-store", "cannot store it", "the bought item does not fit (bags full, unique, ...)"),
        [InteractionFailure.BuyStoreFailed] = new(S, QaCause.NotImplemented, "buy-store-failed", "store failed silently",
            "the store check passed and storing the item then failed: nothing was bought and the client was told nothing"),
        [InteractionFailure.BuybackVendorUnreachable] = new(E, QaCause.BehaviourBug, "buyback-vendor-unreachable", "vendor out of reach", "the vendor was out of reach"),
        [InteractionFailure.BuybackEmptySlot] = new(S, QaCause.BehaviourBug, "buyback-empty-slot", "empty buyback slot", "the bot asked to buy back an empty buyback slot"),
        [InteractionFailure.BuybackNoMoney] = new(E, QaCause.BehaviourBug, "buyback-no-money", "not enough money", "not enough money for the buyback"),
        [InteractionFailure.BuybackCannotStore] = new(E, QaCause.BehaviourBug, "buyback-cannot-store", "cannot store it", "the bought-back item does not fit"),
        [InteractionFailure.SellVendorUnreachable] = new(E, QaCause.BehaviourBug, "sell-vendor-unreachable", "vendor out of reach", "the vendor was out of reach (SELL_ERR_CANT_FIND_VENDOR)"),
        [InteractionFailure.SellNoItemService] = new(S, QaCause.NotImplemented, "sell-no-item-service", "no item owner", "the server has no item owner to sell through"),
        [InteractionFailure.TrainerUnreachable] = new(E, QaCause.BehaviourBug, "train-unreachable", "trainer out of reach", "the trainer was out of reach, or not a trainer"),
        [InteractionFailure.TrainerNotForPlayer] = new(S, QaCause.BehaviourBug, "train-not-for-player", "trainer does not train this class",
            "the trainer does not train this character (another class, race or profession), yet the bot asked it"),
        [InteractionFailure.TrainerNoSpellService] = new(S, QaCause.NotImplemented, "train-no-spell-service", "no spell owner", "the server has no spell owner to teach through"),
        [InteractionFailure.TrainerSpellNotListed] = new(S, QaCause.BehaviourBug, "train-not-listed", "trainer does not teach it",
            "the trainer does not list the spell (npc_trainer): the bot asked for a spell the server does not offer it"),
        [InteractionFailure.TrainerSpellUnknown] = new(S, QaCause.MissingData, "train-unknown-spell", "teaching spell missing",
            "the trainer lists the spell but the teaching spell is not in the spell table"),
        [InteractionFailure.TrainerSpellNotReady] = new(E, QaCause.BehaviourBug, "train-not-ready", "not learnable yet",
            "the spell is not GREEN for the bot (level, skill or a previous rank missing; TRAIN_FAIL_NOT_ENOUGH_SKILL)"),
        [InteractionFailure.TrainerNoMoney] = new(E, QaCause.BehaviourBug, "train-no-money", "not enough money", "not enough money (TRAIN_FAIL_NOT_ENOUGH_MONEY)"),
        [InteractionFailure.TrainerTeachingFailed] = new(S, QaCause.NotImplemented, "train-teaching-failed", "trainer cannot teach a valid spell",
            "the spell is listed, GREEN for the bot's class and level, and affordable, and the teaching cast failed: the trainer cannot teach it"),
    }.ToFrozenDictionary();

    private static readonly FrozenSet<SellResult> s_suspiciousSells = new[] { SellResult.CantFindItem, SellResult.YouDontOwnThatItem, SellResult.Unk }.ToFrozenSet();

    /// <summary>Inventory results a bot should never get from its own actions: it acted on an item or slot it does not have, or one its class can never use.</summary>
    internal static FrozenSet<InventoryResult> SuspiciousInventory { get; } = new[]
    {
        InventoryResult.ItemNotFound, InventoryResult.ItemNotFound2, InventoryResult.SlotIsEmpty, InventoryResult.DontOwnThatItem, InventoryResult.IntBagError,
        InventoryResult.YouCanNeverUseThatItem, InventoryResult.YouCanNeverUseThatItem2, InventoryResult.ItemDoesntGoToSlot,
    }.ToFrozenSet();

    /// <summary>The rule of an interaction refusal with its protocol <paramref name="code"/>.</summary>
    internal static QaInteractionRule Of(InteractionFailure failure, int code)
    {
        switch (failure)
        {
            case InteractionFailure.SellRefused:
            {
                var sell = (SellResult)code;
                return s_suspiciousSells.Contains(sell)
                    ? new QaInteractionRule(S, sell == SellResult.Unk ? QaCause.NotImplemented : QaCause.BehaviourBug, "sell-refused", "sale refused",
                        $"the vendor refused the sale with {SellName(code)}: the bot sold an item it does not have, or the server could not say why")
                    : new QaInteractionRule(E, QaCause.BehaviourBug, "sell-refused", "sale refused", $"the vendor refused the sale with {SellName(code)}");
            }

            case InteractionFailure.InventoryChangeFailed:
                return SuspiciousInventory.Contains((InventoryResult)code)
                    ? new QaInteractionRule(S, QaCause.BehaviourBug, "inventory", "inventory change refused",
                        $"the server refused an inventory change with {InventoryName(code)}: the bot acted on an item or slot it does not have, or one its class can never use")
                    : new QaInteractionRule(E, QaCause.BehaviourBug, "inventory", "inventory change refused", $"the server refused an inventory change with {InventoryName(code)}");
            case InteractionFailure.LootRefused:
                return (LootResult)code == LootResult.Unsupported
                    ? new QaInteractionRule(S, QaCause.NotImplemented, "loot", "loot unsupported",
                        "the loot could not be opened because the server cannot store it (LootResult.Unsupported): the corpse or chest gives nothing")
                    : new QaInteractionRule(E, QaCause.BehaviourBug, "loot", "loot refused", $"the loot was refused with {LootName(code)}");
            case InteractionFailure.BuyCannotStore or InteractionFailure.BuybackCannotStore when SuspiciousInventory.Contains((InventoryResult)code):
                QaInteractionRule store = s_interactions[failure];
                return store with { Class = S, Why = store.Why + $" ({InventoryName(code)}, which a purchase should never get)" };
            default:
                return s_interactions.TryGetValue(failure, out QaInteractionRule? rule)
                    ? rule
                    : new QaInteractionRule(S, QaCause.Unknown, "interaction-" + ((int)failure).ToString(CultureInfo.InvariantCulture), "refused", "an interaction refusal with no rule");
        }
    }

    /// <summary>The rule of a reason token (<see cref="QaSignal.Reason"/> of an interaction finding) and its code; null when no rule has the token.</summary>
    internal static QaInteractionRule? OfToken(string token, int code)
    {
        foreach ((InteractionFailure failure, QaInteractionRule rule) in s_interactions)
        {
            if (string.Equals(rule.Token, token, StringComparison.Ordinal)) return Of(failure, code);
        }

        return token switch
        {
            "sell-refused" => Of(InteractionFailure.SellRefused, code),
            "inventory" => Of(InteractionFailure.InventoryChangeFailed, code),
            "loot" => Of(InteractionFailure.LootRefused, code),
            _ => null,
        };
    }

    /// <summary>The rule of a quest refusal with its QuestInvalidReason <paramref name="code"/>.</summary>
    internal static QaInteractionRule Of(QuestFailure failure, int code) => failure switch
    {
        QuestFailure.Unsupported => new(S, QaCause.NotImplemented, "unsupported", "quest not supported",
            "the server withholds the quest because it lacks behaviour the quest needs (QuestNpcServices.MissingAdapters), yet a bot picked it up"),
        QuestFailure.Invalid => new(E, QaCause.BehaviourBug, "invalid", "quest refused", $"the quest giver refused the quest with {QuestReasonName(code)} (QUESTGIVER_QUEST_INVALID)"),
        QuestFailure.TurnInRefused when (QuestInvalidReason)code == QuestInvalidReason.DontHaveReq => new(S, QaCause.BehaviourBug, "turn-in-refused",
            "turn-in refused", "the turn-in was refused with DontHaveReq: the bot turned in a quest the server does not hold complete, or its reward could not be settled"),
        QuestFailure.TurnInRefused => new(E, QaCause.BehaviourBug, "turn-in-refused", "turn-in refused", $"the turn-in was refused with {QuestReasonName(code)} (QUESTGIVER_QUEST_FAILED)"),
        _ => new(E, QaCause.Unknown, "failed", "quest keeps failing", "the quest failed (a timer ran out, an escort died or a script failed it)"),
    };

    /// <summary>The rule of an objective that never progressed (<see cref="NoProgressKill"/>, <see cref="NoProgressItem"/>, <see cref="NoProgressObject"/>).</summary>
    internal static QaInteractionRule NoProgress(string token) => token switch
    {
        NoProgressItem => new(S, QaCause.NotImplemented, NoProgressItem, "objective item never counted",
            "the bot received the objective item again and again and the quest never counted it (no SMSG_QUESTUPDATE_ADD_ITEM)"),
        NoProgressObject => new(S, QaCause.NotImplemented, NoProgressObject, "objective object never credited",
            "the bot used the objective game object again and again and the quest never credited it (no SMSG_QUESTUPDATE_ADD_KILL)"),
        _ => new(S, QaCause.NotImplemented, NoProgressKill, "objective kill never credited",
            "the bot killed the objective creature again and again and the quest never credited it (no SMSG_QUESTUPDATE_ADD_KILL)"),
    };

    /// <summary>The rule of a quest finding's reason token and code; null when no rule has the token.</summary>
    internal static QaInteractionRule? OfQuestToken(string token, int code) => token switch
    {
        "unsupported" => Of(QuestFailure.Unsupported, code),
        "invalid" => Of(QuestFailure.Invalid, code),
        "turn-in-refused" => Of(QuestFailure.TurnInRefused, code),
        "failed" => Of(QuestFailure.Failed, code),
        NoProgressKill or NoProgressItem or NoProgressObject => NoProgress(token),
        _ => null,
    };

    /// <summary>The name of an interaction's protocol code (<c>NotEnoughMoney (2)</c>).</summary>
    internal static string CodeName(string token, int code)
    {
        string name = token switch
        {
            _ when token.StartsWith("buy-", StringComparison.Ordinal) && token is not ("buy-cannot-store" or "buy-store-failed") => Named<BuyResult>(code),
            "buyback-vendor-unreachable" or "sell-vendor-unreachable" or "sell-no-item-service" or "sell-refused" => SellName(code),
            "buyback-empty-slot" or "buyback-no-money" => Named<BuyResult>(code),
            "buy-cannot-store" or "buyback-cannot-store" or "inventory" => InventoryName(code),
            "loot" => LootName(code),
            _ when token.StartsWith("train-", StringComparison.Ordinal) => code switch { 1 => "NotEnoughMoney", 2 => "NotEnoughSkill", _ => "Unavailable" },
            _ => string.Empty,
        };
        return name.Length == 0 ? string.Empty : name + string.Create(CultureInfo.InvariantCulture, $" ({code})");
    }

    internal static string QuestReasonName(int code) => Named<QuestInvalidReason>(code);

    private static string SellName(int code) => Named<SellResult>(code);

    private static string InventoryName(int code) => Named<InventoryResult>(code);

    private static string LootName(int code) => Named<LootResult>(code);

    private static string Named<T>(int code) where T : struct, Enum
    {
        foreach (T value in Enum.GetValues<T>())
        {
            if (Convert.ToInt64(value, CultureInfo.InvariantCulture) == code) return value.ToString();
        }

        return code.ToString(CultureInfo.InvariantCulture);
    }
}
