using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PETITION_SHOW_SIGNATURES: VanillaServerMessage, IWorldMessage {
    public required ulong Item { get; set; }
    public required ulong Owner { get; set; }
    public required uint Petition { get; set; }
    public required List<PetitionSignature> Signatures { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Owner, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Petition, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Signatures.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Signatures) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 447, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 447, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PETITION_SHOW_SIGNATURES> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var item = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var owner = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var petition = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfSignatures = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var signatures = new List<PetitionSignature>();
        for (var i = 0; i < amountOfSignatures; ++i) {
            signatures.Add(await Vanilla.PetitionSignature.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_PETITION_SHOW_SIGNATURES {
            Item = item,
            Owner = owner,
            Petition = petition,
            Signatures = signatures,
        };
    }

    internal int Size() {
        var size = 0;

        // item: Generator.Generated.DataTypeGuid
        size += 8;

        // owner: Generator.Generated.DataTypeGuid
        size += 8;

        // petition: Generator.Generated.DataTypeInteger
        size += 4;

        // amount_of_signatures: Generator.Generated.DataTypeInteger
        size += 1;

        // signatures: Generator.Generated.DataTypeArray
        size += Signatures.Sum(e => 12);

        return size;
    }

}

