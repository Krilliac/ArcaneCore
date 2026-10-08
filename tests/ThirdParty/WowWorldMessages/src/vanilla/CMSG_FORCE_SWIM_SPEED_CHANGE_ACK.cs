using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_FORCE_SWIM_SPEED_CHANGE_ACK: VanillaClientMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required uint Counter { get; set; }
    public required MovementInfo Info { get; set; }
    public required float NewSpeed { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Counter, cancellationToken).ConfigureAwait(false);

        await Info.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(NewSpeed, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 231, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 231, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_FORCE_SWIM_SPEED_CHANGE_ACK> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var counter = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var info = await MovementInfo.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

        var newSpeed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new CMSG_FORCE_SWIM_SPEED_CHANGE_ACK {
            Guid = guid,
            Counter = counter,
            Info = info,
            NewSpeed = newSpeed,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // counter: Generator.Generated.DataTypeInteger
        size += 4;

        // info: Generator.Generated.DataTypeStruct
        size += Info.Size();

        // new_speed: Generator.Generated.DataTypeFloatingPoint
        size += 4;

        return size;
    }

}

