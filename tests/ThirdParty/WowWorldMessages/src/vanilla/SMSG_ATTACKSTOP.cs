using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_ATTACKSTOP: VanillaServerMessage, IWorldMessage {
    public required ulong Player { get; set; }
    public required ulong Enemy { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero/arcemu/azerothcore/mangostwo: set to 0 with comment: unk, can be 1 also
    /// </summary>
    public required uint Unknown1 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WritePackedGuid(Player, cancellationToken).ConfigureAwait(false);

        await w.WritePackedGuid(Enemy, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 324, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 324, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_ATTACKSTOP> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var player = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var enemy = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_ATTACKSTOP {
            Player = player,
            Enemy = enemy,
            Unknown1 = unknown1,
        };
    }

    internal int Size() {
        var size = 0;

        // player: Generator.Generated.DataTypePackedGuid
        size += Player.PackedGuidLength();

        // enemy: Generator.Generated.DataTypePackedGuid
        size += Enemy.PackedGuidLength();

        // unknown1: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

