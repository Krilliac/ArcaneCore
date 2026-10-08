using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_TEXT_EMOTE: VanillaClientMessage, IWorldMessage {
    public required Vanilla.TextEmote TextEmote { get; set; }
    public required uint Emote { get; set; }
    /// <summary>
    /// Guid targeted by the client.
    /// </summary>
    public required ulong Target { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)TextEmote, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Emote, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Target, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 20, 260, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 20, 260, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_TEXT_EMOTE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var textEmote = (Vanilla.TextEmote)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var emote = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var target = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new CMSG_TEXT_EMOTE {
            TextEmote = textEmote,
            Emote = emote,
            Target = target,
        };
    }

}

