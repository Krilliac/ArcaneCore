using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class SpellCooldownStatus {
    public required uint Id { get; set; }
    public required uint CooldownTime { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Id, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CooldownTime, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<SpellCooldownStatus> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var id = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var cooldownTime = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SpellCooldownStatus {
            Id = id,
            CooldownTime = cooldownTime,
        };
    }

}

