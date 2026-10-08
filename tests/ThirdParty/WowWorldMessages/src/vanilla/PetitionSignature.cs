using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class PetitionSignature {
    public required ulong Signer { get; set; }
    public required uint Unknown1 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Signer, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<PetitionSignature> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var signer = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new PetitionSignature {
            Signer = signer,
            Unknown1 = unknown1,
        };
    }

}

