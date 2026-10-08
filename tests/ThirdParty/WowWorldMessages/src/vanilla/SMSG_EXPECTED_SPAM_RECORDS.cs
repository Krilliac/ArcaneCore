using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_EXPECTED_SPAM_RECORDS: VanillaServerMessage, IWorldMessage {
    public required List<string> Records { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Records.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Records) {
            await w.WriteCString(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 818, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 818, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_EXPECTED_SPAM_RECORDS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfRecords = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var records = new List<string>();
        for (var i = 0; i < amountOfRecords; ++i) {
            records.Add(await r.ReadCString(cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_EXPECTED_SPAM_RECORDS {
            Records = records,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_records: Generator.Generated.DataTypeInteger
        size += 4;

        // records: Generator.Generated.DataTypeArray
        size += Records.Sum(e => e.Length + 1);

        return size;
    }

}

