using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LIST_INVENTORY: VanillaServerMessage, IWorldMessage {
    public required ulong Vendor { get; set; }
    public required List<ListInventoryItem> Items { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Vendor, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Items.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Items) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 415, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 415, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LIST_INVENTORY> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var vendor = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfItems = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var items = new List<ListInventoryItem>();
        for (var i = 0; i < amountOfItems; ++i) {
            items.Add(await Vanilla.ListInventoryItem.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_LIST_INVENTORY {
            Vendor = vendor,
            Items = items,
        };
    }

    internal int Size() {
        var size = 0;

        // vendor: Generator.Generated.DataTypeGuid
        size += 8;

        // amount_of_items: Generator.Generated.DataTypeInteger
        size += 1;

        // items: Generator.Generated.DataTypeArray
        size += Items.Sum(e => 28);

        return size;
    }

}

