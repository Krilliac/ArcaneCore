using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PARTY_COMMAND_RESULT: VanillaServerMessage, IWorldMessage {
    public required Vanilla.PartyOperation Operation { get; set; }
    public required string Member { get; set; }
    public required Vanilla.PartyResult Result { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Operation, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Member, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Result, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 127, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 127, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PARTY_COMMAND_RESULT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var operation = (Vanilla.PartyOperation)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var member = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var result = (Vanilla.PartyResult)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_PARTY_COMMAND_RESULT {
            Operation = operation,
            Member = member,
            Result = result,
        };
    }

    internal int Size() {
        var size = 0;

        // operation: Generator.Generated.DataTypeEnum
        size += 4;

        // member: Generator.Generated.DataTypeCstring
        size += Member.Length + 1;

        // result: Generator.Generated.DataTypeEnum
        size += 4;

        return size;
    }

}

