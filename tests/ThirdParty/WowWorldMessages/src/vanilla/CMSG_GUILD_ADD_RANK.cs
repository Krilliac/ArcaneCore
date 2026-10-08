using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GUILD_ADD_RANK: VanillaClientMessage, IWorldMessage {
    public required string RankName { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(RankName, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 562, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 562, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GUILD_ADD_RANK> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var rankName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_GUILD_ADD_RANK {
            RankName = rankName,
        };
    }

    internal int Size() {
        var size = 0;

        // rank_name: Generator.Generated.DataTypeCstring
        size += RankName.Length + 1;

        return size;
    }

}

