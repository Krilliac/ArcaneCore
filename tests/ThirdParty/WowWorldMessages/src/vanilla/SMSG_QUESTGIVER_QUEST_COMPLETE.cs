using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_QUESTGIVER_QUEST_COMPLETE: VanillaServerMessage, IWorldMessage {
    public required uint QuestId { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: set to 0x03
    /// </summary>
    public required uint Unknown { get; set; }
    public required uint ExperienceReward { get; set; }
    public required uint MoneyReward { get; set; }
    public required List<QuestItemReward> ItemRewards { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(QuestId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ExperienceReward, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MoneyReward, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)ItemRewards.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in ItemRewards) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 401, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 401, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_QUESTGIVER_QUEST_COMPLETE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var questId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var experienceReward = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var moneyReward = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfItemRewards = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRewards = new List<QuestItemReward>();
        for (var i = 0; i < amountOfItemRewards; ++i) {
            itemRewards.Add(await Vanilla.QuestItemReward.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_QUESTGIVER_QUEST_COMPLETE {
            QuestId = questId,
            Unknown = unknown,
            ExperienceReward = experienceReward,
            MoneyReward = moneyReward,
            ItemRewards = itemRewards,
        };
    }

    internal int Size() {
        var size = 0;

        // quest_id: Generator.Generated.DataTypeInteger
        size += 4;

        // unknown: Generator.Generated.DataTypeInteger
        size += 4;

        // experience_reward: Generator.Generated.DataTypeInteger
        size += 4;

        // money_reward: Generator.Generated.DataTypeGold
        size += 4;

        // amount_of_item_rewards: Generator.Generated.DataTypeInteger
        size += 4;

        // item_rewards: Generator.Generated.DataTypeArray
        size += ItemRewards.Sum(e => 8);

        return size;
    }

}

