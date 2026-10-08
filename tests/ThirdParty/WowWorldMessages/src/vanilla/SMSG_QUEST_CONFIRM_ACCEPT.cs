using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_QUEST_CONFIRM_ACCEPT: VanillaServerMessage, IWorldMessage {
    public required uint QuestId { get; set; }
    public required string QuestTitle { get; set; }
    public required ulong Guid { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(QuestId, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(QuestTitle, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 412, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 412, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_QUEST_CONFIRM_ACCEPT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var questId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var questTitle = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new SMSG_QUEST_CONFIRM_ACCEPT {
            QuestId = questId,
            QuestTitle = questTitle,
            Guid = guid,
        };
    }

    internal int Size() {
        var size = 0;

        // quest_id: Generator.Generated.DataTypeInteger
        size += 4;

        // quest_title: Generator.Generated.DataTypeCstring
        size += QuestTitle.Length + 1;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        return size;
    }

}

