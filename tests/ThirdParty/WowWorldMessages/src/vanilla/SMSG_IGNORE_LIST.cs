using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_IGNORE_LIST: VanillaServerMessage, IWorldMessage {
    public required List<ulong> Ignored { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Ignored.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Ignored) {
            await w.WriteULong(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 107, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 107, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_IGNORE_LIST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfIgnored = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var ignored = new List<ulong>();
        for (var i = 0; i < amountOfIgnored; ++i) {
            ignored.Add(await r.ReadULong(cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_IGNORE_LIST {
            Ignored = ignored,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_ignored: Generator.Generated.DataTypeInteger
        size += 1;

        // ignored: Generator.Generated.DataTypeArray
        size += Ignored.Sum(e => 8);

        return size;
    }

}

