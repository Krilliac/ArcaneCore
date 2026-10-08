using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_CHAR_CREATE: VanillaClientMessage, IWorldMessage {
    public required string Name { get; set; }
    public required Vanilla.Race Race { get; set; }
    public required Vanilla.Class ClassType { get; set; }
    public required Vanilla.Gender Gender { get; set; }
    public required byte SkinColor { get; set; }
    public required byte Face { get; set; }
    public required byte HairStyle { get; set; }
    public required byte HairColor { get; set; }
    public required byte FacialHair { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(Name, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Race, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)ClassType, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Gender, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(SkinColor, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Face, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(HairStyle, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(HairColor, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(FacialHair, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(0, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 54, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 54, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_CHAR_CREATE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var name = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var race = (Vanilla.Race)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var classType = (Vanilla.Class)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var gender = (Vanilla.Gender)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var skinColor = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var face = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var hairStyle = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var hairColor = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var facialHair = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var outfitId = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_CHAR_CREATE {
            Name = name,
            Race = race,
            ClassType = classType,
            Gender = gender,
            SkinColor = skinColor,
            Face = face,
            HairStyle = hairStyle,
            HairColor = hairColor,
            FacialHair = facialHair,
        };
    }

    internal int Size() {
        var size = 0;

        // name: Generator.Generated.DataTypeCstring
        size += Name.Length + 1;

        // race: Generator.Generated.DataTypeEnum
        size += 1;

        // class_type: Generator.Generated.DataTypeEnum
        size += 1;

        // gender: Generator.Generated.DataTypeEnum
        size += 1;

        // skin_color: Generator.Generated.DataTypeInteger
        size += 1;

        // face: Generator.Generated.DataTypeInteger
        size += 1;

        // hair_style: Generator.Generated.DataTypeInteger
        size += 1;

        // hair_color: Generator.Generated.DataTypeInteger
        size += 1;

        // facial_hair: Generator.Generated.DataTypeInteger
        size += 1;

        // outfit_id: Generator.Generated.DataTypeInteger
        size += 1;

        return size;
    }

}

