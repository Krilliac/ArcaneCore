using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SPELLLOGMISS: VanillaServerMessage, IWorldMessage {
    public required uint Id { get; set; }
    public required ulong Caster { get; set; }
    /// <summary>
    /// cmangos/mangoszero: can be 0 or 1
    /// </summary>
    public required byte Unknown1 { get; set; }
    public required List<SpellLogMiss> Targets { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Id, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Caster, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Targets.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Targets) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 587, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 587, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SPELLLOGMISS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var id = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var caster = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfTargets = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var targets = new List<SpellLogMiss>();
        for (var i = 0; i < amountOfTargets; ++i) {
            targets.Add(await Vanilla.SpellLogMiss.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_SPELLLOGMISS {
            Id = id,
            Caster = caster,
            Unknown1 = unknown1,
            Targets = targets,
        };
    }

    internal int Size() {
        var size = 0;

        // id: Generator.Generated.DataTypeSpell
        size += 4;

        // caster: Generator.Generated.DataTypeGuid
        size += 8;

        // unknown1: Generator.Generated.DataTypeInteger
        size += 1;

        // amount_of_targets: Generator.Generated.DataTypeInteger
        size += 4;

        // targets: Generator.Generated.DataTypeArray
        size += Targets.Sum(e => 9);

        return size;
    }

}

