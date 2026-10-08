using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LOOT_ROLL_WON: VanillaServerMessage, IWorldMessage {
    public required ulong LootedTarget { get; set; }
    public required uint LootSlot { get; set; }
    public required uint Item { get; set; }
    /// <summary>
    /// vmangos/mangoszero: not used ?
    /// </summary>
    public required uint ItemRandomSuffix { get; set; }
    public required uint ItemRandomPropertyId { get; set; }
    public required ulong WinningPlayer { get; set; }
    /// <summary>
    /// rollnumber related to SMSG_LOOT_ROLL
    /// </summary>
    public required byte WinningRoll { get; set; }
    /// <summary>
    /// Rolltype related to SMSG_LOOT_ROLL
    /// </summary>
    public required Vanilla.RollVote Vote { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(LootedTarget, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(LootSlot, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomSuffix, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomPropertyId, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(WinningPlayer, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(WinningRoll, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Vote, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 36, 671, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 36, 671, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LOOT_ROLL_WON> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var lootedTarget = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var lootSlot = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomSuffix = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomPropertyId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var winningPlayer = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var winningRoll = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var vote = (Vanilla.RollVote)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_LOOT_ROLL_WON {
            LootedTarget = lootedTarget,
            LootSlot = lootSlot,
            Item = item,
            ItemRandomSuffix = itemRandomSuffix,
            ItemRandomPropertyId = itemRandomPropertyId,
            WinningPlayer = winningPlayer,
            WinningRoll = winningRoll,
            Vote = vote,
        };
    }

}

