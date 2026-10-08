using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class ItemSpells {
    public required uint Spell { get; set; }
    public required Vanilla.SpellTriggerType SpellTrigger { get; set; }
    /// <summary>
    /// let the database control the sign here. negative means that the item should be consumed once the charges are consumed.
    /// </summary>
    public required int SpellCharges { get; set; }
    public required int SpellCooldown { get; set; }
    public required uint SpellCategory { get; set; }
    public required int SpellCategoryCooldown { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Spell, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)SpellTrigger, cancellationToken).ConfigureAwait(false);

        await w.WriteInt(SpellCharges, cancellationToken).ConfigureAwait(false);

        await w.WriteInt(SpellCooldown, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(SpellCategory, cancellationToken).ConfigureAwait(false);

        await w.WriteInt(SpellCategoryCooldown, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<ItemSpells> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var spell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var spellTrigger = (Vanilla.SpellTriggerType)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var spellCharges = await r.ReadInt(cancellationToken).ConfigureAwait(false);

        var spellCooldown = await r.ReadInt(cancellationToken).ConfigureAwait(false);

        var spellCategory = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var spellCategoryCooldown = await r.ReadInt(cancellationToken).ConfigureAwait(false);

        return new ItemSpells {
            Spell = spell,
            SpellTrigger = spellTrigger,
            SpellCharges = spellCharges,
            SpellCooldown = spellCooldown,
            SpellCategory = spellCategory,
            SpellCategoryCooldown = spellCategoryCooldown,
        };
    }

}

