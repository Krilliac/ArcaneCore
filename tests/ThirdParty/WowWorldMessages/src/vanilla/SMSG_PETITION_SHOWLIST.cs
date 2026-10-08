using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PETITION_SHOWLIST: VanillaServerMessage, IWorldMessage {
    public required ulong Npc { get; set; }
    public required List<PetitionShowlist> Petitions { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Npc, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Petitions.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Petitions) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 444, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 444, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PETITION_SHOWLIST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var npc = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfPetitions = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var petitions = new List<PetitionShowlist>();
        for (var i = 0; i < amountOfPetitions; ++i) {
            petitions.Add(await Vanilla.PetitionShowlist.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_PETITION_SHOWLIST {
            Npc = npc,
            Petitions = petitions,
        };
    }

    internal int Size() {
        var size = 0;

        // npc: Generator.Generated.DataTypeGuid
        size += 8;

        // amount_of_petitions: Generator.Generated.DataTypeInteger
        size += 1;

        // petitions: Generator.Generated.DataTypeArray
        size += Petitions.Sum(e => 20);

        return size;
    }

}

