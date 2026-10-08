using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class GossipItem {
    /// <summary>
    /// vmangos: sets to loop index
    /// </summary>
    public required uint Id { get; set; }
    public required byte ItemIcon { get; set; }
    /// <summary>
    /// vmangos: makes pop up box password
    /// </summary>
    public required bool Coded { get; set; }
    public required string Message { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Id, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(ItemIcon, cancellationToken).ConfigureAwait(false);

        await w.WriteBool8(Coded, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Message, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<GossipItem> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var id = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemIcon = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var coded = await r.ReadBool8(cancellationToken).ConfigureAwait(false);

        var message = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new GossipItem {
            Id = id,
            ItemIcon = itemIcon,
            Coded = coded,
            Message = message,
        };
    }

    internal int Size() {
        var size = 0;

        // id: Generator.Generated.DataTypeInteger
        size += 4;

        // item_icon: Generator.Generated.DataTypeInteger
        size += 1;

        // coded: Generator.Generated.DataTypeBool
        size += 1;

        // message: Generator.Generated.DataTypeCstring
        size += Message.Length + 1;

        return size;
    }

}

