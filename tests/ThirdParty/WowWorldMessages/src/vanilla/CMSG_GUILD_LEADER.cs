using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GUILD_LEADER: VanillaClientMessage, IWorldMessage {
    public required string NewGuildLeaderName { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(NewGuildLeaderName, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 144, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 144, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GUILD_LEADER> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var newGuildLeaderName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_GUILD_LEADER {
            NewGuildLeaderName = newGuildLeaderName,
        };
    }

    internal int Size() {
        var size = 0;

        // new_guild_leader_name: Generator.Generated.DataTypeCstring
        size += NewGuildLeaderName.Length + 1;

        return size;
    }

}

