using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_INITIAL_SPELLS: VanillaServerMessage, IWorldMessage {
    /// <summary>
    /// cmangos/mangoszero: sets to 0
    /// </summary>
    public required byte Unknown1 { get; set; }
    public required List<InitialSpell> InitialSpells { get; set; }
    public required List<CooldownSpell> Cooldowns { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort((ushort)InitialSpells.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in InitialSpells) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUShort((ushort)Cooldowns.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Cooldowns) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 298, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 298, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_INITIAL_SPELLS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var unknown1 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var spellCount = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var initialSpells = new List<InitialSpell>();
        for (var i = 0; i < spellCount; ++i) {
            initialSpells.Add(await Vanilla.InitialSpell.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        // ReSharper disable once UnusedVariable.Compiler
        var cooldownCount = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var cooldowns = new List<CooldownSpell>();
        for (var i = 0; i < cooldownCount; ++i) {
            cooldowns.Add(await Vanilla.CooldownSpell.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_INITIAL_SPELLS {
            Unknown1 = unknown1,
            InitialSpells = initialSpells,
            Cooldowns = cooldowns,
        };
    }

    internal int Size() {
        var size = 0;

        // unknown1: Generator.Generated.DataTypeInteger
        size += 1;

        // spell_count: Generator.Generated.DataTypeInteger
        size += 2;

        // initial_spells: Generator.Generated.DataTypeArray
        size += InitialSpells.Sum(e => 4);

        // cooldown_count: Generator.Generated.DataTypeInteger
        size += 2;

        // cooldowns: Generator.Generated.DataTypeArray
        size += Cooldowns.Sum(e => 14);

        return size;
    }

}

