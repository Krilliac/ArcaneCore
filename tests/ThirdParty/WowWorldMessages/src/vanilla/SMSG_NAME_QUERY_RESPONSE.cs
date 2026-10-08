using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_NAME_QUERY_RESPONSE: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required string CharacterName { get; set; }
    /// <summary>
    /// Used for showing cross realm realm names. If this is an empty string it is shown like a regular player on the same realm.
    /// </summary>
    public required string RealmName { get; set; }
    public required Vanilla.Race Race { get; set; }
    public required Vanilla.Gender Gender { get; set; }
    public required Vanilla.Class ClassType { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(CharacterName, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(RealmName, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Race, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Gender, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)ClassType, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 81, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 81, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_NAME_QUERY_RESPONSE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var characterName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var realmName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var race = (Vanilla.Race)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var gender = (Vanilla.Gender)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var classType = (Vanilla.Class)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_NAME_QUERY_RESPONSE {
            Guid = guid,
            CharacterName = characterName,
            RealmName = realmName,
            Race = race,
            Gender = gender,
            ClassType = classType,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // character_name: Generator.Generated.DataTypeCstring
        size += CharacterName.Length + 1;

        // realm_name: Generator.Generated.DataTypeCstring
        size += RealmName.Length + 1;

        // race: Generator.Generated.DataTypeEnum
        size += 4;

        // gender: Generator.Generated.DataTypeEnum
        size += 4;

        // class_type: Generator.Generated.DataTypeEnum
        size += 4;

        return size;
    }

}

