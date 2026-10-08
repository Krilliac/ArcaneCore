using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_ITEM_ENCHANT_TIME_UPDATE: VanillaServerMessage, IWorldMessage {
    public required ulong Item { get; set; }
    /// <summary>
    /// Possibly used with EnchantmentSlot enum.
    /// </summary>
    public required uint Slot { get; set; }
    public required uint Duration { get; set; }
    public required ulong Player { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Slot, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Duration, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Player, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 26, 491, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 26, 491, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_ITEM_ENCHANT_TIME_UPDATE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var item = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var slot = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var duration = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var player = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new SMSG_ITEM_ENCHANT_TIME_UPDATE {
            Item = item,
            Slot = slot,
            Duration = duration,
            Player = player,
        };
    }

}

