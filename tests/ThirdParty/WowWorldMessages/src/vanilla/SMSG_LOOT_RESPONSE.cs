using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using LootMethodType = OneOf.OneOf<SMSG_LOOT_RESPONSE.LootMethodError, LootMethod>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LOOT_RESPONSE: VanillaServerMessage, IWorldMessage {
    public class LootMethodError {
        public required Vanilla.LootMethodError LootError { get; set; }
    }
    public required ulong Guid { get; set; }
    public required LootMethodType LootMethod { get; set; }
    internal LootMethod LootMethodValue => LootMethod.Match(
        _ => Vanilla.LootMethod.Error,
        v => v
    );
    public required uint Gold { get; set; }
    public required List<LootItem> Items { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)LootMethodValue, cancellationToken).ConfigureAwait(false);

        if (LootMethod.Value is SMSG_LOOT_RESPONSE.LootMethodError lootMethodError) {
            await w.WriteByte((byte)lootMethodError.LootError, cancellationToken).ConfigureAwait(false);

        }

        await w.WriteUInt(Gold, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Items.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Items) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 352, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 352, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LOOT_RESPONSE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        LootMethodType lootMethod = (Vanilla.LootMethod)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (lootMethod.Value is Vanilla.LootMethod.Error) {
            var lootError = (Vanilla.LootMethodError)await r.ReadByte(cancellationToken).ConfigureAwait(false);

            lootMethod = new LootMethodError {
                LootError = lootError,
            };
        }

        var gold = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfItems = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var items = new List<LootItem>();
        for (var i = 0; i < amountOfItems; ++i) {
            items.Add(await Vanilla.LootItem.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_LOOT_RESPONSE {
            Guid = guid,
            LootMethod = lootMethod,
            Gold = gold,
            Items = items,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // loot_method: Generator.Generated.DataTypeEnum
        size += 1;

        if (LootMethod.Value is SMSG_LOOT_RESPONSE.LootMethodError lootMethodError) {
            // loot_error: Generator.Generated.DataTypeEnum
            size += 1;

        }

        // gold: Generator.Generated.DataTypeGold
        size += 4;

        // amount_of_items: Generator.Generated.DataTypeInteger
        size += 1;

        // items: Generator.Generated.DataTypeArray
        size += Items.Sum(e => 6);

        return size;
    }

}

