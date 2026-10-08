using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class PetSpellCooldown {
    public required ushort Spell { get; set; }
    /// <summary>
    /// mangoszero: sets to 0
    /// </summary>
    public required ushort SpellCategory { get; set; }
    public required uint Cooldown { get; set; }
    public required uint CategoryCooldown { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUShort(Spell, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(SpellCategory, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Cooldown, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CategoryCooldown, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<PetSpellCooldown> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var spell = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var spellCategory = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var cooldown = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var categoryCooldown = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new PetSpellCooldown {
            Spell = spell,
            SpellCategory = spellCategory,
            Cooldown = cooldown,
            CategoryCooldown = categoryCooldown,
        };
    }

}

