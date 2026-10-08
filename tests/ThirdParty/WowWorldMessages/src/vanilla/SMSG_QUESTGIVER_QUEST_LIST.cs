using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_QUESTGIVER_QUEST_LIST: VanillaServerMessage, IWorldMessage {
    public required ulong Npc { get; set; }
    public required string Title { get; set; }
    /// <summary>
    /// mangoszero: player emote
    /// </summary>
    public required uint EmoteDelay { get; set; }
    /// <summary>
    /// mangoszero: NPC emote
    /// </summary>
    public required uint Emote { get; set; }
    public required List<QuestItem> QuestItems { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Npc, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Title, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(EmoteDelay, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Emote, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)QuestItems.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in QuestItems) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 389, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 389, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_QUESTGIVER_QUEST_LIST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var npc = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var title = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var emoteDelay = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var emote = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfEntries = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var questItems = new List<QuestItem>();
        for (var i = 0; i < amountOfEntries; ++i) {
            questItems.Add(await Vanilla.QuestItem.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_QUESTGIVER_QUEST_LIST {
            Npc = npc,
            Title = title,
            EmoteDelay = emoteDelay,
            Emote = emote,
            QuestItems = questItems,
        };
    }

    internal int Size() {
        var size = 0;

        // npc: Generator.Generated.DataTypeGuid
        size += 8;

        // title: Generator.Generated.DataTypeCstring
        size += Title.Length + 1;

        // emote_delay: Generator.Generated.DataTypeInteger
        size += 4;

        // emote: Generator.Generated.DataTypeInteger
        size += 4;

        // amount_of_entries: Generator.Generated.DataTypeInteger
        size += 1;

        // quest_items: Generator.Generated.DataTypeArray
        size += QuestItems.Sum(e => e.Size());

        return size;
    }

}

