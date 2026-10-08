using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class StabledPet {
    public required uint PetNumber { get; set; }
    public required uint Entry { get; set; }
    public required uint Level { get; set; }
    public required string Name { get; set; }
    public required uint Loyalty { get; set; }
    /// <summary>
    /// vmangos/mangoszero/cmangos: client slot 1 == current pet (0)
    /// </summary>
    public required byte Slot { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(PetNumber, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Entry, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Level, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Name, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Loyalty, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Slot, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<StabledPet> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var petNumber = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var entry = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var level = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var name = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var loyalty = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var slot = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new StabledPet {
            PetNumber = petNumber,
            Entry = entry,
            Level = level,
            Name = name,
            Loyalty = loyalty,
            Slot = slot,
        };
    }

    internal int Size() {
        var size = 0;

        // pet_number: Generator.Generated.DataTypeInteger
        size += 4;

        // entry: Generator.Generated.DataTypeInteger
        size += 4;

        // level: Generator.Generated.DataTypeLevel32
        size += 4;

        // name: Generator.Generated.DataTypeCstring
        size += Name.Length + 1;

        // loyalty: Generator.Generated.DataTypeInteger
        size += 4;

        // slot: Generator.Generated.DataTypeInteger
        size += 1;

        return size;
    }

}

