using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_DESTROYITEM: VanillaClientMessage, IWorldMessage {
    public required byte Bag { get; set; }
    public required byte Slot { get; set; }
    public required byte Amount { get; set; }
    public required byte Unknown1 { get; set; }
    public required byte Unknown2 { get; set; }
    public required byte Unknown3 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(Bag, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Slot, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Amount, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Unknown2, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Unknown3, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 10, 273, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 10, 273, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_DESTROYITEM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var bag = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var slot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var amount = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var unknown2 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var unknown3 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_DESTROYITEM {
            Bag = bag,
            Slot = slot,
            Amount = amount,
            Unknown1 = unknown1,
            Unknown2 = unknown2,
            Unknown3 = unknown3,
        };
    }

}

