using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class DamageInfo {
    public required uint SpellSchoolMask { get; set; }
    /// <summary>
    /// vmangos sends the same data in `damage_uint`.
    /// </summary>
    public required float DamageFloat { get; set; }
    /// <summary>
    /// vmangos sends the same data in `damage_float`.
    /// </summary>
    public required uint DamageUint { get; set; }
    public required uint Absorb { get; set; }
    public required uint Resist { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(SpellSchoolMask, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(DamageFloat, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(DamageUint, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Absorb, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Resist, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<DamageInfo> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var spellSchoolMask = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var damageFloat = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var damageUint = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var absorb = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var resist = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new DamageInfo {
            SpellSchoolMask = spellSchoolMask,
            DamageFloat = damageFloat,
            DamageUint = damageUint,
            Absorb = absorb,
            Resist = resist,
        };
    }

}

