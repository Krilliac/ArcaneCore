using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class ItemDamageType {
    public required float DamageMinimum { get; set; }
    public required float DamageMaximum { get; set; }
    public required Vanilla.SpellSchool School { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteFloat(DamageMinimum, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(DamageMaximum, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)School, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<ItemDamageType> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var damageMinimum = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var damageMaximum = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var school = (Vanilla.SpellSchool)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new ItemDamageType {
            DamageMinimum = damageMinimum,
            DamageMaximum = damageMaximum,
            School = school,
        };
    }

}

