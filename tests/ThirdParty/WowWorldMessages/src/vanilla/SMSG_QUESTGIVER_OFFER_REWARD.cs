using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_QUESTGIVER_OFFER_REWARD: VanillaServerMessage, IWorldMessage {
    public required ulong Npc { get; set; }
    public required uint QuestId { get; set; }
    public required string Title { get; set; }
    public required string OfferRewardText { get; set; }
    public required bool AutoFinish { get; set; }
    public required List<NpcTextUpdateEmote> Emotes { get; set; }
    public required List<QuestItemRequirement> ChoiceItemRewards { get; set; }
    public required List<QuestItemRequirement> ItemRewards { get; set; }
    public required uint MoneyReward { get; set; }
    public required uint RewardSpell { get; set; }
    /// <summary>
    /// mangoszero and cmangos disagree about which field is _cast, although they both agree that the _cast field should not be in zero (vanilla). They still both include both fields in the code though.
    /// </summary>
    public required uint RewardSpellCast { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Npc, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(QuestId, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Title, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(OfferRewardText, cancellationToken).ConfigureAwait(false);

        await w.WriteBool32(AutoFinish, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Emotes.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Emotes) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt((uint)ChoiceItemRewards.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in ChoiceItemRewards) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt((uint)ItemRewards.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in ItemRewards) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt(MoneyReward, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RewardSpell, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RewardSpellCast, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 397, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 397, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_QUESTGIVER_OFFER_REWARD> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var npc = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var questId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var title = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var offerRewardText = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var autoFinish = await r.ReadBool32(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfEmotes = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var emotes = new List<NpcTextUpdateEmote>();
        for (var i = 0; i < amountOfEmotes; ++i) {
            emotes.Add(await Vanilla.NpcTextUpdateEmote.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfChoiceItemRewards = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var choiceItemRewards = new List<QuestItemRequirement>();
        for (var i = 0; i < amountOfChoiceItemRewards; ++i) {
            choiceItemRewards.Add(await Vanilla.QuestItemRequirement.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfItemRewards = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRewards = new List<QuestItemRequirement>();
        for (var i = 0; i < amountOfItemRewards; ++i) {
            itemRewards.Add(await Vanilla.QuestItemRequirement.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        var moneyReward = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var rewardSpell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var rewardSpellCast = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_QUESTGIVER_OFFER_REWARD {
            Npc = npc,
            QuestId = questId,
            Title = title,
            OfferRewardText = offerRewardText,
            AutoFinish = autoFinish,
            Emotes = emotes,
            ChoiceItemRewards = choiceItemRewards,
            ItemRewards = itemRewards,
            MoneyReward = moneyReward,
            RewardSpell = rewardSpell,
            RewardSpellCast = rewardSpellCast,
        };
    }

    internal int Size() {
        var size = 0;

        // npc: Generator.Generated.DataTypeGuid
        size += 8;

        // quest_id: Generator.Generated.DataTypeInteger
        size += 4;

        // title: Generator.Generated.DataTypeCstring
        size += Title.Length + 1;

        // offer_reward_text: Generator.Generated.DataTypeCstring
        size += OfferRewardText.Length + 1;

        // auto_finish: Generator.Generated.DataTypeBool
        size += 4;

        // amount_of_emotes: Generator.Generated.DataTypeInteger
        size += 4;

        // emotes: Generator.Generated.DataTypeArray
        size += Emotes.Sum(e => 8);

        // amount_of_choice_item_rewards: Generator.Generated.DataTypeInteger
        size += 4;

        // choice_item_rewards: Generator.Generated.DataTypeArray
        size += ChoiceItemRewards.Sum(e => 12);

        // amount_of_item_rewards: Generator.Generated.DataTypeInteger
        size += 4;

        // item_rewards: Generator.Generated.DataTypeArray
        size += ItemRewards.Sum(e => 12);

        // money_reward: Generator.Generated.DataTypeGold
        size += 4;

        // reward_spell: Generator.Generated.DataTypeSpell
        size += 4;

        // reward_spell_cast: Generator.Generated.DataTypeSpell
        size += 4;

        return size;
    }

}

