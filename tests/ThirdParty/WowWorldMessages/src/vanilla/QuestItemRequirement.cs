using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class QuestItemRequirement {
    public required uint Item { get; set; }
    public required uint ItemCount { get; set; }
    public required uint ItemDisplayId { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemCount, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemDisplayId, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<QuestItemRequirement> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemDisplayId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new QuestItemRequirement {
            Item = item,
            ItemCount = itemCount,
            ItemDisplayId = itemDisplayId,
        };
    }

}

