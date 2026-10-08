using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_QUESTGIVER_REQUEST_ITEMS: VanillaServerMessage, IWorldMessage {
    public required ulong Npc { get; set; }
    public required uint QuestId { get; set; }
    public required string Title { get; set; }
    public required string RequestItemsText { get; set; }
    public required uint EmoteDelay { get; set; }
    public required uint Emote { get; set; }
    public required bool AutoFinish { get; set; }
    public required uint RequiredMoney { get; set; }
    public required List<QuestItemRequirement> RequiredItems { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: All emulators set to 0x02
    /// </summary>
    public required uint Unknown1 { get; set; }
    public required Vanilla.QuestCompletable Completable { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: set to 0x04
    /// </summary>
    public required uint Flags2 { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: set to 0x08
    /// </summary>
    public required uint Flags3 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Npc, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(QuestId, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Title, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(RequestItemsText, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(EmoteDelay, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Emote, cancellationToken).ConfigureAwait(false);

        await w.WriteBool32(AutoFinish, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RequiredMoney, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)RequiredItems.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in RequiredItems) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Completable, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Flags2, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Flags3, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 395, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 395, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_QUESTGIVER_REQUEST_ITEMS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var npc = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var questId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var title = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var requestItemsText = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var emoteDelay = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var emote = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var autoFinish = await r.ReadBool32(cancellationToken).ConfigureAwait(false);

        var requiredMoney = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfRequiredItems = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var requiredItems = new List<QuestItemRequirement>();
        for (var i = 0; i < amountOfRequiredItems; ++i) {
            requiredItems.Add(await Vanilla.QuestItemRequirement.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var completable = (Vanilla.QuestCompletable)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var flags2 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var flags3 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_QUESTGIVER_REQUEST_ITEMS {
            Npc = npc,
            QuestId = questId,
            Title = title,
            RequestItemsText = requestItemsText,
            EmoteDelay = emoteDelay,
            Emote = emote,
            AutoFinish = autoFinish,
            RequiredMoney = requiredMoney,
            RequiredItems = requiredItems,
            Unknown1 = unknown1,
            Completable = completable,
            Flags2 = flags2,
            Flags3 = flags3,
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

        // request_items_text: Generator.Generated.DataTypeCstring
        size += RequestItemsText.Length + 1;

        // emote_delay: Generator.Generated.DataTypeInteger
        size += 4;

        // emote: Generator.Generated.DataTypeInteger
        size += 4;

        // auto_finish: Generator.Generated.DataTypeBool
        size += 4;

        // required_money: Generator.Generated.DataTypeGold
        size += 4;

        // amount_of_required_items: Generator.Generated.DataTypeInteger
        size += 4;

        // required_items: Generator.Generated.DataTypeArray
        size += RequiredItems.Sum(e => 12);

        // unknown1: Generator.Generated.DataTypeInteger
        size += 4;

        // completable: Generator.Generated.DataTypeEnum
        size += 4;

        // flags2: Generator.Generated.DataTypeInteger
        size += 4;

        // flags3: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

