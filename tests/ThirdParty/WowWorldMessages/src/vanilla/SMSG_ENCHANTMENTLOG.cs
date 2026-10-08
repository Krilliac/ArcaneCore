using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_ENCHANTMENTLOG: VanillaServerMessage, IWorldMessage {
    public required ulong Target { get; set; }
    /// <summary>
    /// vmangos: message says enchant has faded if empty
    /// </summary>
    public required ulong Caster { get; set; }
    public required uint Item { get; set; }
    public required uint Spell { get; set; }
    /// <summary>
    /// vmangos: Only used if `caster` is not 0.
    /// </summary>
    public required bool ShowAffiliation { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Target, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Caster, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Spell, cancellationToken).ConfigureAwait(false);

        await w.WriteBool8(ShowAffiliation, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 27, 471, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 27, 471, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_ENCHANTMENTLOG> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var target = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var caster = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var spell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var showAffiliation = await r.ReadBool8(cancellationToken).ConfigureAwait(false);

        return new SMSG_ENCHANTMENTLOG {
            Target = target,
            Caster = caster,
            Item = item,
            Spell = spell,
            ShowAffiliation = showAffiliation,
        };
    }

}

