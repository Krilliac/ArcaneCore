using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_ENVIRONMENTAL_DAMAGE_LOG: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required Vanilla.EnvironmentalDamageType DamageType { get; set; }
    public required uint Damage { get; set; }
    public required uint Absorb { get; set; }
    public required uint Resist { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)DamageType, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Damage, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Absorb, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Resist, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 23, 508, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 23, 508, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_ENVIRONMENTAL_DAMAGE_LOG> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var damageType = (Vanilla.EnvironmentalDamageType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var damage = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var absorb = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var resist = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_ENVIRONMENTAL_DAMAGE_LOG {
            Guid = guid,
            DamageType = damageType,
            Damage = damage,
            Absorb = absorb,
            Resist = resist,
        };
    }

}

