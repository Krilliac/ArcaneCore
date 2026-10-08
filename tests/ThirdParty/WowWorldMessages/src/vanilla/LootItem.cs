using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class LootItem {
    public required byte Index { get; set; }
    public required uint Item { get; set; }
    public required Vanilla.LootSlotType Ty { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(Index, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Ty, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<LootItem> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var index = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var ty = (Vanilla.LootSlotType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new LootItem {
            Index = index,
            Item = item,
            Ty = ty,
        };
    }

}

