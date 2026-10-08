using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_NPC_TEXT_UPDATE: VanillaServerMessage, IWorldMessage {
    public required uint TextId { get; set; }
    public const int TextsLength = 8;
    public required NpcTextUpdate[] Texts { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(TextId, cancellationToken).ConfigureAwait(false);

        foreach (var v in Texts) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 384, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 384, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_NPC_TEXT_UPDATE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var textId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var texts = new NpcTextUpdate[TextsLength];
        for (var i = 0; i < TextsLength; ++i) {
            texts[i] = await Vanilla.NpcTextUpdate.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);
        }

        return new SMSG_NPC_TEXT_UPDATE {
            TextId = textId,
            Texts = texts,
        };
    }

    internal int Size() {
        var size = 0;

        // text_id: Generator.Generated.DataTypeInteger
        size += 4;

        // texts: Generator.Generated.DataTypeArray
        size += Texts.Sum(e => e.Size());

        return size;
    }

}

