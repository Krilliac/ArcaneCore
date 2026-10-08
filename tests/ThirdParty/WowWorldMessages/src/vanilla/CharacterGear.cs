using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class CharacterGear {
    public required uint EquipmentDisplayId { get; set; }
    public required Vanilla.InventoryType InventoryType { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(EquipmentDisplayId, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)InventoryType, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<CharacterGear> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var equipmentDisplayId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var inventoryType = (Vanilla.InventoryType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CharacterGear {
            EquipmentDisplayId = equipmentDisplayId,
            InventoryType = inventoryType,
        };
    }

}

