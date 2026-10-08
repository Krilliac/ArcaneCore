using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_QUESTGIVER_QUEST_FAILED: VanillaServerMessage, IWorldMessage {
    public required uint QuestId { get; set; }
    public required Vanilla.QuestFailedReason Reason { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(QuestId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Reason, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 10, 402, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 10, 402, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_QUESTGIVER_QUEST_FAILED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var questId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var reason = (Vanilla.QuestFailedReason)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_QUESTGIVER_QUEST_FAILED {
            QuestId = questId,
            Reason = reason,
        };
    }

}

