using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LOOT_START_ROLL: VanillaServerMessage, IWorldMessage {
    public required ulong Creature { get; set; }
    public required uint LootSlot { get; set; }
    public required uint Item { get; set; }
    /// <summary>
    /// vmangos/mangoszero: not used ?
    /// </summary>
    public required uint ItemRandomSuffix { get; set; }
    public required uint ItemRandomPropertyId { get; set; }
    public required uint CountdownTime { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Creature, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(LootSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomSuffix, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomPropertyId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CountdownTime, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 30, 673, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 30, 673, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LOOT_START_ROLL> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var creature = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var lootSlot = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomSuffix = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomPropertyId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var countdownTime = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_LOOT_START_ROLL {
            Creature = creature,
            LootSlot = lootSlot,
            Item = item,
            ItemRandomSuffix = itemRandomSuffix,
            ItemRandomPropertyId = itemRandomPropertyId,
            CountdownTime = countdownTime,
        };
    }

}

