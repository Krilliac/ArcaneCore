using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_TEXT_EMOTE: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required Vanilla.TextEmote TextEmote { get; set; }
    public required uint Emote { get; set; }
    public required string Name { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)TextEmote, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Emote, cancellationToken).ConfigureAwait(false);

        await w.WriteSizedCString(Name, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 261, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 261, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_TEXT_EMOTE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var textEmote = (Vanilla.TextEmote)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var emote = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var name = await r.ReadSizedCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_TEXT_EMOTE {
            Guid = guid,
            TextEmote = textEmote,
            Emote = emote,
            Name = name,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // text_emote: Generator.Generated.DataTypeEnum
        size += 4;

        // emote: Generator.Generated.DataTypeInteger
        size += 4;

        // name: Generator.Generated.DataTypeSizedCstring
        size += Name.Length + 5;

        return size;
    }

}

