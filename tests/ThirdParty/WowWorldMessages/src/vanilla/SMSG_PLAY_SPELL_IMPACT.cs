using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PLAY_SPELL_IMPACT: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    /// <summary>
    /// mangoszero/mangosone/azerothcore: index from SpellVisualKit.dbc. Used for visual effect on player with 0x016A
    /// </summary>
    public required uint SpellVisualKit { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(SpellVisualKit, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 14, 503, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 14, 503, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PLAY_SPELL_IMPACT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var spellVisualKit = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_PLAY_SPELL_IMPACT {
            Guid = guid,
            SpellVisualKit = spellVisualKit,
        };
    }

}

