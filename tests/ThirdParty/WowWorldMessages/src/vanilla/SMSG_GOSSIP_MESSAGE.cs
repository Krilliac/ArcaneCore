using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GOSSIP_MESSAGE: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required uint TitleTextId { get; set; }
    public required List<GossipItem> Gossips { get; set; }
    public required List<QuestItem> Quests { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TitleTextId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Gossips.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Gossips) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt((uint)Quests.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Quests) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 381, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 381, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GOSSIP_MESSAGE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var titleTextId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfGossipItems = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var gossips = new List<GossipItem>();
        for (var i = 0; i < amountOfGossipItems; ++i) {
            gossips.Add(await Vanilla.GossipItem.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfQuests = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var quests = new List<QuestItem>();
        for (var i = 0; i < amountOfQuests; ++i) {
            quests.Add(await Vanilla.QuestItem.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_GOSSIP_MESSAGE {
            Guid = guid,
            TitleTextId = titleTextId,
            Gossips = gossips,
            Quests = quests,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // title_text_id: Generator.Generated.DataTypeInteger
        size += 4;

        // amount_of_gossip_items: Generator.Generated.DataTypeInteger
        size += 4;

        // gossips: Generator.Generated.DataTypeArray
        size += Gossips.Sum(e => e.Size());

        // amount_of_quests: Generator.Generated.DataTypeInteger
        size += 4;

        // quests: Generator.Generated.DataTypeArray
        size += Quests.Sum(e => e.Size());

        return size;
    }

}

