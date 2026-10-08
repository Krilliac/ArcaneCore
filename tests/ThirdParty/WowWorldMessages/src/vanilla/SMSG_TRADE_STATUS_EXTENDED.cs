using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_TRADE_STATUS_EXTENDED: VanillaServerMessage, IWorldMessage {
    /// <summary>
    /// cmangos/vmangos/mangoszero: send trader or own trade windows state (last need for proper show spell apply to non-trade slot)
    /// </summary>
    public required bool SelfPlayer { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: sets to 7
    /// cmangos/vmangos/mangoszero: trade slots count/number?, = next field in most cases
    /// </summary>
    public required uint TradeSlotCount1 { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: sets to 7
    /// cmangos/vmangos/mangoszero: trade slots count/number?, = prev field in most cases
    /// </summary>
    public required uint TradeSlotCount2 { get; set; }
    public required uint MoneyInTrade { get; set; }
    public required uint SpellOnLowestSlot { get; set; }
    /// <summary>
    /// vmangos/cmangos/mangoszero: All set to same as trade_slot_count* (7), unsure which determines how big this is. Unused slots are 0.
    /// </summary>
    public const int TradeSlotsLength = 7;
    public required TradeSlot[] TradeSlots { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteBool8(SelfPlayer, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TradeSlotCount1, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TradeSlotCount2, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MoneyInTrade, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(SpellOnLowestSlot, cancellationToken).ConfigureAwait(false);

        foreach (var v in TradeSlots) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 446, 289, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 446, 289, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_TRADE_STATUS_EXTENDED> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var selfPlayer = await r.ReadBool8(cancellationToken).ConfigureAwait(false);

        var tradeSlotCount1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var tradeSlotCount2 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var moneyInTrade = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var spellOnLowestSlot = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var tradeSlots = new TradeSlot[TradeSlotsLength];
        for (var i = 0; i < TradeSlotsLength; ++i) {
            tradeSlots[i] = await Vanilla.TradeSlot.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);
        }

        return new SMSG_TRADE_STATUS_EXTENDED {
            SelfPlayer = selfPlayer,
            TradeSlotCount1 = tradeSlotCount1,
            TradeSlotCount2 = tradeSlotCount2,
            MoneyInTrade = moneyInTrade,
            SpellOnLowestSlot = spellOnLowestSlot,
            TradeSlots = tradeSlots,
        };
    }

}

