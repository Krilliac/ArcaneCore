using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PVP_CREDIT: VanillaServerMessage, IWorldMessage {
    public required uint HonorPoints { get; set; }
    public required ulong Victim { get; set; }
    public required Vanilla.PvpRank Rank { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(HonorPoints, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Victim, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Rank, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 18, 652, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 18, 652, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PVP_CREDIT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var honorPoints = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var victim = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var rank = (Vanilla.PvpRank)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_PVP_CREDIT {
            HonorPoints = honorPoints,
            Victim = victim,
            Rank = rank,
        };
    }

}

