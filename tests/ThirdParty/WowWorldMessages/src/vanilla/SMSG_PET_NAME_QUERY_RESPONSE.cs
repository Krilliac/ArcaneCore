using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PET_NAME_QUERY_RESPONSE: VanillaServerMessage, IWorldMessage {
    public required uint PetNumber { get; set; }
    public required string Name { get; set; }
    public required uint PetNameTimestamp { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(PetNumber, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Name, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(PetNameTimestamp, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 83, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 83, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PET_NAME_QUERY_RESPONSE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var petNumber = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var name = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var petNameTimestamp = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_PET_NAME_QUERY_RESPONSE {
            PetNumber = petNumber,
            Name = name,
            PetNameTimestamp = petNameTimestamp,
        };
    }

    internal int Size() {
        var size = 0;

        // pet_number: Generator.Generated.DataTypeInteger
        size += 4;

        // name: Generator.Generated.DataTypeCstring
        size += Name.Length + 1;

        // pet_name_timestamp: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

