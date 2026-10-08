using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LOOT_ALL_PASSED: VanillaServerMessage, IWorldMessage {
    public required ulong LootedTarget { get; set; }
    public required uint LootSlot { get; set; }
    public required uint Item { get; set; }
    public required uint ItemRandomPropertyId { get; set; }
    /// <summary>
    /// vmangos/mangoszero: Always set to 0.
    /// </summary>
    public required uint ItemRandomSuffixId { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(LootedTarget, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(LootSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomPropertyId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomSuffixId, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 26, 670, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 26, 670, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LOOT_ALL_PASSED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var lootedTarget = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var lootSlot = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomPropertyId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomSuffixId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_LOOT_ALL_PASSED {
            LootedTarget = lootedTarget,
            LootSlot = lootSlot,
            Item = item,
            ItemRandomPropertyId = itemRandomPropertyId,
            ItemRandomSuffixId = itemRandomSuffixId,
        };
    }

}

