using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_BATTLEMASTER_JOIN: VanillaClientMessage, IWorldMessage {
    /// <summary>
    /// vmangos: battlemaster guid, or player guid if joining queue from BG portal
    /// </summary>
    public required ulong Guid { get; set; }
    public required Vanilla.Map Map { get; set; }
    /// <summary>
    /// vmangos: 0 if First Available selected
    /// </summary>
    public required uint InstanceId { get; set; }
    public required bool JoinAsGroup { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Map, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(InstanceId, cancellationToken).ConfigureAwait(false);

        await w.WriteBool8(JoinAsGroup, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 21, 750, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 21, 750, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_BATTLEMASTER_JOIN> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var instanceId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var joinAsGroup = await r.ReadBool8(cancellationToken).ConfigureAwait(false);

        return new CMSG_BATTLEMASTER_JOIN {
            Guid = guid,
            Map = map,
            InstanceId = instanceId,
            JoinAsGroup = joinAsGroup,
        };
    }

}

