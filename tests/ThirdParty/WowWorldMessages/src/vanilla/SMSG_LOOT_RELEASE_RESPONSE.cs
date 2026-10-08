using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LOOT_RELEASE_RESPONSE: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    /// <summary>
    /// Set to 1 on mangoszero/vmangos/cmangos/azerothcraft/mangosone/mangostwo/arcemu
    /// </summary>
    public required byte Unknown1 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Unknown1, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 11, 353, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 11, 353, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LOOT_RELEASE_RESPONSE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_LOOT_RELEASE_RESPONSE {
            Guid = guid,
            Unknown1 = unknown1,
        };
    }

}

