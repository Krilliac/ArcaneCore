using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_RESISTLOG: VanillaServerMessage, IWorldMessage {
    public required ulong Guid1 { get; set; }
    public required ulong Guid2 { get; set; }
    public required uint Unknown1 { get; set; }
    public required float Unknown2 { get; set; }
    public required float Unknown3 { get; set; }
    public required uint Unknown4 { get; set; }
    public required uint Unknown5 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid1, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Guid2, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(Unknown2, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(Unknown3, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown4, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown5, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 38, 470, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 38, 470, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_RESISTLOG> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid1 = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var guid2 = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown2 = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var unknown3 = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var unknown4 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown5 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_RESISTLOG {
            Guid1 = guid1,
            Guid2 = guid2,
            Unknown1 = unknown1,
            Unknown2 = unknown2,
            Unknown3 = unknown3,
            Unknown4 = unknown4,
            Unknown5 = unknown5,
        };
    }

}

