using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_INITIALIZE_FACTIONS: VanillaServerMessage, IWorldMessage {
    public required List<FactionInitializer> Factions { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Factions.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Factions) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 290, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 290, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_INITIALIZE_FACTIONS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfFactions = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var factions = new List<FactionInitializer>();
        for (var i = 0; i < amountOfFactions; ++i) {
            factions.Add(await Vanilla.FactionInitializer.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_INITIALIZE_FACTIONS {
            Factions = factions,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_factions: Generator.Generated.DataTypeInteger
        size += 4;

        // factions: Generator.Generated.DataTypeArray
        size += Factions.Sum(e => 5);

        return size;
    }

}

