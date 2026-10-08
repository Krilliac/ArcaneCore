using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_LOOT_MASTER_GIVE: VanillaClientMessage, IWorldMessage {
    public required ulong Loot { get; set; }
    public required byte SlotId { get; set; }
    public required ulong Player { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Loot, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(SlotId, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Player, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 21, 675, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 21, 675, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_LOOT_MASTER_GIVE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var loot = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var slotId = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var player = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new CMSG_LOOT_MASTER_GIVE {
            Loot = loot,
            SlotId = slotId,
            Player = player,
        };
    }

}

