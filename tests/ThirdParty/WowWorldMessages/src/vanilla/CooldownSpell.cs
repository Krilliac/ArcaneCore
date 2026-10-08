using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class CooldownSpell {
    public required ushort SpellId { get; set; }
    /// <summary>
    /// cmangos/mangoszero: cast item id
    /// </summary>
    public required ushort ItemId { get; set; }
    public required ushort SpellCategory { get; set; }
    public required uint Cooldown { get; set; }
    public required uint CategoryCooldown { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUShort(SpellId, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(ItemId, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(SpellCategory, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Cooldown, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CategoryCooldown, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<CooldownSpell> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var spellId = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var itemId = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var spellCategory = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var cooldown = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var categoryCooldown = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CooldownSpell {
            SpellId = spellId,
            ItemId = itemId,
            SpellCategory = spellCategory,
            Cooldown = cooldown,
            CategoryCooldown = categoryCooldown,
        };
    }

}

