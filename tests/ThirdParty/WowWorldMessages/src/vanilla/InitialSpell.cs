using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class InitialSpell {
    /// <summary>
    /// cmangos/mangoszero: only send 'first' part of spell
    /// </summary>
    public required ushort SpellId { get; set; }
    /// <summary>
    /// cmangos/mangoszero: sets to 0
    /// cmangos/mangoszero: it's not slot id
    /// </summary>
    public required ushort Unknown1 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUShort(SpellId, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(Unknown1, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<InitialSpell> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var spellId = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        return new InitialSpell {
            SpellId = spellId,
            Unknown1 = unknown1,
        };
    }

}

