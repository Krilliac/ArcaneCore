using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SPELL_UPDATE_CHAIN_TARGETS: VanillaServerMessage, IWorldMessage {
    public required ulong Caster { get; set; }
    public required uint Spell { get; set; }
    public required List<ulong> Targets { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Caster, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Spell, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Targets.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Targets) {
            await w.WriteULong(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 816, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 816, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SPELL_UPDATE_CHAIN_TARGETS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var caster = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var spell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfTargets = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var targets = new List<ulong>();
        for (var i = 0; i < amountOfTargets; ++i) {
            targets.Add(await r.ReadULong(cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_SPELL_UPDATE_CHAIN_TARGETS {
            Caster = caster,
            Spell = spell,
            Targets = targets,
        };
    }

    internal int Size() {
        var size = 0;

        // caster: Generator.Generated.DataTypeGuid
        size += 8;

        // spell: Generator.Generated.DataTypeSpell
        size += 4;

        // amount_of_targets: Generator.Generated.DataTypeInteger
        size += 4;

        // targets: Generator.Generated.DataTypeArray
        size += Targets.Sum(e => 8);

        return size;
    }

}

