using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GUILD_RANK: VanillaClientMessage, IWorldMessage {
    public required uint RankId { get; set; }
    public required uint Rights { get; set; }
    public required string RankName { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(RankId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Rights, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(RankName, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 561, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 561, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GUILD_RANK> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var rankId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var rights = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var rankName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_GUILD_RANK {
            RankId = rankId,
            Rights = rights,
            RankName = rankName,
        };
    }

    internal int Size() {
        var size = 0;

        // rank_id: Generator.Generated.DataTypeInteger
        size += 4;

        // rights: Generator.Generated.DataTypeInteger
        size += 4;

        // rank_name: Generator.Generated.DataTypeCstring
        size += RankName.Length + 1;

        return size;
    }

}

