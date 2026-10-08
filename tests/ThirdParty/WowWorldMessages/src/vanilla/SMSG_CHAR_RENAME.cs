using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using WorldResultType = OneOf.OneOf<SMSG_CHAR_RENAME.WorldResultResponseSuccess, WorldResult>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_CHAR_RENAME: VanillaServerMessage, IWorldMessage {
    public class WorldResultResponseSuccess {
        public required ulong Character { get; set; }
        public required string NewName { get; set; }
    }
    public required WorldResultType Result { get; set; }
    internal WorldResult ResultValue => Result.Match(
        _ => Vanilla.WorldResult.ResponseSuccess,
        v => v
    );

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)ResultValue, cancellationToken).ConfigureAwait(false);

        if (Result.Value is SMSG_CHAR_RENAME.WorldResultResponseSuccess worldResultResponseSuccess) {
            await w.WriteULong(worldResultResponseSuccess.Character, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(worldResultResponseSuccess.NewName, cancellationToken).ConfigureAwait(false);

        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 712, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 712, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_CHAR_RENAME> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        WorldResultType result = (Vanilla.WorldResult)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (result.Value is Vanilla.WorldResult.ResponseSuccess) {
            var character = await r.ReadULong(cancellationToken).ConfigureAwait(false);

            var newName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

            result = new WorldResultResponseSuccess {
                Character = character,
                NewName = newName,
            };
        }

        return new SMSG_CHAR_RENAME {
            Result = result,
        };
    }

    internal int Size() {
        var size = 0;

        // result: Generator.Generated.DataTypeEnum
        size += 1;

        if (Result.Value is SMSG_CHAR_RENAME.WorldResultResponseSuccess worldResultResponseSuccess) {
            // character: Generator.Generated.DataTypeGuid
            size += 8;

            // new_name: Generator.Generated.DataTypeCstring
            size += worldResultResponseSuccess.NewName.Length + 1;

        }

        return size;
    }

}

