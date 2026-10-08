using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_LOOT_MASTER_LIST: VanillaServerMessage, IWorldMessage {
    public required List<ulong> Guids { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Guids.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Guids) {
            await w.WriteULong(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 676, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 676, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_LOOT_MASTER_LIST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfPlayers = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var guids = new List<ulong>();
        for (var i = 0; i < amountOfPlayers; ++i) {
            guids.Add(await r.ReadULong(cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_LOOT_MASTER_LIST {
            Guids = guids,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_players: Generator.Generated.DataTypeInteger
        size += 1;

        // guids: Generator.Generated.DataTypeArray
        size += Guids.Sum(e => 8);

        return size;
    }

}

